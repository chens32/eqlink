using System.Net;
using System.Net.Sockets;
using System.Text;

namespace EqLink.Core;

/// <summary>A loopback-only, single-target CONNECT proxy for the OpenVPN TCP stream.</summary>
public sealed class ProxySelector : IAsyncDisposable
{
    private readonly DeviceProfile _profile;
    private readonly Func<TransportMode> _mode;
    private readonly Func<ActivePath, bool> _pathEligible;
    private readonly Action<ActivePath, string> _state;
    private readonly int _listenPort;
    private readonly int _udpProxyPort;
    private readonly int _tcpProxyPort;
    private readonly string _token;
    private readonly object _gate = new();
    private TcpListener? _listener;
    private TcpClient? _activeLocal;
    private TcpClient? _activeRemote;
    private CancellationTokenSource? _stop;
    private Task? _acceptLoop;
    private DateTimeOffset _udpNextProbe;
    private DateTimeOffset _tcpNextProbe;

    public ProxySelector(DeviceProfile profile, Func<TransportMode> mode, Action<ActivePath, string> state,
        string token, int listenPort = 18080, int udpProxyPort = 18081, int tcpProxyPort = 18082,
        Func<ActivePath, bool>? pathEligible = null)
    {
        _profile = profile;
        _mode = mode;
        _state = state;
        _pathEligible = pathEligible ?? (_ => true);
        if (token.Length != 64 || token.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("Invalid selector token.");
        _token = token;
        _listenPort = listenPort;
        _udpProxyPort = udpProxyPort;
        _tcpProxyPort = tcpProxyPort;
    }

    public void Start()
    {
        if (_listener is not null) throw new InvalidOperationException("Selector already started.");
        _stop = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, _listenPort);
        _listener.Start();
        _acceptLoop = AcceptAsync(_stop.Token);
    }

    private async Task AcceptAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient local;
            try { local = await _listener!.AcceptTcpClientAsync(stop); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            _ = HandleAsync(local, stop);
        }
    }

    private async Task HandleAsync(TcpClient local, CancellationToken stop)
    {
        using (local)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                var localStream = local.GetStream();
                var header = await ReadHeaderAsync(localStream, timeout.Token);
                var expected = $"CONNECT 127.0.0.1:{_profile.OpenVpnPort} HTTP/";
                var requestLine = header.Split("\r\n", 2)[0];
                if (!header.StartsWith(expected, StringComparison.Ordinal) ||
                    !(requestLine.EndsWith("HTTP/1.0", StringComparison.Ordinal) ||
                      requestLine.EndsWith("HTTP/1.1", StringComparison.Ordinal)) ||
                    !header.Split("\r\n", StringSplitOptions.None).Any(line =>
                        line.Equals("X-EqLink-Token: " + _token, StringComparison.OrdinalIgnoreCase)))
                {
                    await localStream.WriteAsync("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n\r\n"u8.ToArray(), stop);
                    return;
                }

                foreach (var path in Candidates())
                {
                    if (!CanProbe(path)) continue;
                    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(stop);
                    attempt.CancelAfter(TimeSpan.FromSeconds(4));
                    if (!await ProbeAsync(path, attempt.Token)) { MarkFailure(path); continue; }
                    var remote = await ConnectThroughAsync(path, _profile.OpenVpnPort, attempt.Token);
                    if (remote is null) { MarkFailure(path); continue; }
                    ClearFailure(path);
                    using (remote)
                    {
                        lock (_gate)
                        {
                            _activeLocal?.Close();
                            _activeRemote?.Close();
                            _activeLocal = local;
                            _activeRemote = remote;
                        }
                        _state(path, "外层连接已建立，正在建立虚拟网络");
                        await localStream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), stop);
                        try
                        {
                            using var relayStop = CancellationTokenSource.CreateLinkedTokenSource(stop);
                            var a = localStream.CopyToAsync(remote.GetStream(), relayStop.Token);
                            var b = remote.GetStream().CopyToAsync(localStream, relayStop.Token);
                            var health = MonitorHealthAsync(path, local, relayStop.Token);
                            await Task.WhenAny(a, b, health);
                            relayStop.Cancel();
                            try { await Task.WhenAll(a, b, health); }
                            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
                        }
                        finally
                        {
                            var wasActive = false;
                            lock (_gate)
                            {
                                if (ReferenceEquals(_activeLocal, local))
                                {
                                    _activeLocal = null;
                                    _activeRemote = null;
                                    wasActive = true;
                                }
                            }
                            if (wasActive)
                            {
                                MarkFailure(path);
                                _state(ActivePath.None, "外层连接中断，等待重连");
                            }
                        }
                        return;
                    }
                }
                _state(ActivePath.None, "没有可用的外层路径");
                await localStream.WriteAsync("HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\n\r\n"u8.ToArray(), stop);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or TimeoutException or ObjectDisposedException) { }
        }
    }

    private IEnumerable<ActivePath> Candidates()
    {
        var mode = _mode();
        if (_profile.EnableUdp && mode != TransportMode.TcpOnly && _pathEligible(ActivePath.Udp)) yield return ActivePath.Udp;
        if (_profile.EnableTcp && mode != TransportMode.UdpOnly && _pathEligible(ActivePath.Tcp)) yield return ActivePath.Tcp;
    }

    private async Task MonitorHealthAsync(ActivePath path, TcpClient local, CancellationToken stop)
    {
        var failures = 0;
        while (!stop.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(4000, 6001)), stop);
            using var probe = CancellationTokenSource.CreateLinkedTokenSource(stop);
            probe.CancelAfter(TimeSpan.FromSeconds(3));
            if (await ProbeAsync(path, probe.Token)) { failures = 0; continue; }
            if (++failures < 2) continue;
            lock (_gate)
            {
                if (!ReferenceEquals(_activeLocal, local)) return;
                MarkFailure(path);
                _activeLocal?.Close();
                _activeRemote?.Close();
            }
            return;
        }
    }

    private bool CanProbe(ActivePath path)
    {
        lock (_gate) return DateTimeOffset.UtcNow >= (path == ActivePath.Udp ? _udpNextProbe : _tcpNextProbe);
    }

    private void MarkFailure(ActivePath path)
    {
        var next = DateTimeOffset.UtcNow.AddSeconds(Random.Shared.Next(8, 13));
        lock (_gate)
        {
            if (path == ActivePath.Udp) _udpNextProbe = next;
            else _tcpNextProbe = next;
        }
    }

    private void ClearFailure(ActivePath path)
    {
        lock (_gate)
        {
            if (path == ActivePath.Udp) _udpNextProbe = default;
            else _tcpNextProbe = default;
        }
    }

    private async Task<bool> ProbeAsync(ActivePath path, CancellationToken stop)
    {
        if (_profile.AuthenticatedHealth)
        {
            try
            {
                using var api = new ControlApiClient(_profile, path == ActivePath.Udp ? _udpProxyPort : _tcpProxyPort);
                await api.HealthAsync(stop); return true;
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException) { return false; }
        }
        using var remote = await ConnectThroughAsync(path, _profile.HealthPort, stop);
        if (remote is null) return false;
        try
        {
            var stream = remote.GetStream();
            await stream.WriteAsync("PING\n"u8.ToArray(), stop);
            var response = new byte[5];
            await stream.ReadExactlyAsync(response, stop);
            return response.AsSpan().SequenceEqual("PONG\n"u8);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { return false; }
    }

    private async Task<TcpClient?> ConnectThroughAsync(ActivePath path, int targetPort, CancellationToken stop)
    {
        var proxyPort = path == ActivePath.Udp ? _udpProxyPort : _tcpProxyPort;
        var tcp = new TcpClient();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await tcp.ConnectAsync(IPAddress.Loopback, proxyPort, timeout.Token);
            var stream = tcp.GetStream();
            var request = $"CONNECT 127.0.0.1:{targetPort} HTTP/1.1\r\nHost: 127.0.0.1:{targetPort}\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request), timeout.Token);
            var header = await ReadHeaderAsync(stream, timeout.Token);
            if (!header.StartsWith("HTTP/1.1 200", StringComparison.Ordinal) && !header.StartsWith("HTTP/1.0 200", StringComparison.Ordinal))
            {
                tcp.Dispose();
                return null;
            }
            return tcp;
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or TimeoutException)
        {
            tcp.Dispose();
            return null;
        }
    }

    private static async Task<string> ReadHeaderAsync(NetworkStream stream, CancellationToken stop)
    {
        var bytes = new List<byte>(256);
        var one = new byte[1];
        while (bytes.Count < 8192)
        {
            if (await stream.ReadAsync(one, stop) != 1) throw new IOException("CONNECT header closed early.");
            bytes.Add(one[0]);
            var n = bytes.Count;
            if (n >= 4 && bytes[n - 4] == '\r' && bytes[n - 3] == '\n' && bytes[n - 2] == '\r' && bytes[n - 1] == '\n')
                return Encoding.ASCII.GetString(bytes.ToArray());
        }
        throw new IOException("CONNECT header too long.");
    }

    public void CloseActive()
    {
        lock (_gate) { _activeLocal?.Close(); _activeRemote?.Close(); }
    }

    public async ValueTask DisposeAsync()
    {
        _stop?.Cancel();
        _listener?.Stop();
        CloseActive();
        if (_acceptLoop is not null) await _acceptLoop;
        _stop?.Dispose();
    }
}
