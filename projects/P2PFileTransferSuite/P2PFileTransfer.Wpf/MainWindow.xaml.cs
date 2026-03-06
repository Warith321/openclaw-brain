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
        SetRoleUi(null);
        Log("[SYSTEM] Ready");
    }

    private enum Role
    {
        Sender,
        Receiver
    }

    private void ChooseReceiver_Click(object sender, RoutedEventArgs e) => SetRoleUi(Role.Receiver);

    private void ChooseSender_Click(object sender, RoutedEventArgs e) => SetRoleUi(Role.Sender);

    private void SetRoleUi(Role? role)
    {
        if (role == null)
        {
            ReceiverGroup.IsEnabled = false;
            SenderGroup.IsEnabled = false;
            RoleHintText.Text = "请先选择当前电脑角色。接收端电脑只需要点【启动接收】；发送端电脑填接收端IP后点【发送文件】。";
            return;
        }

        if (role == Role.Receiver)
        {
            ReceiverGroup.IsEnabled = true;
            SenderGroup.IsEnabled = false;
            RoleHintText.Text = "当前模式：接收端。请启动接收，并把本机IP告诉发送端电脑。";
            Log("[SYSTEM] 切换到接收端模式");
        }
        else
        {
            ReceiverGroup.IsEnabled = false;
            SenderGroup.IsEnabled = true;
            RoleHintText.Text = "当前模式：发送端。请填写接收端电脑IP和端口，再选择文件发送。";
            Log("[SYSTEM] 切换到发送端模式");
        }
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

    private void StopReceive_Click(object sender, RoutedEventArgs e)
    {
        if (_receiveCts == null)
        {
            Log("[SYSTEM] 接收端当前未运行");
            return;
        }

        _receiveCts.Cancel();
        _receiveCts.Dispose();
        _receiveCts = null;
        Log("[SYSTEM] 正在停止接收端...");
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
