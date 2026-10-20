using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using EqLink.Core;

internal static class PhysicalEgressUdpTests
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (!OperatingSystem.IsWindows() || args.Length != 3) throw new ArgumentException("--egress-udp INTERFACE_GUID OUTPUT_JSON");
        var nic = NetworkInterface.GetAllNetworkInterfaces().Single(n => n.Id.Equals(args[1], StringComparison.OrdinalIgnoreCase));
        if (nic.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)) throw new ArgumentException("Physical interface required");
        var local = nic.GetIPProperties().UnicastAddresses.First(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Address;
        var output = Path.GetFullPath(args[2]);
        var settings = Path.Combine(Path.GetDirectoryName(output)!, "fixture-egress.json");
        var evidence = new Dictionary<string, object?> { ["timestamp"] = DateTimeOffset.UtcNow, ["scope"] = "Actual PhysicalEgress class; local physical-IP UDP peer; no public server or credentials", ["interface"] = nic.Id };
        var checks = new List<object>();
        Socket? server = null;
        PhysicalEgress? egress = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            EgressSettingsStore.Save(settings, new(nic.Id, ["223.5.5.5"]));
            server = NewServer(local, 0);
            var serverPort = ((IPEndPoint)server.LocalEndPoint!).Port;
            egress = await PhysicalEgress.FromFileAsync(settings, new DeviceProfile {
                PublicHost = local.ToString(), UdpPort = serverPort, TcpPort = serverPort,
                DeviceId = "fixture", DeviceName = "fixture", VirtualIp = "10.88.0.10", OverlayCidr = "10.88.0.0/24", ServerIp = "10.88.0.1",
                OpenVpnPort = 1194, HealthPort = 1195, EnableUdp = true, EnableTcp = true,
                CaPem = "unused", CertificatePem = "unused", PrivateKeyPem = "unused", TlsCryptV2Key = "unused"
            }) ?? throw new Exception("No egress");
            var relay = new IPEndPoint(IPAddress.Loopback, egress.RuntimeProfile.UdpPort);
            for (var i = 1; i <= 5; i++)
            {
                IPEndPoint upstream;
                using (var oldClient = NewServer(IPAddress.Loopback, 0))
                    upstream = await Exchange(server, oldClient, relay, local);
                // A late response to an exited HY2 loopback port must not kill the shared receive loop.
                for (var late = 0; late < 3; late++)
                {
                    await server.SendToAsync(new byte[] { 1, 2, 3 }, SocketFlags.None, upstream);
                    await Task.Delay(30);
                }
                await Task.Delay(120);
                using var replacement = NewServer(IPAddress.Loopback, 0);
                await Exchange(server, replacement, relay, local);
                checks.Add(new { name = "closed-local-peer-replacement", iteration = i, result = "passed" });
            }
            // An ICMP error from a temporarily closed upstream port must retire only that peer socket.
            using var persistentClient = NewServer(IPAddress.Loopback, 0);
            await Exchange(server, persistentClient, relay, local);
            server.Dispose();
            await persistentClient.SendToAsync(new byte[] { 7 }, SocketFlags.None, relay);
            await Task.Delay(200);
            server = NewServer(local, serverPort);
            await Exchange(server, persistentClient, relay, local);
            checks.Add(new { name = "closed-upstream-port-recovery", result = "passed" });
            egress.ResetUdpPeer();
            await Exchange(server, persistentClient, relay, local);
            checks.Add(new { name = "explicit-peer-reset-keeps-listener", result = "passed" });
            evidence["relay"] = egress.Inspect();
            var listener = (Socket)typeof(PhysicalEgress).GetField("_udp", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(egress)!;
            listener.Dispose();
            await Task.Delay(150);
            if (egress.IsHealthy) throw new Exception("Dead receive task was reported healthy");
            checks.Add(new { name = "dead-listener-health-detected", result = "passed" });
            evidence["result"] = "passed";
        }
        catch (Exception ex)
        {
            evidence["result"] = "failed";
            evidence["errorType"] = ex.GetType().Name;
            if (egress is not null)
            {
                var tasks = (Task[])typeof(PhysicalEgress).GetField("_tasks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(egress)!;
                evidence["actualRelayTasks"] = tasks.Select(t => new { state = t.Status.ToString(), error = t.Exception?.GetBaseException().GetType().Name,
                    socketError = (t.Exception?.GetBaseException() as SocketException)?.SocketErrorCode.ToString(), nativeCode = (t.Exception?.GetBaseException() as SocketException)?.NativeErrorCode }).ToArray();
            }
        }
        finally
        {
            server?.Dispose();
            if (egress is not null) await egress.DisposeAsync();
            File.Delete(settings);
            evidence["checks"] = checks;
            evidence["cleanup"] = new { socketsDisposed = true, temporarySettingsRemoved = !File.Exists(settings) };
            File.WriteAllText(output, JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
        }
        Console.WriteLine(JsonSerializer.Serialize(evidence));
        return Equals(evidence["result"], "passed") ? 0 : 1;
    }

    private static Socket NewServer(IPAddress address, int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(address, port));
        return socket;
    }

    private static async Task<IPEndPoint> Exchange(Socket server, Socket client, IPEndPoint relay, IPAddress expectedSource)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var payload = System.Security.Cryptography.RandomNumberGenerator.GetBytes(64);
        await client.SendToAsync(payload, SocketFlags.None, relay, deadline.Token);
        var buffer = new byte[128];
        var received = await server.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), deadline.Token);
        if (!((IPEndPoint)received.RemoteEndPoint).Address.Equals(expectedSource) || !payload.AsSpan().SequenceEqual(buffer.AsSpan(0, received.ReceivedBytes))) throw new Exception("Source binding or payload mismatch");
        await server.SendToAsync(buffer.AsMemory(0, received.ReceivedBytes), SocketFlags.None, received.RemoteEndPoint, deadline.Token);
        var answer = await client.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), deadline.Token);
        if (!payload.AsSpan().SequenceEqual(buffer.AsSpan(0, answer.ReceivedBytes))) throw new Exception("Response mismatch");
        return (IPEndPoint)received.RemoteEndPoint;
    }
}
