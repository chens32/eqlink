using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net.NetworkInformation;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using EqLink.Core;

namespace EqLink.ClientService;

internal sealed partial class EqLinkDaemon
{
    private const string PipeName = "EqLink.Control.v1";
    private const int SelectorPort = 18080, UdpProxyPort = 18081, TcpProxyPort = 18082;
    private readonly object _gate = new();
    private readonly string _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "EqLink");
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _commands = new(1, 1);
    private int _generation;
    private DeviceProfile? _profile;
    private TransportMode _mode = TransportMode.Auto;
    private ActivePath _path = ActivePath.None;
    private string _message = "请导入设备配置";
    private volatile bool _wantConnection;
    private bool _vpnConnected;
    private volatile bool _authFailure;
    private volatile bool _udpAuthFailure, _tcpAuthFailure;
    private volatile bool _udpStopped, _tcpStopped;
    private ProxySelector? _selector;
    private PhysicalEgress? _physicalEgress;
    private string _configuredEgressName = "系统路由";
    private Process? _hysteria, _xray, _openVpn;
    private Task? _pipeLoop;
    private string? _selectorToken;
    private OwnedProcessJob? _processJob;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Start()
    {
        _processJob = new OwnedProcessJob();
        SecureDirectory();
        DeleteRuntimeConfigs();
        var profileFile = Path.Combine(_root, "profile.dpapi");
        if (File.Exists(profileFile))
        {
            try
            {
                var plaintext = ProtectedData.Unprotect(File.ReadAllBytes(profileFile), "EqLink-profile-v1"u8.ToArray(), DataProtectionScope.LocalMachine);
                try
                {
                    _profile = JsonSerializer.Deserialize<DeviceProfile>(plaintext, Json) ?? throw new InvalidDataException("Empty profile.");
                    _profile.Validate();
                    _message = "已导入设备，尚未连接";
                }
                finally { CryptographicOperations.ZeroMemory(plaintext); }
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException or ArgumentException or InvalidDataException)
            {
                _profile = null;
                _message = "已保存的设备配置无效；请以管理员身份重新导入";
            }
        }
        var modeFile = Path.Combine(_root, "mode.txt");
        if (File.Exists(modeFile) && Enum.TryParse<TransportMode>(File.ReadAllText(modeFile), out var mode) && Enum.IsDefined(mode)) _mode = mode;
        ReadEgressConfiguration();
        StartNetworkProtection();
        _pipeLoop = PipeLoopAsync(_stop.Token);
    }

    private void SecureDirectory()
    {
        Directory.CreateDirectory(_root);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(_root).SetAccessControl(security);
    }

    private ClientStatus Status()
    {
        lock (_gate) return new ClientStatus(_profile is not null, _vpnConnected, _profile?.DeviceName ?? "", _profile?.VirtualIp ?? "",
            _mode, _path, _message, _physicalEgress?.InterfaceName ?? _configuredEgressName, _phase, _errorCode,
            new ProtectionStatus(_protectionEnabled, _guard?.Installed ?? false, _guard?.Allowing ?? false, _profile?.OverlayCidr, _protectionError));
    }

    private async Task PipeLoopAsync(CancellationToken stop)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        while (!stop.IsCancellationRequested)
        {
            var pipe = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 4096, 4096, security);
            try { await pipe.WaitForConnectionAsync(stop); }
            catch (OperationCanceledException) { pipe.Dispose(); break; }
            _ = HandlePipeAsync(pipe, stop);
        }
    }

    private async Task HandlePipeAsync(NamedPipeServerStream pipe, CancellationToken stop)
    {
        await using (pipe)
        {
            try
            {
                var line = await ReadLineLimitedAsync(pipe, 1_000_000, stop);
                var request = JsonSerializer.Deserialize<IpcRequest>(line, Json) ?? throw new InvalidDataException("请求为空");
                if (request.Command is "import" or "egress-set" or "protection-set")
                {
                    var isAdmin = false;
                    pipe.RunAsClient(() => isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator));
                    if (!isAdmin) throw new UnauthorizedAccessException("修改设备、出口或网络保护需要以管理员身份运行 EqLink。");
                }
                await _commands.WaitAsync(stop);
                IpcResponse response;
                try { response = await DispatchAsync(request); }
                finally { _commands.Release(); }
                var bytes = JsonSerializer.SerializeToUtf8Bytes(response, Json);
                await pipe.WriteAsync(bytes, stop);
                await pipe.WriteAsync("\n"u8.ToArray(), stop);
            }
            catch (Exception ex)
            {
                var response = JsonSerializer.SerializeToUtf8Bytes(new IpcResponse(false, ex.Message, Status()), Json);
                try { await pipe.WriteAsync(response, stop); await pipe.WriteAsync("\n"u8.ToArray(), stop); }
                catch (IOException) { }
            }
        }
    }

    private async Task<IpcResponse> DispatchAsync(IpcRequest request)
    {
        switch (request.Command)
        {
            case "status": break;
            case "diagnostics": return new IpcResponse(true, null, Status(), Diagnostics: Diagnostics());
            case "protection-set":
                await SetProtectionAsync(request.ProtectionEnabled ?? throw new ArgumentException("缺少保护开关"));
                break;
            case "egress-get":
                var egressConfiguration = ReadEgressConfiguration();
                return new IpcResponse(true, null, Status(), egressConfiguration);
            case "egress-set":
                await _lifecycle.WaitAsync();
                try
                {
                    if (_wantConnection || _physicalEgress is not null) throw new InvalidOperationException("请先断开连接，再保存出口设置。");
                    if (request.Egress is not null)
                    {
                        var settings = EgressSettingsStore.Validate(request.Egress);
                        if (!EgressSettingsStore.Interfaces().Any(n => n.Id.Equals(settings.InterfaceId, StringComparison.OrdinalIgnoreCase)))
                            throw new InvalidOperationException("所选网卡已不存在，请刷新网卡列表后重新选择。");
                    }
                    EgressSettingsStore.Save(Path.Combine(_root, "egress.json"), request.Egress);
                    var configuration = ReadEgressConfiguration();
                    lock (_gate) _message = "出口设置已保存，下次连接生效";
                    return new IpcResponse(true, null, Status(), configuration);
                }
                finally { _lifecycle.Release(); }
            case "import":
                if (string.IsNullOrEmpty(request.BundleBase64) || request.Passphrase is null) throw new ArgumentException("缺少导入包或密码");
                if (_wantConnection) throw new InvalidOperationException("请先断开当前连接");
                var profile = BundleCrypto.Decrypt(Convert.FromBase64String(request.BundleBase64), request.Passphrase);
                CheckOverlayCollision(profile);
                var plain = JsonSerializer.SerializeToUtf8Bytes(profile, Json);
                byte[] protectedBytes;
                try { protectedBytes = ProtectedData.Protect(plain, "EqLink-profile-v1"u8.ToArray(), DataProtectionScope.LocalMachine); }
                finally { CryptographicOperations.ZeroMemory(plain); }
                SaveImportedProfile(profile, protectedBytes);
                SetState("Disconnected", null, "设备已导入，虚拟网段保护已配置");
                if (_mode == TransportMode.UdpOnly && !profile.EnableUdp || _mode == TransportMode.TcpOnly && !profile.EnableTcp)
                    _mode = TransportMode.Auto;
                break;
            case "mode":
                if (!Enum.TryParse<TransportMode>(request.Mode, true, out var mode) || !Enum.IsDefined(mode)) throw new ArgumentException("无效连接模式");
                if (_profile is not null && (mode == TransportMode.UdpOnly && !_profile.EnableUdp || mode == TransportMode.TcpOnly && !_profile.EnableTcp))
                    throw new ArgumentException("服务端未启用所选路径");
                lock (_gate) _mode = mode;
                File.WriteAllText(Path.Combine(_root, "mode.txt"), mode.ToString());
                if (mode == TransportMode.UdpOnly && _path == ActivePath.Tcp || mode == TransportMode.TcpOnly && _path == ActivePath.Udp)
                { BlockOverlay(); SetState("Reconnecting", null, "连接模式已变更，正在重连"); _selector?.CloseActive(); }
                break;
            case "connect":
                if (_profile is null) throw new InvalidOperationException("请先导入设备配置");
                CheckOverlayCollision(_profile);
                if (_protectionEnabled) EnsureProtection(_profile);
                if (!_wantConnection)
                {
                    _wantConnection = true;
                    _authFailure = false;
                    _udpAuthFailure = false;
                    _tcpAuthFailure = false;
                    _udpStopped = false;
                    _tcpStopped = false;
                    SetState("Connecting", null, "正在启动连接");
                    _connecting = true;
                    _ = ConnectAsync(Interlocked.Increment(ref _generation));
                }
                break;
            case "disconnect": await DisconnectAsync(); break;
            default: throw new ArgumentException("未知控制命令");
        }
        return new IpcResponse(true, null, Status());
    }

    private EgressConfiguration ReadEgressConfiguration()
    {
        var interfaces = EgressSettingsStore.Interfaces();
        try
        {
            var settings = EgressSettingsStore.Load(Path.Combine(_root, "egress.json"));
            var label = settings is null ? "系统路由" : interfaces.FirstOrDefault(n => n.Id.Equals(settings.InterfaceId, StringComparison.OrdinalIgnoreCase))?.Name
                ?? settings.InterfaceId + "（未找到）";
            lock (_gate) _configuredEgressName = label;
            return new EgressConfiguration(settings, interfaces, _wantConnection || _physicalEgress is not null);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            lock (_gate) _configuredEgressName = "配置无效";
            return new EgressConfiguration(null, interfaces, _wantConnection || _physicalEgress is not null, "出口配置读取失败：" + ex.Message);
        }
    }

    private async Task ConnectAsync(int generation)
    {
        try
        {
            await _lifecycle.WaitAsync();
            try
            {
                if (generation != Volatile.Read(ref _generation) || !_wantConnection) return;
            var p = _profile ?? throw new InvalidOperationException("缺少设备配置");
            var egress = await PhysicalEgress.FromFileAsync(Path.Combine(_root, "egress.json"), p);
            if (generation != Volatile.Read(ref _generation) || !_wantConnection)
            {
                if (egress is not null) await egress.DisposeAsync();
                return;
            }
            _physicalEgress = egress;
            _egressSignature = WindowsNetworkEnvironment.EgressSignature(EgressSettingsStore.Load(Path.Combine(_root, "egress.json")));
            var outerProfile = _physicalEgress?.RuntimeProfile ?? p;
            _selectorToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            _selector = new ProxySelector(p, () => _mode, (path, msg) =>
            {
                if (generation == Volatile.Read(ref _generation))
                {
                    _vpnInitialized = false;
                    BlockOverlay();
                    lock (_gate)
                    {
                        _path = path;
                        _vpnConnected = false;
                        _message = path == ActivePath.None && (_udpAuthFailure || _tcpAuthFailure)
                            ? "外层认证失败；请检查设备是否被吊销或重新导入配置"
                            : path == ActivePath.None && (_udpStopped || _tcpStopped)
                                ? "外层进程持续退出；请断开后重试并检查安装文件" : msg;
                    }
                    SetState(path == ActivePath.None ? "Reconnecting" : "Connecting", null, _message);
                    if (path == ActivePath.None && (_physicalEgress?.HasNewTarget ?? false)) _rebuildRequested = true;
                    SignalNetwork();
                }
            }, _selectorToken, SelectorPort, UdpProxyPort, TcpProxyPort,
                path => !_rebuildRequested && (path == ActivePath.Udp ? !_udpAuthFailure && !_udpStopped : !_tcpAuthFailure && !_tcpStopped));
            _selector.Start();
            File.WriteAllText(Path.Combine(_root, "openvpn.ovpn"), ConfigRenderer.ClientOpenVpn(p, SelectorPort, _selectorToken));
            if (p.EnableUdp)
            {
                File.WriteAllText(Path.Combine(_root, "hysteria.json"), ConfigRenderer.ClientHysteria(outerProfile, UdpProxyPort));
                _hysteria = StartProcess(Path.Combine(AppContext.BaseDirectory, "bin", "hysteria.exe"), "client", "-c", Path.Combine(_root, "hysteria.json"));
                _ = MonitorSidecarAsync(ActivePath.Udp, _hysteria, generation);
            }
            if (p.EnableTcp)
            {
                File.WriteAllText(Path.Combine(_root, "xray.json"), ConfigRenderer.ClientXray(outerProfile, TcpProxyPort));
                _xray = StartProcess(Path.Combine(AppContext.BaseDirectory, "bin", "xray.exe"), "run", "-c", Path.Combine(_root, "xray.json"));
                _ = MonitorSidecarAsync(ActivePath.Tcp, _xray, generation);
            }
            _connecting = false;
            }
            finally { _lifecycle.Release(); }
            await RunOpenVpnLoopAsync(generation);
        }
        catch (Exception ex)
        {
            await _lifecycle.WaitAsync();
            try
            {
                if (generation != Volatile.Read(ref _generation)) return;
                _connecting = false;
                lock (_gate) { _vpnConnected = false; _path = ActivePath.None; }
                await StopProcessesAsync();
                if (ex is FileNotFoundException)
                { _wantConnection = false; SetState("Error", "COMPONENT_MISSING", "缺少连接组件，请修复 EqLink 安装。"); }
                else SetState("WaitingForEgress", ex is System.ComponentModel.Win32Exception ? "PROTECTION_OR_NETWORK_UNAVAILABLE" : "EGRESS_OR_DNS_UNAVAILABLE", "连接准备失败，等待恢复: " + ex.Message);
            }
            finally { _lifecycle.Release(); }
        }
    }

    private async Task RunOpenVpnLoopAsync(int generation)
    {
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenVPN", "bin", "openvpn.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException("OpenVPN Community 未安装", exe);
        while (generation == Volatile.Read(ref _generation) && _wantConnection && !_authFailure)
        {
            _openVpn = StartProcess(exe, "--config", Path.Combine(_root, "openvpn.ovpn"));
            var current = _openVpn;
            if (generation != Volatile.Read(ref _generation) || !_wantConnection)
            {
                if (!current.HasExited) current.Kill(true);
                current.Dispose();
                if (ReferenceEquals(_openVpn, current)) _openVpn = null;
                break;
            }
            var stdout = ReadOpenVpnAsync(current.StandardOutput, generation);
            var stderr = ReadOpenVpnAsync(current.StandardError, generation);
            await current.WaitForExitAsync();
            if (generation == Volatile.Read(ref _generation)) { _vpnInitialized = false; BlockOverlay(); }
            await Task.WhenAll(stdout, stderr);
            current.Dispose();
            if (ReferenceEquals(_openVpn, current)) _openVpn = null;
            if (generation != Volatile.Read(ref _generation)) break;
            lock (_gate) { _vpnConnected = false; _message = _authFailure ? "认证失败，请联系管理员" : "OpenVPN 已断开，正在重连"; }
            if (_wantConnection && !_authFailure) await Task.Delay(2000);
        }
        if (generation == Volatile.Read(ref _generation) && _authFailure)
        {
            await _lifecycle.WaitAsync();
            try
            {
                if(generation != Volatile.Read(ref _generation)) return;
                _wantConnection = false;
                _connecting = false;
                Interlocked.Increment(ref _generation); // Ignore transport-close callbacks from this rejected session.
                await StopProcessesAsync();
                lock(_gate) _path=ActivePath.None;
                SetState("Error","AUTH_FAILED","OpenVPN 证书或设备认证失败，请联系管理员后重新连接。");
            }
            finally { _lifecycle.Release(); }
        }
    }

    private async Task ReadOpenVpnAsync(StreamReader reader, int generation)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            if (generation != Volatile.Read(ref _generation)) continue;
            if (line.Contains("ARP Flush on interface",StringComparison.OrdinalIgnoreCase) ||
                line.Contains("TAP-WIN32 device",StringComparison.OrdinalIgnoreCase) && line.Contains("opened",StringComparison.Ordinal))
            {
                var adapter = System.Text.RegularExpressions.Regex.Match(line,@"\{[0-9a-fA-F-]{36}\}");
                if(adapter.Success) _virtualInterfaceId=adapter.Value;
            }
            // OpenVPN 2.7 logs the TAP adapter's name rather than its device path.
            var opened = System.Text.RegularExpressions.Regex.Match(line,@"tap-windows6 device \[(.+)\] opened",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if(opened.Success)
                _virtualInterfaceId = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n=>n.Name==opened.Groups[1].Value && n.Description.Contains("TAP-Windows",StringComparison.OrdinalIgnoreCase))?.Id;
            if (line.Contains("Initialization Sequence Completed", StringComparison.Ordinal))
            { _vpnInitialized = true; SetState("Verifying", null, "OpenVPN 已建立，正在验证虚拟路由与网络保护"); SignalNetwork(); }
            else if (line.Contains("AUTH_FAILED", StringComparison.Ordinal) || line.Contains("VERIFY ERROR", StringComparison.Ordinal))
            { _authFailure = true; BlockOverlay(); SetState("Error", "AUTH_FAILED", "OpenVPN 证书或设备认证失败，请联系管理员"); }
        }
    }

    private async Task MonitorSidecarAsync(ActivePath path, Process process, int generation, int priorFailures = 0)
    {
        var started = DateTimeOffset.UtcNow;
        var stdout = DrainAsync(process.StandardOutput, path, generation);
        var stderr = DrainAsync(process.StandardError, path, generation);
        await process.WaitForExitAsync();
        await Task.WhenAll(stdout, stderr);
        if (generation != Volatile.Read(ref _generation) || !_wantConnection) return;
        if (_path == path) _selector?.CloseActive();
        if (path == ActivePath.Udp ? _udpAuthFailure : _tcpAuthFailure) return;
        var failures = DateTimeOffset.UtcNow - started > TimeSpan.FromSeconds(30) ? 1 : priorFailures + 1;
        if (failures >= 5)
        {
            if (path == ActivePath.Udp) _udpStopped = true;
            else _tcpStopped = true;
            lock (_gate) _message = "外层进程持续退出；请断开后重试并检查安装文件";
            SignalNetwork();
            return;
        }
        await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 1 << failures) + Random.Shared.Next(0, 3)));
        await _lifecycle.WaitAsync();
        try
        {
            if (generation != Volatile.Read(ref _generation) || !_wantConnection || _profile is null) return;
            if (path == ActivePath.Udp)
            {
                _hysteria = StartProcess(Path.Combine(AppContext.BaseDirectory, "bin", "hysteria.exe"), "client", "-c", Path.Combine(_root, "hysteria.json"));
                _ = MonitorSidecarAsync(path, _hysteria, generation, failures);
            }
            else
            {
                _xray = StartProcess(Path.Combine(AppContext.BaseDirectory, "bin", "xray.exe"), "run", "-c", Path.Combine(_root, "xray.json"));
                _ = MonitorSidecarAsync(path, _xray, generation, failures);
            }
        }
        catch (Exception ex)
        {
            if (path == ActivePath.Udp) _udpStopped = true;
            else _tcpStopped = true;
            lock (_gate) _message = "外层进程启动失败: " + ex.Message;
        }
        finally { _lifecycle.Release(); }
    }

    private async Task DrainAsync(StreamReader reader, ActivePath path, int generation)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            if (generation != Volatile.Read(ref _generation)) continue;
            if (line.Contains("authentication failed", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("invalid username/password", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("certificate verify failed", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("x509:", StringComparison.OrdinalIgnoreCase))
            {
                if (path == ActivePath.Udp) _udpAuthFailure = true;
                else _tcpAuthFailure = true;
                lock (_gate) _message = "外层认证或证书失败；请联系管理员检查设备凭据";
                if (_path == path) _selector?.CloseActive();
                SignalNetwork();
            }
        }
    }

    private static void CheckOverlayCollision(DeviceProfile profile)
    {
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => (a.Address, a.PrefixLength));
        if (OverlayGuard.Overlaps(profile.OverlayCidr, addresses, profile.VirtualIp))
            throw new InvalidOperationException("虚拟网段与本机现有网络重叠，请联系管理员更换网段");
    }

    private Process StartProcess(string exe, params string[] args)
    {
        if (!File.Exists(exe)) throw new FileNotFoundException("缺少依赖程序", exe);
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 " + Path.GetFileName(exe));
        try { _processJob?.Add(process); return process; }
        catch { process.Dispose(); throw; }
    }

    private async Task DisconnectAsync()
    {
        _wantConnection = false;
        _connecting = false;
        BlockOverlay();
        Interlocked.Increment(ref _generation);
        await _lifecycle.WaitAsync();
        try { await StopProcessesAsync(); }
        finally { _lifecycle.Release(); }
        lock (_gate) { _vpnConnected = false; _path = ActivePath.None; _message = "已断开"; }
        SetState("Disconnected", null, "已断开，虚拟网段保持保护");
    }

    private async Task StopProcessesAsync()
    {
        _vpnInitialized = false;
        _virtualInterfaceId = null;
        BlockOverlay();
        _ownedOverlayRoute = false;
        _selector?.CloseActive();
        foreach (var process in new[] { _openVpn, _hysteria, _xray })
        {
            if (process is null) continue;
            try { if (!process.HasExited) process.Kill(true); }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or TimeoutException) { }
        }
        if (_selector is not null) { await _selector.DisposeAsync(); _selector = null; }
        if (_physicalEgress is not null) { await _physicalEgress.DisposeAsync(); _physicalEgress = null; }
        DeleteRuntimeConfigs();
    }

    private void DeleteRuntimeConfigs()
    {
        foreach (var name in new[] { "openvpn.ovpn", "hysteria.json", "xray.json" })
            File.Delete(Path.Combine(_root, name));
    }

    public async Task StopAsync()
    {
        _stop.Cancel();
        await DisconnectAsync();
        _networkChanges?.Dispose();
        if (_networkLoop is not null) await _networkLoop;
        _guard?.Dispose();
        _processJob?.Dispose();
        if (_pipeLoop is not null) await _pipeLoop;
    }

    private static async Task<byte[]> ReadLineLimitedAsync(Stream stream, int limit, CancellationToken stop)
    {
        using var memory = new MemoryStream();
        var b = new byte[1];
        while (memory.Length < limit)
        {
            if (await stream.ReadAsync(b, stop) == 0) throw new IOException("控制连接已关闭");
            if (b[0] == '\n') return memory.ToArray();
            memory.WriteByte(b[0]);
        }
        throw new IOException("控制请求过大");
    }
}
