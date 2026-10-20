using System.ComponentModel;
using System.Runtime.CompilerServices;
using EqLink.Core;

namespace EqLink.ClientGui;

public sealed class MainViewModel(Func<IpcRequest, Task<IpcResponse>>? transport = null) : INotifyPropertyChanged
{
    private readonly Func<IpcRequest, Task<IpcResponse>> _send = transport ?? ControlClient.SendAsync;
    private readonly SemaphoreSlim _requests = new(1, 1);
    private bool _busy, _available;
    private ClientStatus _status = new(false, false, "", "", TransportMode.Auto, ActivePath.None, "请选择通用入网包，开始加入网络。");
    public event PropertyChangedEventHandler? PropertyChanged;
    public ClientStatus Status => _status;
    public bool Busy => _busy;
    public bool CanConfigure => !_busy && _available && _status.Phase is "Disconnected" or "Error";
    public bool CanConnect => !_busy && _available && _status.Imported && _status.Phase is "Disconnected" or "Error";
    public bool CanDisconnect => !_busy && _available && _status.Phase is not ("Disconnected" or "Error");
    public bool CanRead => !_busy && _available;
    public string StateText => !_available ? "后台服务不可用" : _status.Connected ? "已连接" : _status.Phase switch
    {
        "Enrolling" => "正在登记设备", "Configuring" => "正在申请地址", "Connecting" => "正在连接", "Reconnecting" => "正在重连",
        "Verifying" => "正在验证网络", "WaitingForEgress" => "等待出口恢复", "Blocked" => "网段已阻断", "Error" => "需要处理", _ => "未连接"
    };
    public string StateColor => _status.Connected && _available ? "#087F6B" : _status.Phase == "Error" || !_available ? "#B23C30" : "#485B78";
    public string Server => _status.Server ?? "尚未导入";
    public string Device => string.IsNullOrEmpty(_status.DeviceName) ? "待登记" : _status.DeviceName;
    public string VirtualIp => string.IsNullOrEmpty(_status.VirtualIp) ? "待服务器分配" : _status.VirtualIp;
    public string Path => _status.Path switch { ActivePath.Udp => "Hysteria 2 · UDP", ActivePath.Tcp => "VLESS · XHTTP · REALITY", _ => "—" };
    public string Egress => _status.Egress;
    public string Registration => !_status.Imported ? "尚未导入" : _status.Registration == "Registered" ? "已登记 · 本机独立身份" : "已导入，待登记";
    public string Protection => _status.Protection is not { } p ? "正在读取" : !p.Enabled ? "管理员已关闭" : p.Error is not null ? "异常 · 连接受限" : p.Allowing ? "已保护 · 仅经 EqLink 放行" : p.Installed ? "已保护 · 当前阻断" : "等待建立";
    public string ProtectionAction => _status.Protection?.Enabled == false ? "启用网段保护" : "关闭网段保护";
    public string Message => _status.Message;
    public string ErrorCode => _status.ErrorCode ?? "";
    public string HealthWarning => _status.HealthWarning ?? "";
    public string TcpConnections => _status.Metrics?.ActiveTcpConnections?.ToString() ?? "—";
    public string Upload => Rate(_status.Metrics?.UploadBytesPerSecond);
    public string Download => Rate(_status.Metrics?.DownloadBytesPerSecond);
    public string MetricDetails => _status.Metrics?.Error is { } code ? code switch
    { "WAITING_FOR_SAMPLE" => "正在取得首个速率样本", _ => "统计采集异常：" + code } : "虚拟网卡流量 · 约 3 秒平均 · 不含外层开销";
    public string VersionLabel => typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) + " 测试版";
    private HostsOverrideSettings _hostsSettings = HostsOverrideSettings.Empty;
    private string _hostsText = "", _hostsResult = "";
    private bool _hostsEnabled;
    public string HostsText { get => _hostsText; set { _hostsText = value; Changed(); } }
    public bool HostsEnabled { get => _hostsEnabled; set { _hostsEnabled = value; Changed(); } }
    public string HostsResult => _hostsResult;
    public bool CanEditHosts => CanRead && _status.Imported;
    public string HostsState => _status.Hosts is not { } hosts ? "尚未启用独立域名覆写" : hosts.Error is not null ? "域名覆写异常：" + HostsError(hosts.Error) :
        hosts.Applied ? $"已应用 · {hosts.EntryCount} 个域名" + (hosts.ExistingMatches > 0 ? "；系统 hosts 另有同地址条目，停用 EqLink 不会删除它们。" : "") : hosts.Enabled ? "已启用，当前没有生效条目" : $"已停用 · 保留 {hosts.EntryCount} 个域名";
    private static string HostsError(string code) => code switch {
        "HOSTS_FILE_CONFLICT" => "系统 hosts 已将同一域名绑定到其他地址，请先处理冲突。",
        "HOSTS_FILE_WRITE_FAILED" => "系统 hosts 无法写入，可能被其他程序占用或设为只读；现有条目保持。",
        "HOSTS_FILE_BUSY" => "系统 hosts 持续被其他程序占用，请稍后重新保存。",
        "HOSTS_FILE_MARKERS_INVALID" => "EqLink 的 hosts 区块被手动改动，请以管理员身份检查区块标记。",
        "HOSTS_FILE_ROLLBACK_FAILED" => "hosts 恢复失败，请使用管理员恢复命令清理。",
        _ => code
    };
    public async Task LoadHostsAsync()
    {
        var reply = await ExecuteAsync(new("hosts-get"));
        if (!reply.Ok || reply.Hosts is null) throw new InvalidOperationException(reply.Error ?? "无法读取域名覆写。");
        SetHosts(reply.Hosts); _hostsResult = "已读取当前网络的独立配置。"; Changed();
    }
    public async Task SaveHostsAsync(Func<IpcRequest, Task<IpcResponse>>? write = null)
    {
        var settings = HostsOverrideSettings.Parse(HostsEnabled, HostsText, _status.Protection?.Prefix ?? "", _hostsSettings.Revision);
        var reply = await ExecuteAsync(new("hosts-set", Hosts: settings), write ?? (r => ControlClient.WriteAsync(r)));
        if (!reply.Ok || reply.Hosts is null) throw new InvalidOperationException(HostsError(reply.Error ?? "保存域名覆写失败。"));
        SetHosts(reply.Hosts); _hostsResult = settings.Enabled ? "已保存并应用，现有 VPN 连接保持。" : "已停用覆写，条目仍保留。"; Changed();
    }
    private void SetHosts(HostsOverrideSettings settings)
    { _hostsSettings = settings; _hostsEnabled = settings.Enabled; _hostsText = settings.ToHostsText(); }
    public static string Rate(double? value) => value is null ? "—" : value >= 1048576 ? $"{value / 1048576:0.0} MiB/s" : value >= 1024 ? $"{value / 1024:0.0} KiB/s" : $"{value:0} B/s";

    public async Task RefreshAsync()
    {
        if (_busy || !await _requests.WaitAsync(0)) return;
        try { var reply = await _send(new("status")); _status = reply.Status; _available = reply.Ok; }
        catch { _available = false; _status = _status with { Message = "请确认 EqLink 后台服务已安装并正在运行。", Metrics = null }; }
        finally { _requests.Release(); Changed(); }
    }
    public async Task<IpcResponse> ExecuteAsync(IpcRequest request, Func<IpcRequest, Task<IpcResponse>>? write = null)
    {
        if (_busy) throw new InvalidOperationException("操作正在进行，请稍候。");
        _busy = true; Changed(); await _requests.WaitAsync();
        try { var reply = await (write ?? _send)(request); _status = reply.Status; _available = true; return reply; }
        finally { _requests.Release(); _busy = false; Changed(); }
    }
    internal void ShowPreview(ClientStatus status) { _status = status; _available = true; Changed(); }
    private void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}
