using System.Diagnostics;
using System.IO;
using EqLink.Core;

namespace EqLink.ClientService;

internal sealed partial class EqLinkDaemon
{
    private bool ModeAllows(ActivePath path) => path == ActivePath.Udp ? _mode != TransportMode.TcpOnly : _mode != TransportMode.UdpOnly;

    private async Task ApplyModeAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (!_wantConnection || _connecting || _selector is null || _profile is not { } profile) return;
            var generation = Volatile.Read(ref _generation);
            if (_path != ActivePath.None && !ModeAllows(_path))
            { BlockOverlay(); SetState("Reconnecting",null,"连接模式已变更，正在重连"); _selector.CloseActive(); }
            var outer = (_physicalEgress?.RuntimeProfile ?? profile) with { HysteriaAuth = profile.HysteriaAuth, VlessId = profile.VlessId, HysteriaCaPem = profile.HysteriaCaPem, HysteriaCertificateSha256 = profile.HysteriaCertificateSha256 };
            if (profile.EnableUdp && ModeAllows(ActivePath.Udp))
            {
                if (_hysteria is null || _hysteria.HasExited)
                {
                    _physicalEgress?.ResetUdpPeer();
                    WriteHysteriaConfiguration(outer);
                    _hysteria=StartProcess(Path.Combine(AppContext.BaseDirectory,"bin","hysteria.exe"),"client","-c",Path.Combine(_root,"hysteria.json"));
                    _udpStopped=false; _ = MonitorSidecarAsync(ActivePath.Udp,_hysteria,generation);
                }
            }
            else if (_hysteria is { } oldHy)
            { _hysteria=null; if(!oldHy.HasExited) {oldHy.Kill(true);await oldHy.WaitForExitAsync();} _physicalEgress?.ResetUdpPeer(); }
            if (profile.EnableTcp && ModeAllows(ActivePath.Tcp))
            {
                if (_xray is null || _xray.HasExited)
                {
                    File.WriteAllText(Path.Combine(_root,"xray.json"),ConfigRenderer.ClientXray(outer,TcpProxyPort));
                    _xray=StartProcess(Path.Combine(AppContext.BaseDirectory,"bin","xray.exe"),"run","-c",Path.Combine(_root,"xray.json"));
                    _tcpStopped=false; _ = MonitorSidecarAsync(ActivePath.Tcp,_xray,generation);
                }
            }
            else if (_xray is { } oldXray)
            { _xray=null; if(!oldXray.HasExited) {oldXray.Kill(true);await oldXray.WaitForExitAsync();} }
        }
        finally { _lifecycle.Release(); }
    }
}
