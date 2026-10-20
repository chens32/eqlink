using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using EqLink.Core;
using Microsoft.Win32;

namespace EqLink.ClientGui;

public partial class MainWindow : Window
{
    public MainViewModel Model { get; }
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _loadedSettings;
    public MainWindow() : this(new MainViewModel()) { }
    public MainWindow(MainViewModel model)
    {
        Model = model; InitializeComponent(); DataContext = model;
        Loaded += async (_, _) => { _timer.Tick += async (_, _) => await RefreshAsync(); _timer.Start(); await RefreshAsync(); };
        Closed += (_, _) => _timer.Stop();
    }
    private async Task RefreshAsync()
    {
        await Model.RefreshAsync();
        if (_loadedSettings || !Model.CanRead) return;
        _loadedSettings = true;
        ModeBox.SelectedIndex = (int)Model.Status.Mode;
        AddressModeBox.SelectedIndex = Model.Status.Address?.Mode == AddressMode.Static ? 1 : 0;
        StaticIpBox.Text = Model.Status.Address?.RequestedIp ?? "";
        if (Model.Status.Imported) { try { await Model.LoadHostsAsync(); } catch { _loadedSettings = false; } }
    }
    private async Task ExecuteAsync(IpcRequest request, bool write = false, string? path = null)
    {
        try
        {
            var reply = await Model.ExecuteAsync(request, write ? r => ControlClient.WriteAsync(r, path) : null);
            if (!reply.Ok) throw new IOException(reply.Error ?? "操作失败");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "EqLink", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private async void Connect_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(new("connect"));
    private async void Disconnect_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(new("disconnect"));
    private async void Protection_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(new("protection-set", ProtectionEnabled: !(Model.Status.Protection?.Enabled ?? true)), true);
    private async void ApplyMode_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(new("mode", Mode: (ModeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Auto"), true);
    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "EqLink 通用入网包 (*.eqlink)|*.eqlink", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) await ExecuteAsync(new("import"), true, dialog.FileName);
        _loadedSettings = false;
    }
    private void AddressMode_Changed(object sender, SelectionChangedEventArgs e)
    { if (StaticIpBox is not null) StaticIpBox.IsEnabled = AddressModeBox.SelectedIndex == 1; }
    private async void AddressSave_Click(object sender, RoutedEventArgs e)
    {
        var preference = AddressModeBox.SelectedIndex == 1 ? new AddressPreference(AddressMode.Static, StaticIpBox.Text.Trim()) : new AddressPreference();
        try { preference.Validate(Model.Status.Protection?.Prefix ?? ""); }
        catch { MessageBox.Show(this, "请输入当前虚拟 /24 网段内的 .10～.254 地址。", "EqLink"); return; }
        await ExecuteAsync(new("address-set", Address: preference), true);
    }
    private void Egress_Click(object sender, RoutedEventArgs e)
    {
        var window = new EgressWindow(r => r.Command == "egress-set" ? ControlClient.WriteAsync(r) : ControlClient.SendAsync(r)) { Owner = this };
        window.ShowDialog();
    }
    private async void HostsSave_Click(object sender, RoutedEventArgs e)
    {
        try { await Model.SaveHostsAsync(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "EqLink · 域名覆写", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private async void HostsReload_Click(object sender, RoutedEventArgs e)
    {
        try { await Model.LoadHostsAsync(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "EqLink · 域名覆写", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private async void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "EqLink 诊断 (*.json)|*.json", FileName = "eqlink-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var reply = await Model.ExecuteAsync(new("diagnostics"));
            if (!reply.Ok || reply.Diagnostics is null) throw new IOException(reply.Error ?? "诊断不可用");
            await File.WriteAllTextAsync(dialog.FileName, JsonSerializer.Serialize(reply.Diagnostics, new JsonSerializerOptions { WriteIndented = true }));
            MessageBox.Show(this, "诊断已导出。", "EqLink");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "EqLink", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
