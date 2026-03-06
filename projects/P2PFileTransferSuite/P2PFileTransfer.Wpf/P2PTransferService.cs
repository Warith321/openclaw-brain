using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace P2PFileTransfer.Wpf;

public sealed class P2PTransferService
{
    private const int ChunkSize = 1024 * 1024;

    public async Task StartReceiverAsync(int port, string saveDir, Action<string> log, CancellationToken ct)
    {
        Directory.CreateDirectory(saveDir);
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        log($"[RECEIVER] Listening on 0.0.0.0:{port}");

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleClientAsync(client, saveDir, log), ct);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleClientAsync(TcpClient client, string saveDir, Action<string> log)
    {
        using var _ = client;
        using var stream = client.GetStream();

        var info = await ReadJsonAsync<FileInfoMsg>(stream) ?? throw new InvalidDataException("Invalid FILE_INFO");
        var safeName = Path.GetFileName(info.FileName);
        var targetPath = Path.Combine(saveDir, safeName);
        var metaPath = targetPath + ".meta";

        var received = LoadMeta(metaPath);
        await WriteJsonAsync(stream, new ResumeMsg(received.ToList()));

        await using var fs = new FileStream(targetPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        log($"[RECEIVER] Start {safeName}, resume={received.Count}/{info.ChunkCount}");

        while (true)
        {
            var frame = await ReadFrameAsync(stream);
            if (frame == null) break;

            var header = JsonSerializer.Deserialize<ChunkHeader>(frame.Value.header)!;
            if (header.Type == "FINISH") break;
            if (header.Type != "CHUNK") throw new InvalidDataException("Unknown frame type");

            if (!received.Contains(header.Index))
            {
                fs.Seek((long)header.Index * ChunkSize, SeekOrigin.Begin);
                await fs.WriteAsync(frame.Value.payload);
                received.Add(header.Index);
                SaveMeta(metaPath, received);
            }

            await WriteJsonAsync(stream, new AckMsg(header.Index));
        }

        fs.Flush(true);
        var hash = await ComputeSha256Async(targetPath);
        if (string.Equals(hash, info.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(metaPath)) File.Delete(metaPath);
            log($"[RECEIVER] DONE {safeName}, SHA256 OK");
        }
        else
        {
            log($"[RECEIVER] HASH MISMATCH expected={info.Sha256}, got={hash}");
        }
    }

    public async Task SendFileAsync(string host, int port, string filePath, Action<double> progress, Action<string> log)
    {
        if (!File.Exists(filePath)) throw new FileNotFoundException(filePath);

        var fileName = Path.GetFileName(filePath);
        var fileSize = new FileInfo(filePath).Length;
        var chunkCount = (int)Math.Ceiling(fileSize / (double)ChunkSize);
        var sha256 = await ComputeSha256Async(filePath);

        using var client = new TcpClient();
        await client.ConnectAsync(host, port);
        using var stream = client.GetStream();

        await WriteJsonAsync(stream, new FileInfoMsg(fileName, fileSize, ChunkSize, chunkCount, sha256));
        var resume = await ReadJsonAsync<ResumeMsg>(stream) ?? new ResumeMsg(new List<int>());
        var done = new HashSet<int>(resume.DoneChunks);

        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        log($"[SENDER] Connected {host}:{port}, resume={done.Count}/{chunkCount}");

        for (int i = 0; i < chunkCount; i++)
        {
            if (done.Contains(i))
            {
                progress((i + 1) * 100.0 / chunkCount);
                continue;
            }

            var offset = (long)i * ChunkSize;
            var size = (int)Math.Min(ChunkSize, fileSize - offset);
            var buffer = new byte[size];

            fs.Seek(offset, SeekOrigin.Begin);
            await fs.ReadExactlyAsync(buffer.AsMemory(0, size));

            await WriteFrameAsync(stream, JsonSerializer.SerializeToUtf8Bytes(new ChunkHeader("CHUNK", i, size)), buffer);

            var ack = await ReadJsonAsync<AckMsg>(stream);
            if (ack?.Index != i) throw new IOException($"ACK mismatch expected={i}, got={ack?.Index}");

            var pct = (i + 1) * 100.0 / chunkCount;
            progress(pct);
            log($"[SENDER] chunk {i + 1}/{chunkCount} ({pct:F1}%)");
        }

        await WriteFrameAsync(stream, JsonSerializer.SerializeToUtf8Bytes(new ChunkHeader("FINISH", -1, 0)), Array.Empty<byte>());
        progress(100);
        log("[SENDER] DONE");
    }

    private static async Task WriteJsonAsync<T>(NetworkStream stream, T obj)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(obj);
        var len = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(len, data.Length);
        await stream.WriteAsync(len);
        await stream.WriteAsync(data);
    }

    private static async Task<T?> ReadJsonAsync<T>(NetworkStream stream)
    {
        var lenBuf = await ReadExactOrNullAsync(stream, 4);
        if (lenBuf == null) return default;

        var len = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
        var payload = await ReadExactOrNullAsync(stream, len);
        if (payload == null) return default;

        return JsonSerializer.Deserialize<T>(payload);
    }

    private static async Task WriteFrameAsync(NetworkStream stream, byte[] headerJson, byte[] payload)
    {
        var b4 = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b4, headerJson.Length);
        await stream.WriteAsync(b4);
        await stream.WriteAsync(headerJson);

        BinaryPrimitives.WriteInt32LittleEndian(b4, payload.Length);
        await stream.WriteAsync(b4);
        if (payload.Length > 0) await stream.WriteAsync(payload);
    }

    private static async Task<(byte[] header, byte[] payload)?> ReadFrameAsync(NetworkStream stream)
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

    private static async Task<byte[]?> ReadExactOrNullAsync(Stream stream, int n)
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

    private static HashSet<int> LoadMeta(string metaPath)
    {
        if (!File.Exists(metaPath)) return new HashSet<int>();
        var txt = File.ReadAllText(metaPath);
        var arr = JsonSerializer.Deserialize<List<int>>(txt) ?? new List<int>();
        return new HashSet<int>(arr);
    }

    private static void SaveMeta(string metaPath, HashSet<int> done)
    {
        File.WriteAllText(metaPath, JsonSerializer.Serialize(done.OrderBy(x => x).ToArray()));
    }

    private static async Task<string> ComputeSha256Async(string filePath)
    {
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = await SHA256.HashDataAsync(fs);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed record FileInfoMsg(string FileName, long FileSize, int ChunkSize, int ChunkCount, string Sha256);
    private sealed record ResumeMsg(List<int> DoneChunks);
    private sealed record AckMsg(int Index);
    private sealed record ChunkHeader(string Type, int Index, int Size);
}
