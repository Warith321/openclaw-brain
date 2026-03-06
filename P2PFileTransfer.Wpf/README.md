# P2PFileTransfer.Wpf（Visual Studio 桌面应用）

WPF 图形界面版（.NET 8）。

## 核心交互
启动后先选角色：
- **我是接收端电脑**：启动监听，等待另一台电脑发文件
- **我是发送端电脑**：填接收端IP+端口，选择文件发送

> 两台电脑都运行这个程序，各自选不同角色即可。

## 运行（命令行）
```bash
cd P2PFileTransfer.Wpf
dotnet run
```

## 在 Visual Studio 中运行
1. 打开 `P2PFileTransfer.Wpf.csproj`
2. 直接点“启动”即可

## 在 Visual Studio 中发布给别人用（推荐）
1. 右键项目 → **发布(Publish)**
2. 选择 **Folder**
3. Deployment Mode 选 **Self-contained**
4. Target Runtime 选 **win-x64**
5. Publish 后把输出目录打包给对方，对方直接运行 exe

## 已支持
- 角色选择（发送端 / 接收端）
- 分块传输（1MB/chunk）
- 断点续传（`.meta`）
- SHA-256 完整性校验
- 发送进度条 + 实时日志
