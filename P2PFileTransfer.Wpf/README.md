# P2PFileTransfer.Wpf

WPF 图形界面版（.NET 8）。

## 功能
- 启动接收端（监听端口 + 保存目录）
- 发送文件（IP + 端口 + 文件）
- 进度条显示
- 断点续传（`.meta`）
- SHA-256 完整性校验

## 运行
```bash
cd P2PFileTransfer.Wpf
dotnet run
```

## 使用
1. 在接收端机器点“启动接收”。
2. 在发送端机器填目标 IP/端口，选择文件，点“发送文件”。
