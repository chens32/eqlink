using System.Net.NetworkInformation;
using System.IO;
using System.Text.Json;
using EqLink.Core;

namespace EqLink.ClientService;

internal sealed partial class EqLinkDaemon
{
    private WindowsNetworkProtection? _guard;
    private bool _protectionEnabled = true;
    private string? _protectionError, _errorCode, _egressSignature;
    private string _phase = "Disconnected";
    private volatile bool _vpnInitialized, _connecting, _rebuildRequested;
    private bool _ownedOverlayRoute;
    private WindowsNetworkChanges? _networkChanges;
    private Task? _networkLoop;
    private NetworkSnapshot? _lastNetwork;
    private string? _virtualInterfaceId;
    private readonly SemaphoreSlim _networkSignal = new(0, 1);
    private readonly Queue<object> _events = new();
    private readonly object _policyGate = new();
    private Task<object>? _componentInfo;
    private sealed record ProtectionSettings(bool Enabled = true);

    private void StartNetworkProtection()
    {
        _componentInfo=Task.Run(()=>ComponentDiagnostics.Read(Path.Combine(AppContext.BaseDirectory,"bin","hysteria.exe"),
            Path.Combine(AppContext.BaseDirectory,"bin","xray.exe"),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"OpenVPN","bin","openvpn.exe")));
        try
        {
            var file = Path.Combine(_root, "protection.json");
            if (File.Exists(file))
            {
                if (new FileInfo(file).Length > 4096) throw new InvalidDataException("网络保护配置过大");
                _protectionEnabled = (JsonSerializer.Deserialize<ProtectionSettings>(File.ReadAllText(file), Json) ?? throw new InvalidDataException("网络保护配置为空")).Enabled;
            }
            if (_profile is not null && _protectionEnabled) EnsureProtection(_profile);
        }
        catch (Exception ex) { _protectionError = ex.Message; SetState("Error", "PROTECTION_UNAVAILABLE", "网络保护尚未建立: " + ex.Message); }
        _networkChanges = new WindowsNetworkChanges(SignalNetwork);
        _networkLoop = NetworkLoopAsync(_stop.Token);
    }

    private void EnsureProtection(DeviceProfile profile)
    {
        try
        {
            _guard ??= new WindowsNetworkProtection();
            _guard.EnsureBlocked(profile.OverlayCidr, profile.VirtualIp);
            _protectionError = null;
        }
        catch (Exception ex) { _protectionError = ex.Message; throw new InvalidOperationException("虚拟网段保护不可用，请检查 BFE 服务或运行管理员恢复工具。", ex); }
    }

    private void SaveImportedProfile(DeviceProfile profile, byte[] protectedBytes)
    {
        lock (_policyGate)
        {
            var previous = _profile;
            if (_protectionEnabled) EnsureProtection(profile);
            var temp = Path.Combine(_root, "profile.dpapi.tmp");
            try { File.WriteAllBytes(temp, protectedBytes); File.Move(temp, Path.Combine(_root, "profile.dpapi"), true); }
            catch
            {
                if (_protectionEnabled && previous is not null) EnsureProtection(previous); else _guard?.Remove();
                throw;
            }
            lock (_gate) _profile = profile;
        }
    }

    private void BlockOverlay()
    {
        lock (_policyGate)
        {
            lock (_gate) _vpnConnected = false;
            try { _guard?.Deny(); }
            catch (Exception ex) { _protectionError = ex.Message; SetState("Error", "PROTECTION_UNAVAILABLE", "撤销虚拟网放行失败，正在停止连接"); }
        }
    }

    private async Task SetProtectionAsync(bool enabled)
    {
        await _lifecycle.WaitAsync();
        try
        {
            lock (_policyGate)
            {
            if (_wantConnection) throw new InvalidOperationException("请先断开连接，再修改虚拟网段保护。");
            var old = _protectionEnabled;
            if (enabled && _profile is not null) EnsureProtection(_profile);
            if (!enabled) _guard?.Remove();
            var path = Path.Combine(_root, "protection.json");
            try { File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(new ProtectionSettings(enabled), Json)); File.Move(path + ".tmp", path, true); }
            catch { if (old && _profile is not null) EnsureProtection(_profile); else _guard?.Remove(); throw; }
            _protectionEnabled = enabled; _protectionError = null;
            SetState("Disconnected", null, enabled ? "虚拟网段保护已启用" : "虚拟网段保护已由管理员关闭");
            }
        }
        finally { _lifecycle.Release(); }
    }

    private void SignalNetwork() { try { _networkSignal.Release(); } catch (SemaphoreFullException) { } }

    private async Task NetworkLoopAsync(CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await _networkSignal.WaitAsync(TimeSpan.FromSeconds(5), stop);
                await Task.Delay(500, stop);
                try { await ObserveNetworkAsync(stop); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    BlockOverlay();
                    SetState("Blocked", "NETWORK_INSPECTION_FAILED", "网络状态检查失败，虚拟网段保持阻断: " + ex.Message);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    private async Task ObserveNetworkAsync(CancellationToken stop)
    {
        var profile = _profile;
        if (profile is null) return;
        lock (_policyGate)
        {
            if (!ReferenceEquals(profile, _profile)) return;
            if (_protectionEnabled)
            {
                try { EnsureProtection(profile); }
                catch { BlockOverlay(); SetState("Error", "PROTECTION_UNAVAILABLE", "网络保护不可用，连接保持阻断"); return; }
            }
        }
        if (!_wantConnection || _connecting) return;
        var udpEligible = profile.EnableUdp && _mode != TransportMode.TcpOnly && !_udpAuthFailure && !_udpStopped;
        var tcpEligible = profile.EnableTcp && _mode != TransportMode.UdpOnly && !_tcpAuthFailure && !_tcpStopped;
        if (!udpEligible && !tcpEligible)
        {
            await _lifecycle.WaitAsync(stop);
            try
            {
                _wantConnection = false;
                Interlocked.Increment(ref _generation);
                await StopProcessesAsync();
                lock (_gate) _path = ActivePath.None;
                var auth = _udpAuthFailure || _tcpAuthFailure;
                SetState("Error", auth ? "AUTH_FAILED" : "OUTER_PROCESS_FAILED", auth
                    ? "所选路径的认证或证书被拒绝，请联系管理员后重新连接。"
                    : "所选路径的外层进程持续退出，请修复安装后重新连接。");
            }
            finally { _lifecycle.Release(); }
            return;
        }
        var egress = EgressSettingsStore.Load(Path.Combine(_root, "egress.json"));
        var generation = Volatile.Read(ref _generation);
        var snapshot = WindowsNetworkEnvironment.Read(profile, egress, _ownedOverlayRoute && _vpnInitialized, _virtualInterfaceId);
        _lastNetwork = snapshot;
        if (!snapshot.EgressReady)
        {
            if (_selector is not null) await RebuildAsync(false);
            SetState("WaitingForEgress", "EGRESS_UNAVAILABLE", "指定出口不可用，等待同一网卡恢复；可断开后重新选择出口");
            return;
        }
        if (_selector is null || _rebuildRequested || snapshot.EgressSignature != _egressSignature)
        {
            if (!_authFailure && !(_udpAuthFailure && _tcpAuthFailure)) await RebuildAsync(true);
            return;
        }
        if (!_vpnInitialized || _virtualInterfaceId is null) return;
        if (!snapshot.VirtualReady)
        {
            BlockOverlay();
            SetState(snapshot.Conflict is null ? "Verifying" : "Blocked", snapshot.Conflict is null ? "TUN_ROUTE_UNAVAILABLE" : "ROUTE_CONFLICT",
                snapshot.Conflict is null ? "等待 EqLink 虚拟地址和路由就绪" : "虚拟网段存在竞争路由: " + snapshot.Conflict);
            return;
        }
        _ownedOverlayRoute = true;
        lock (_policyGate)
        {
            if (generation != Volatile.Read(ref _generation) || !_wantConnection || !_vpnInitialized || _path == ActivePath.None) return;
            if (_protectionEnabled) _guard!.Allow(snapshot.VirtualLuid!.Value);
        }
        using var ping = new Ping();
        var reply = await ping.SendPingAsync(System.Net.IPAddress.Parse(profile.ServerIp), TimeSpan.FromSeconds(1), "EqLink readiness"u8.ToArray(), null, stop);
        if (generation != Volatile.Read(ref _generation) || !_wantConnection || !_vpnInitialized) { BlockOverlay(); return; }
        if (reply.Status != IPStatus.Success)
        {
            BlockOverlay();
            SetState("Verifying", "VIRTUAL_NETWORK_UNREACHABLE", "外层已连接，虚拟网络探测尚未通过");
            return;
        }
        lock (_gate)
        {
            if (generation != Volatile.Read(ref _generation) || !_wantConnection || !_vpnInitialized || _path == ActivePath.None || (_protectionEnabled && !(_guard?.Allowing ?? false))) return;
            _vpnConnected = true;
            SetState("Connected", null, "虚拟网络已连接");
        }
    }

    private async Task RebuildAsync(bool connect)
    {
        await _lifecycle.WaitAsync();
        int generation;
        try
        {
            if (!_wantConnection || _connecting) return;
            generation = Interlocked.Increment(ref _generation);
            await StopProcessesAsync();
            _rebuildRequested = false;
            _connecting = connect;
            lock (_gate) _path = ActivePath.None;
            SetState(connect ? "Reconnecting" : "WaitingForEgress", null, connect ? "网络环境变化，正在重建连接" : "等待指定出口恢复");
        }
        finally { _lifecycle.Release(); }
        if (connect) _ = ConnectAsync(generation);
    }

    private void SetState(string phase, string? code, string message)
    {
        lock (_gate)
        {
            if (_phase != phase || _errorCode != code || _message != message)
            {
                _events.Enqueue(new { timestamp = DateTimeOffset.UtcNow, phase, code, path = _path, message });
                while (_events.Count > 200) _events.Dequeue();
            }
            _phase = phase; _errorCode = code; _message = message;
        }
    }

    private object Diagnostics()
    {
        object[] events;
        lock (_gate) events = _events.ToArray();
        var egress = ReadEgressConfiguration();
        object? network = null, policy = null;
        try { if(_profile is not null) network=WindowsNetworkEnvironment.Read(_profile,egress.Settings,false,_virtualInterfaceId); }
        catch { network=new {unavailable="ROUTE_QUERY_FAILED"}; }
        try { policy=_guard?.Inspect(); } catch { policy=new {unavailable="WFP_QUERY_FAILED"}; }
        return new
        {
            schemaVersion = 1, timestamp = DateTimeOffset.UtcNow,
            version = typeof(EqLinkDaemon).Assembly.GetName().Version?.ToString(), status = Status(),
            components = _componentInfo?.IsCompletedSuccessfully==true ? _componentInfo.Result : new {state="collecting"},
            endpoint = _profile is null ? null : new { _profile.PublicHost, _profile.TcpPort, _profile.UdpPort, _profile.OverlayCidr, _profile.ServerIp },
            egress = new {egress.Settings,selectedInterface=egress.Interfaces.FirstOrDefault(n=>n.Id.Equals(egress.Settings?.InterfaceId,StringComparison.OrdinalIgnoreCase)),egress.Locked,egress.Error},
            targetAddress = _physicalEgress?.TargetAddress, latestResolvedAddress = _physicalEgress?.LatestTargetAddress,
            virtualAdapterId = _virtualInterfaceId,
            network, protectionPolicy=policy, timeline = events
        };
    }
}
