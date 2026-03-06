using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

const int ChunkSize = 1024 * 1024; // 1MB

if (args.Length == 0)
{
    PrintHelp();
    return;
}

var cmd = args[0].ToLowerInvariant();

try
{
    switch (cmd)
    {
        case "receive":
            // receive <port> <saveDir>
            if (args.Length < 3) { PrintHelp(); return; }
            await RunReceiverAsync(int.Parse(args[1]), args[2]);
            break;

        case "send":
            // send <host> <port> <filePath>
            if (args.Length < 4) { PrintHelp(); return; }
            await RunSenderAsync(args[1], int.Parse(args[2]), args[3]);
            break;

        default:
            PrintHelp();
            break;
    }
}
catch (Exception ex)
{
    Console.WriteLine($"[ERROR] {ex.Message}");
}

static void PrintHelp()
{
    Console.WriteLine("P2PFileTransfer (.NET 8)");
    Console.WriteLine("Usage:");
    Console.WriteLine("  receive <port> <saveDir>");
    Console.WriteLine("  send <host> <port> <filePath>");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  dotnet run -- receive 9000 ./recv");
    Console.WriteLine("  dotnet run -- send 127.0.0.1 9000 ./test.zip");
}

static async Task RunReceiverAsync(int port, string saveDir)
{
    Directory.CreateDirectory(saveDir);

    var listener = new TcpListener(IPAddress.Any, port);
    listener.Start();
    Console.WriteLine($"[RECEIVER] Listening on 0.0.0.0:{port}");

    while (true)
    {
        var client = await listener.AcceptTcpClientAsync();
        _ = Task.Run(() => HandleClientAsync(client, saveDir));
    }
}

static async Task HandleClientAsync(TcpClient client, string saveDir)
{
    using var _ = client;
    using var stream = client.GetStream();

    var info = await ReadJsonAsync<FileInfoMsg>(stream) ?? throw new InvalidDataException("Invalid FILE_INFO");
    var safeName = Path.GetFileName(info.FileName);
    var targetPath = Path.Combine(saveDir, safeName);
    var metaPath = targetPath + ".meta";

    var received = LoadMeta(metaPath);

    await WriteJsonAsync(stream, new ResumeMsg(received.ToList()));

    Console.WriteLine($"[RECEIVER] Start {safeName} chunks={info.ChunkCount}");

    await using var fs = new FileStream(targetPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);

    while (true)
    {
        var frame = await ReadFrameAsync(stream);
        if (frame == null) break;

        var header = JsonSerializer.Deserialize<ChunkHeader>(frame.Value.header)!;

        if (header.Type == "FINISH")
            break;

        if (header.Type != "CHUNK")
            throw new InvalidDataException("Unknown frame type");

        if (!received.Contains(header.Index))
        {
            fs.Seek((long)header.Index * ChunkSize, SeekOrigin.Begin);
            await fs.WriteAsync(frame.Value.payload);
            received.Add(header.Index);
            SaveMeta(metaPath, received);
        }

        await WriteJsonAsync(stream, new AckMsg(header.Index));
    }

    // verify hash
    fs.Flush(true);
    var hash = await ComputeSha256Async(targetPath);
    var ok = string.Equals(hash, info.Sha256, StringComparison.OrdinalIgnoreCase);

    if (ok)
    {
        if (File.Exists(metaPath)) File.Delete(metaPath);
        Console.WriteLine($"[RECEIVER] DONE {safeName}, SHA256 OK");
    }
    else
    {
        Console.WriteLine($"[RECEIVER] HASH MISMATCH! expected={info.Sha256} got={hash}");
    }
}

static async Task RunSenderAsync(string host, int port, string filePath)
{
    if (!File.Exists(filePath)) throw new FileNotFoundException(filePath);

    var fileName = Path.GetFileName(filePath);
    var fileSize = new FileInfo(filePath).Length;
    var chunkCount = (int)Math.Ceiling(fileSize / (double)ChunkSize);
    var sha256 = await ComputeSha256Async(filePath);

    using var client = new TcpClient();
    await client.ConnectAsync(host, port);
    using var stream = client.GetStream();

    var info = new FileInfoMsg(fileName, fileSize, ChunkSize, chunkCount, sha256);
    await WriteJsonAsync(stream, info);

    var resume = await ReadJsonAsync<ResumeMsg>(stream) ?? new ResumeMsg(new List<int>());
    var doneSet = new HashSet<int>(resume.DoneChunks);

    Console.WriteLine($"[SENDER] Connected. Resume chunks={doneSet.Count}/{chunkCount}");

    await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);

    for (int i = 0; i < chunkCount; i++)
    {
        if (doneSet.Contains(i)) continue;

        var offset = (long)i * ChunkSize;
        var size = (int)Math.Min(ChunkSize, fileSize - offset);

        var buffer = new byte[size];
        fs.Seek(offset, SeekOrigin.Begin);
        await fs.ReadExactlyAsync(buffer.AsMemory(0, size));

        var header = new ChunkHeader("CHUNK", i, size);
        await WriteFrameAsync(stream, JsonSerializer.SerializeToUtf8Bytes(header), buffer);

        var ack = await ReadJsonAsync<AckMsg>(stream);
        if (ack?.Index != i)
            throw new IOException($"ACK mismatch. expected={i}, got={ack?.Index}");

        var pct = ((i + 1) * 100.0) / chunkCount;
        Console.WriteLine($"[SENDER] chunk {i + 1}/{chunkCount} ({pct:F1}%)");
    }

    await WriteFrameAsync(stream, JsonSerializer.SerializeToUtf8Bytes(new ChunkHeader("FINISH", -1, 0)), Array.Empty<byte>());
    Console.WriteLine("[SENDER] DONE");
}

// ===== Protocol helpers =====
// JSON envelope: [4-byte length][json bytes]
static async Task WriteJsonAsync<T>(NetworkStream stream, T obj)
{
    var data = JsonSerializer.SerializeToUtf8Bytes(obj);
    var len = new byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(len, data.Length);
    await stream.WriteAsync(len);
    await stream.WriteAsync(data);
}

static async Task<T?> ReadJsonAsync<T>(NetworkStream stream)
{
    var lenBuf = await ReadExactOrNullAsync(stream, 4);
    if (lenBuf == null) return default;

    var len = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
    var payload = await ReadExactOrNullAsync(stream, len);
    if (payload == null) return default;

    return JsonSerializer.Deserialize<T>(payload);
}

// binary frame: [4-byte headerLen][headerJson][4-byte payloadLen][payload]
static async Task WriteFrameAsync(NetworkStream stream, byte[] headerJson, byte[] payload)
{
    var b4 = new byte[4];

    BinaryPrimitives.WriteInt32LittleEndian(b4, headerJson.Length);
    await stream.WriteAsync(b4);
    await stream.WriteAsync(headerJson);

    BinaryPrimitives.WriteInt32LittleEndian(b4, payload.Length);
    await stream.WriteAsync(b4);
    if (payload.Length > 0)
        await stream.WriteAsync(payload);
}

static async Task<(byte[] header, byte[] payload)?> ReadFrameAsync(NetworkStream stream)
{
    var headerLenBuf = await ReadExactOrNullAsync(stream, 4);
    if (headerLenBuf == null) return null;

    var headerLen = BinaryPrimitives.ReadInt32LittleEndian(headerLenBuf);
    var header = await ReadExactOrNullAsync(stream, headerLen) ?? throw new EndOfStreamException();

    var payloadLenBuf = await ReadExactOrNullAsync(stream, 4) ?? throw new EndOfStreamException();
    var payloadLen = BinaryPrimitives.ReadInt32LittleEndian(payloadLenBuf);
    var payload = payloadLen == 0 ? Array.Empty<byte>() : (await ReadExactOrNullAsync(stream, payloadLen) ?? throw new EndOfStreamException());

    return (header, payload);
}

static async Task<byte[]?> ReadExactOrNullAsync(Stream stream, int n)
{
    var buf = new byte[n];
    var read = 0;
    while (read < n)
    {
        var r = await stream.ReadAsync(buf.AsMemory(read, n - read));
        if (r == 0) return read == 0 ? null : throw new EndOfStreamException();
        read += r;
    }
    return buf;
}

static HashSet<int> LoadMeta(string metaPath)
{
    if (!File.Exists(metaPath)) return new HashSet<int>();
    var txt = File.ReadAllText(metaPath);
    var arr = JsonSerializer.Deserialize<List<int>>(txt) ?? new List<int>();
    return new HashSet<int>(arr);
}

static void SaveMeta(string metaPath, HashSet<int> done)
{
    File.WriteAllText(metaPath, JsonSerializer.Serialize(done.OrderBy(x => x).ToArray()));
}

static async Task<string> ComputeSha256Async(string filePath)
{
    await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
    var hash = await SHA256.HashDataAsync(fs);
    return Convert.ToHexString(hash).ToLowerInvariant();
}

record FileInfoMsg(string FileName, long FileSize, int ChunkSize, int ChunkCount, string Sha256);
record ResumeMsg(List<int> DoneChunks);
record AckMsg(int Index);
record ChunkHeader(string Type, int Index, int Size);
