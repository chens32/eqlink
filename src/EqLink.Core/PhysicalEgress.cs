using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace EqLink.Core;

/// <summary>Optional Windows interface binding for the two encrypted outer transports.</summary>
public sealed class PhysicalEgress : IAsyncDisposable
{
    public sealed record Settings(string InterfaceId, string[] DnsServers);
    private readonly NetworkInterface _nic;
    private readonly Settings _settings;
    private readonly DeviceProfile _profile;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<Socket, byte> _streams = new();
    private readonly TcpListener _tcp = new(IPAddress.Loopback, 0);
    private readonly Socket _udp = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private readonly object _udpGate = new();
    private UdpPeer? _peer;
    private sealed record Resolution(IPAddress Address, DateTimeOffset Expires);
    private Resolution _resolution = new(IPAddress.None, DateTimeOffset.MinValue);
    private IPAddress _sessionTarget = IPAddress.None;
    private Task[] _tasks = [];

    private sealed record UdpPeer(IPEndPoint Local, IPAddress Target, Socket Socket);

    private PhysicalEgress(NetworkInterface nic, Settings settings, DeviceProfile profile)
    {
        _nic = nic;
        _settings = settings;
        _profile = profile;
    }

    public string InterfaceName => _nic.Name;
    public string TargetAddress => _sessionTarget.ToString();
    public string LatestTargetAddress => Volatile.Read(ref _resolution).Address.ToString();
    public bool HasNewTarget => !Volatile.Read(ref _resolution).Address.Equals(_sessionTarget);
    public DeviceProfile RuntimeProfile => _profile with
    {
        PublicHost = IPAddress.Loopback.ToString(),
        TcpPort = ((IPEndPoint)_tcp.LocalEndpoint).Port,
        UdpPort = ((IPEndPoint)_udp.LocalEndPoint!).Port
    };

    public static async Task<PhysicalEgress?> FromFileAsync(string path, DeviceProfile profile)
    {
        var settings = EgressSettingsStore.Load(path);
        if (settings is null) return null;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Physical egress currently requires Windows.");
        var nic = NetworkInterface.GetAllNetworkInterfaces().SingleOrDefault(n => n.Id.Equals(settings.InterfaceId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Configured physical network interface was not found.");
        if (nic.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211))
            throw new InvalidOperationException("Choose an Ethernet or Wi-Fi interface for physical egress.");
        var instance = new PhysicalEgress(nic, settings, profile);
        try
        {
            await instance.RefreshAsync(instance._stop.Token);
            instance._sessionTarget = Volatile.Read(ref instance._resolution).Address;
            instance._tcp.Start(16);
            instance._udp.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            instance._tasks = [instance.TcpLoopAsync(), instance.UdpLoopAsync(), instance.RefreshLoopAsync()];
            return instance;
        }
        catch { await instance.DisposeAsync(); throw; }
    }

    private Socket BoundSocket(SocketType type)
    {
        var nic = NetworkInterface.GetAllNetworkInterfaces().SingleOrDefault(n => n.Id.Equals(_settings.InterfaceId, StringComparison.OrdinalIgnoreCase));
        if (nic is null || nic.OperationalStatus != OperationalStatus.Up)
            throw new IOException("Selected physical network interface is down.");
        var properties = nic.GetIPProperties();
        var local = properties.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork &&
            !IPAddress.IsLoopback(a.Address) && !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
            .OrderBy(a => a.Address.ToString(), StringComparer.Ordinal).FirstOrDefault()?.Address
            ?? throw new IOException("Selected physical network interface has no usable IPv4 address.");
        var socket = new Socket(AddressFamily.InterNetwork, type, type == SocketType.Stream ? ProtocolType.Tcp : ProtocolType.Udp);
        try
        {
            // Windows accepts IP_UNICAST_IF in network byte order; the option applies to this socket only.
            socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31, IPAddress.HostToNetworkOrder(properties.GetIPv4Properties().Index));
            socket.Bind(new IPEndPoint(local, 0));
            return socket;
        }
        catch { socket.Dispose(); throw; }
    }

    private static bool UsableTarget(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address)) return false;
        var b = address.GetAddressBytes();
        return b[0] is > 0 and < 224 && !(b[0] == 169 && b[1] == 254) && !(b[0] == 198 && b[1] is 18 or 19);
    }

    private async Task RefreshAsync(CancellationToken stop)
    {
        if (IPAddress.TryParse(_profile.PublicHost, out var numeric))
        {
            if (!UsableTarget(numeric)) throw new IOException("Physical egress rejected the server address.");
            Volatile.Write(ref _resolution, new Resolution(numeric, DateTimeOffset.MaxValue));
            return;
        }
        var host = new IdnMapping().GetAscii(_profile.PublicHost.TrimEnd('.'));
        foreach (var resolver in _settings.DnsServers)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                var query = BuildQuery(host, out var id);
                using var dns = BoundSocket(SocketType.Dgram);
                await dns.ConnectAsync(new IPEndPoint(IPAddress.Parse(resolver), 53), timeout.Token);
                await dns.SendAsync(query, SocketFlags.None, timeout.Token);
                var response = new byte[4096];
                var count = await dns.ReceiveAsync(response, SocketFlags.None, timeout.Token);
                var (address, ttl) = ParseAnswer(response.AsSpan(0, count), id);
                if (!UsableTarget(address)) throw new IOException("DNS returned a loopback, link-local or fake IP address.");
                Volatile.Write(ref _resolution, new Resolution(address, DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(ttl, 10u, 300u))));
                return;
            }
            catch (Exception ex) when (!stop.IsCancellationRequested && ex is IOException or SocketException or OperationCanceledException or ArgumentException or NetworkInformationException) { }
        }
        throw new IOException("Physical-interface DNS resolution failed; automatic proxy routing was not used.");
    }

    private async Task RefreshLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), _stop.Token);
                if (DateTimeOffset.UtcNow < Volatile.Read(ref _resolution).Expires) continue;
                try { await RefreshAsync(_stop.Token); }
                catch (IOException) { }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private IPAddress CurrentTarget()
    {
        // One transport generation stays pinned to its verified address, including its health
        // probes. A new generation must resolve afresh. Never move an existing QUIC connection
        // to a different server just because its DNS answer changed.
        return _sessionTarget;
    }

    private async Task TcpLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _tcp.AcceptSocketAsync(_stop.Token);
                if (_streams.Count >= 32) { client.Dispose(); continue; }
                _streams.TryAdd(client, 0);
                _ = RelayTcpAsync(client);
            }
        }
        catch (Exception ex) when (_stop.IsCancellationRequested && ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private async Task RelayTcpAsync(Socket client)
    {
        Socket? remote = null;
        try
        {
            remote = BoundSocket(SocketType.Stream);
            _streams.TryAdd(remote, 0);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            await remote.ConnectAsync(new IPEndPoint(CurrentTarget(), _profile.TcpPort), timeout.Token);
            using var localStream = new NetworkStream(client, false);
            using var remoteStream = new NetworkStream(remote, false);
            using var relayStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            var outbound = localStream.CopyToAsync(remoteStream, relayStop.Token);
            var inbound = remoteStream.CopyToAsync(localStream, relayStop.Token);
            await Task.WhenAny(outbound, inbound);
            relayStop.Cancel();
            try { await Task.WhenAll(outbound, inbound); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            client.Dispose();
            _streams.TryRemove(client, out _);
            if (remote is not null) { remote.Dispose(); _streams.TryRemove(remote, out _); }
        }
    }

    private async Task UdpLoopAsync()
    {
        var buffer = new byte[65535];
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var received = await _udp.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), _stop.Token);
                try
                {
                    var client = (IPEndPoint)received.RemoteEndPoint;
                    var target = CurrentTarget();
                    UdpPeer peer;
                    lock (_udpGate)
                    {
                        if (_peer is null || !_peer.Local.Equals(client) || !_peer.Target.Equals(target))
                        {
                            _peer?.Socket.Dispose();
                            var upstream = BoundSocket(SocketType.Dgram);
                            try { upstream.Connect(new IPEndPoint(target, _profile.UdpPort)); }
                            catch { upstream.Dispose(); throw; }
                            _peer = new UdpPeer(client, target, upstream);
                            _ = ReceiveUdpAsync(_peer);
                        }
                        peer = _peer;
                    }
                    await peer.Socket.SendAsync(buffer.AsMemory(0, received.ReceivedBytes), SocketFlags.None, _stop.Token);
                }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { }
            }
        }
        catch (Exception ex) when (_stop.IsCancellationRequested && ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private async Task ReceiveUdpAsync(UdpPeer peer)
    {
        var buffer = new byte[65535];
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var count = await peer.Socket.ReceiveAsync(buffer, SocketFlags.None, _stop.Token);
                lock (_udpGate) if (!ReferenceEquals(_peer, peer)) return;
                await _udp.SendToAsync(buffer.AsMemory(0, count), SocketFlags.None, peer.Local, _stop.Token);
            }
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            lock (_udpGate) if (ReferenceEquals(_peer, peer)) { _peer = null; peer.Socket.Dispose(); }
        }
    }

    private static byte[] BuildQuery(string host, out ushort id)
    {
        using var buffer = new MemoryStream();
        id = (ushort)RandomNumberGenerator.GetInt32(65536);
        var header = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, id);
        header[2] = 1;
        header[5] = 1;
        buffer.Write(header);
        foreach (var label in host.Split('.'))
        {
            if (label.Length is < 1 or > 63) throw new ArgumentException("Invalid DNS label.");
            buffer.WriteByte((byte)label.Length);
            buffer.Write(System.Text.Encoding.ASCII.GetBytes(label));
        }
        buffer.Write([0, 0, 1, 0, 1]);
        return buffer.ToArray();
    }

    private static (IPAddress Address, uint Ttl) ParseAnswer(ReadOnlySpan<byte> data, ushort id)
    {
        if (data.Length < 12 || BinaryPrimitives.ReadUInt16BigEndian(data) != id ||
            (BinaryPrimitives.ReadUInt16BigEndian(data[2..]) & 0x820F) != 0x8000 ||
            BinaryPrimitives.ReadUInt16BigEndian(data[4..]) != 1)
            throw new IOException("Invalid or truncated DNS response.");
        var offset = SkipName(data, 12) + 4;
        var answers = BinaryPrimitives.ReadUInt16BigEndian(data[6..]);
        for (var i = 0; i < answers; i++)
        {
            offset = SkipName(data, offset);
            if (offset + 10 > data.Length) throw new IOException("Truncated DNS answer.");
            var type = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            var klass = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]);
            var ttl = BinaryPrimitives.ReadUInt32BigEndian(data[(offset + 4)..]);
            var size = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 8)..]);
            offset += 10;
            if (offset + size > data.Length) throw new IOException("Truncated DNS record.");
            if (type == 1 && klass == 1 && size == 4) return (new IPAddress(data.Slice(offset, 4)), ttl);
            offset += size;
        }
        throw new IOException("DNS answer contains no IPv4 address.");
    }

    private static int SkipName(ReadOnlySpan<byte> data, int offset)
    {
        for (var i = 0; i < 128 && offset < data.Length; i++)
        {
            var length = data[offset++];
            if (length == 0) return offset;
            if ((length & 0xC0) == 0xC0 && offset < data.Length) return offset + 1;
            if ((length & 0xC0) != 0 || offset + length > data.Length) break;
            offset += length;
        }
        throw new IOException("Invalid DNS name.");
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _tcp.Stop();
        _udp.Dispose();
        lock (_udpGate) { _peer?.Socket.Dispose(); _peer = null; }
        foreach (var socket in _streams.Keys) socket.Dispose();
        try { await Task.WhenAll(_tasks).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or TimeoutException) { }
        _stop.Dispose();
    }
}
