using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using EqLink.Core;

namespace EqLink.Tests;

internal static partial class Program
{
    private static async Task SelectorDeadlinesAndHealth(DeviceProfile profile)
    {
        await using var vpn = new TestBackend(profile.OpenVpnPort, false);
        await using var health = new TestBackend(profile.HealthPort, true);
        await using var udp = new TestProxy(FreePort()) { StallNewConnections = true };
        await using var tcp = new TestProxy(FreePort());
        var active = ActivePath.None;
        var port = FreePort();
        await using (var selector = new ProxySelector(profile, () => TransportMode.Auto, (path, _) => { if(path != ActivePath.None) active = path; }, Token, port, udp.Port, tcp.Port))
        {
            selector.Start();
            var deadline = Stopwatch.StartNew();
            await CheckEcho(port, profile.OpenVpnPort);
            Check(active == ActivePath.Tcp && deadline.Elapsed < TimeSpan.FromSeconds(7), "A stalled UDP attempt consumed the TCP candidate deadline.");
        }
        udp.StallNewConnections = false;
        port = FreePort();
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hadConnection = false;
        await using (var selector = new ProxySelector(profile, () => TransportMode.Auto, (path, _) =>
        {
            if (path == ActivePath.Udp) hadConnection = true;
            if (path == ActivePath.None && hadConnection) lost.TrySetResult();
        }, Token, port, udp.Port, tcp.Port))
        {
            selector.Start();
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{profile.OpenVpnPort} HTTP/1.1\r\nX-EqLink-Token: {Token}\r\n\r\n"));
            Check((await ReadHeader(client.GetStream())).StartsWith("HTTP/1.1 200"), "Initial UDP connection failed.");
            udp.StallNewConnections = true; // Leave the active data stream open; only new authenticated health connections time out.
            var elapsed = Stopwatch.StartNew();
            await lost.Task.WaitAsync(TimeSpan.FromSeconds(22));
            Check(elapsed.Elapsed >= TimeSpan.FromSeconds(8), "The active path was closed before two health failures.");
        }
        Console.WriteLine("Independent candidate deadlines and active blackhole detection: passed");
    }
}
