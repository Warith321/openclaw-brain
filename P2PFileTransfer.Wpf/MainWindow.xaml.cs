using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Forms;

namespace P2PFileTransfer.Wpf;

public partial class MainWindow : Window
{
    private readonly P2PTransferService _service = new();
    private CancellationTokenSource? _receiveCts;

    public MainWindow()
    {
        InitializeComponent();
        Log("[SYSTEM] Ready");
    }

    private async void StartReceive_Click(object sender, RoutedEventArgs e)
    {
        if (_receiveCts != null)
        {
            Log("[SYSTEM] 接收端已在运行");
            return;
        }

        if (!int.TryParse(ReceivePortBox.Text, out var port))
        {
            Log("[ERROR] 监听端口不合法");
            return;
        }

        var saveDir = SaveDirBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(saveDir))
        {
            Log("[ERROR] 保存目录不能为空");
            return;
        }

        _receiveCts = new CancellationTokenSource();
        Log($"[SYSTEM] 启动接收端: {port}, dir={saveDir}");

        try
        {
            await _service.StartReceiverAsync(port, saveDir, Log, _receiveCts.Token);
        }
        catch (OperationCanceledException)
        {
            Log("[SYSTEM] 接收端已停止");
        }
        catch (Exception ex)
        {
            Log($"[ERROR] 接收端异常: {ex.Message}");
        }
        finally
        {
            _receiveCts = null;
        }
    }

    private async void SendFile_Click(object sender, RoutedEventArgs e)
    {
        var host = HostBox.Text.Trim();
        if (!int.TryParse(SendPortBox.Text, out var port))
        {
            Log("[ERROR] 目标端口不合法");
            return;
        }

        var filePath = FilePathBox.Text.Trim();
        if (!File.Exists(filePath))
        {
            Log("[ERROR] 文件不存在");
            return;
        }

        try
        {
            Progress.Value = 0;
            await _service.SendFileAsync(host, port, filePath, p =>
            {
                Dispatcher.Invoke(() => Progress.Value = p);
            }, Log);
        }
        catch (Exception ex)
        {
            Log($"[ERROR] 发送失败: {ex.Message}");
        }
    }

    private void BrowseSaveDir_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new FolderBrowserDialog();
        dialog.SelectedPath = Path.GetFullPath(SaveDirBox.Text.Trim());
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            SaveDirBox.Text = dialog.SelectedPath;
        }
    }

    private void PickFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog();
        if (dialog.ShowDialog() == true)
        {
            FilePathBox.Text = dialog.FileName;
        }
    }

    private void Log(string text)
    {
        Dispatcher.Invoke(() =>
        {
            LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}");
            LogBox.ScrollToEnd();
        });
    }
}
