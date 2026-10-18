using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using EqLink.Core;
using Microsoft.Win32;

namespace EqLink.ClientGui;

public partial class MainWindow : Window
{
    private string? _bundlePath;
    private bool _protectionEnabled = true;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            _timer.Tick += async (_, _) => await RefreshAsync();
            _timer.Start();
            await RefreshAsync();
        };
        Closed += (_, _) => _timer.Stop();
    }

    private async Task<IpcResponse> SendAsync(IpcRequest request)
    {
        await using var pipe = new NamedPipeClientStream(".", "EqLink.Control.v1", PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(3000);
        var payload = JsonSerializer.SerializeToUtf8Bytes(request, Json);
        await pipe.WriteAsync(payload);
        await pipe.WriteAsync("\n"u8.ToArray());
        using var reader = new StreamReader(pipe, Encoding.UTF8);
        var line = await reader.ReadLineAsync() ?? throw new IOException("服务未返回状态");
        return JsonSerializer.Deserialize<IpcResponse>(line, Json) ?? throw new IOException("服务返回无效状态");
    }

    private async Task RefreshAsync()
    {
        try { Show((await SendAsync(new IpcRequest("status"))).Status); }
        catch (Exception ex) { ConnectionText.Text = "后台服务不可用"; MessageText.Text = ex.Message; }
    }

    private void Show(ClientStatus s)
    {
        ConnectionText.Text = s.Connected ? "已连接" : s.Phase switch { "Connecting" => "正在连接", "Reconnecting" => "正在重连", "Verifying" => "正在验证虚拟网络", "WaitingForEgress" => "等待出口恢复", "Blocked" => "网络已阻断", "Error" => "连接异常", _ => "未连接" };
        DeviceText.Text = "设备：" + (s.Imported ? s.DeviceName : "未导入");
        AddressText.Text = "虚拟 IP：" + (s.Imported ? s.VirtualIp : "—");
        PathText.Text = "当前路径：" + (s.Path switch { ActivePath.Udp => "Hysteria 2 / UDP", ActivePath.Tcp => "VLESS + XHTTP + REALITY / TCP", _ => "—" });
        MessageText.Text = s.Message + (s.ErrorCode is null ? "" : "\n错误代码：" + s.ErrorCode);
        EgressText.Text = "出口网卡：" + s.Egress;
        _protectionEnabled = s.Protection?.Enabled ?? true;
        ProtectionText.Text = "虚拟网段保护：" + (s.Protection is null ? "后台版本尚不支持" : !s.Protection.Enabled ? "管理员已关闭" : s.Protection.Error is not null ? "异常，连接受限" : s.Protection.Allowing ? "已启用，仅经 EqLink 适配器放行" : s.Protection.Installed ? "已启用，当前保持阻断" : "等待建立");
        ProtectionButton.Content = _protectionEnabled ? "关闭网段保护" : "启用网段保护";
        for (var i = 0; i < ModeBox.Items.Count; i++)
            if (ModeBox.Items[i] is ComboBoxItem item && (string)item.Tag == s.Mode.ToString()) ModeBox.SelectedIndex = i;
    }

    private async Task ExecuteAsync(IpcRequest request)
    {
        try
        {
            var response = await SendAsync(request);
            Show(response.Status);
            if (!response.Ok) MessageBox.Show(response.Error ?? "操作失败", "EqLink", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "EqLink", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(new IpcRequest("connect"));
    private async void Disconnect_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(new IpcRequest("disconnect"));
    private async void Protection_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(new IpcRequest("protection-set", ProtectionEnabled: !_protectionEnabled));
    private async void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "EqLink 诊断 (*.json)|*.json", FileName = "eqlink-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var response = await SendAsync(new IpcRequest("diagnostics"));
            if (!response.Ok || response.Diagnostics is null) throw new IOException(response.Error ?? "后台服务尚不支持诊断导出");
            await File.WriteAllTextAsync(dialog.FileName, JsonSerializer.Serialize(response.Diagnostics, new JsonSerializerOptions { WriteIndented = true }));
            MessageBox.Show("诊断已导出，内容不包含设备凭据或私钥。", "EqLink");
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "EqLink", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private async void Egress_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new EgressWindow(SendAsync) { Owner = this };
        if (dialog.ShowDialog() == true) await RefreshAsync();
    }
    private async void ApplyMode_Click(object sender, RoutedEventArgs e)
    {
        var mode = (ModeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Auto";
        await ExecuteAsync(new IpcRequest("mode", Mode: mode));
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "EqLink 设备包 (*.eqlink)|*.eqlink|所有文件 (*.*)|*.*", CheckFileExists = true };
        if (dialog.ShowDialog() == true) { _bundlePath = dialog.FileName; BundleText.Text = _bundlePath; }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_bundlePath is null) { MessageBox.Show("请先选择设备包"); return; }
        var passphrase = PassphraseBox.Password;
        PassphraseBox.Clear();
        try
        {
            var bundle = await File.ReadAllBytesAsync(_bundlePath);
            if (bundle.Length > 700_000) throw new InvalidDataException("导入包过大");
            await ExecuteAsync(new IpcRequest("import", BundleBase64: Convert.ToBase64String(bundle), Passphrase: passphrase));
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "EqLink", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
