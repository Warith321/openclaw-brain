# P2PFileTransfer (C# / .NET 8)

基于 Socket(TCP) 的 P2P 文件传输最小项目，支持：
- 文件分块传输（1MB/chunk）
- 断点续传（`.meta`）
- SHA-256 完整性校验

## 运行

### 1) 启动接收端
```bash
dotnet run -- receive 9000 ./recv
```

### 2) 启动发送端
```bash
dotnet run -- send 127.0.0.1 9000 ./test.zip
```

> 跨机器时，把 `127.0.0.1` 改成接收端机器 IP，并放通端口（如 9000）。

## 参数
- `receive <port> <saveDir>`
- `send <host> <port> <filePath>`

## 说明
- 接收端会在目标目录生成临时续传文件 `xxx.meta`。
- 传输成功且哈希校验通过后会自动删除 `.meta`。
