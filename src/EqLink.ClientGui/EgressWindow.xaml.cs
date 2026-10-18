using System.Windows;
using System.Windows.Controls;
using EqLink.Core;

namespace EqLink.ClientGui;

public partial class EgressWindow : Window
{
    private sealed record AdapterChoice(string? Id, string Label, string Details);
    private readonly Func<IpcRequest, Task<IpcResponse>> _send;
    private bool _busy, _locked, _loaded;

    public EgressWindow(Func<IpcRequest, Task<IpcResponse>> send)
    {
        InitializeComponent();
        _send = send;
        Loaded += async (_, _) => await LoadAsync(false);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        RefreshButton.IsEnabled = !busy;
        InterfaceBox.IsEnabled = _loaded && !busy && !_locked;
        SaveButton.IsEnabled = _loaded && !busy && !_locked;
        DnsBox.IsEnabled = InterfaceBox.IsEnabled && (InterfaceBox.SelectedItem as AdapterChoice)?.Id is not null;
    }

    private async Task LoadAsync(bool preserveSelection)
    {
        var previous = InterfaceBox.SelectedItem as AdapterChoice;
        var previousDns = DnsBox.Text;
        SetBusy(true);
        try
        {
            var response = await _send(new IpcRequest("egress-get"));
            if (!response.Ok) throw new InvalidOperationException(response.Error ?? "读取出口设置失败。");
            var configuration = response.Egress ?? throw new InvalidOperationException("后台服务版本不支持出口设置，请升级 EqLink 客户端。");
            var selected = preserveSelection && previous is not null ? previous.Id : configuration.Settings?.InterfaceId;
            var choices = new List<AdapterChoice> { new(null, "系统路由（自动选择）", "EqLink 使用 Windows 当前的路由和 DNS 设置。") };
            foreach (var adapter in configuration.Interfaces)
            {
                var address = adapter.Ipv4Addresses.Length == 0 ? "未配置 IPv4" : string.Join(", ", adapter.Ipv4Addresses);
                choices.Add(new AdapterChoice(adapter.Id, $"{adapter.Name} · {address} · {(adapter.IsUp ? "已连接" : "未连接")}", adapter.Description));
            }
            if (selected is not null && !choices.Any(c => string.Equals(c.Id, selected, StringComparison.OrdinalIgnoreCase)))
                choices.Add(new AdapterChoice(selected, "已配置的网卡未找到", selected + "；请重新选择网卡，否则连接将失败。"));
            InterfaceBox.ItemsSource = choices;
            InterfaceBox.SelectedItem = choices.First(c => string.Equals(c.Id, selected, StringComparison.OrdinalIgnoreCase));
            DnsBox.Text = preserveSelection && previous is not null ? previousDns
                : string.Join(", ", configuration.Settings?.DnsServers ?? ["223.5.5.5"]);
            _locked = configuration.Locked;
            _loaded = true;
            ResultText.Text = configuration.Error ?? (_locked ? "连接正在运行或重连。请先关闭此窗口并断开连接，再保存出口设置。" : "");
        }
        catch (Exception ex)
        {
            _loaded = false;
            ResultText.Text = ex.Message;
        }
        finally { SetBusy(false); }
    }

    private void InterfaceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        InterfaceDetails.Text = (InterfaceBox.SelectedItem as AdapterChoice)?.Details ?? "";
        DnsBox.IsEnabled = _loaded && !_busy && !_locked && (InterfaceBox.SelectedItem as AdapterChoice)?.Id is not null;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync(true);

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _locked || !_loaded) return;
        SetBusy(true);
        try
        {
            var selection = InterfaceBox.SelectedItem as AdapterChoice ?? throw new InvalidOperationException("请选择出口网卡。");
            PhysicalEgress.Settings? settings = null;
            if (selection.Id is not null)
            {
                var dns = DnsBox.Text.Split([',', '，', ';', '；', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                settings = EgressSettingsStore.Validate(new PhysicalEgress.Settings(selection.Id, dns));
            }
            var response = await _send(new IpcRequest("egress-set", Egress: settings));
            if (!response.Ok) throw new InvalidOperationException(response.Error ?? "保存出口设置失败。");
            if (IsVisible) DialogResult = true;
        }
        catch (Exception ex) { ResultText.Text = ex.Message; }
        finally { SetBusy(false); }
    }
}
