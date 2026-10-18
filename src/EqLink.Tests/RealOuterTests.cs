using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using EqLink.Core;

namespace EqLink.Tests;

internal static partial class Program
{
    private static async Task RealOuterTests(string xrayExe, string hysteriaExe, string cert, string key)
    {
        var dir = Path.Combine(Path.GetTempPath(), "eqlink-real-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var processes = new List<Process>();
        var logs = new Dictionary<int, StringBuilder>();
        try
        {
            var ovpnPort = FreePort();
            var healthPort = FreePort();
            var udpPort = FreePort();
            var tcpPort = FreePort();
            var hyProxyPort = FreePort();
            var xrProxyPort = FreePort();
            var selectorPort = FreePort();
            var forbiddenPort = FreePort();
            await using var vpn = new TestBackend(ovpnPort, false);
            await using var health = new TestBackend(healthPort, true);
            await using var forbidden = new TestBackend(forbiddenPort, false);
            var pairProcess = Process.Start(new ProcessStartInfo(xrayExe, "x25519") { RedirectStandardOutput = true, UseShellExecute = false })!;
            var pair = await pairProcess.StandardOutput.ReadToEndAsync();
            await pairProcess.WaitForExitAsync();
            var privateKey = pair.Split('\n').Single(x => x.StartsWith("PrivateKey:")).Split(':', 2)[1].Trim();
            var publicKey = pair.Split('\n').Single(x => x.StartsWith("Password (PublicKey):")).Split(':', 2)[1].Trim();
            var certFile = Path.Combine(dir, "cert.pem");
            var keyFile = Path.Combine(dir, "key.pem");
            File.WriteAllText(certFile, cert);
            File.WriteAllText(keyFile, key);
            var settings = Settings(privateKey, publicKey, ovpnPort, healthPort) with {
                PublicHost = "127.0.0.1", UdpPort = udpPort, TcpPort = tcpPort,
                HysteriaCertificate = certFile, HysteriaPrivateKey = keyFile,
                RealityTarget = "www.cloudflare.com:443", RealityServerName = "www.cloudflare.com"
            };
            var baseProfile = Profile(cert, key, ovpnPort, healthPort);
            var profile = baseProfile with {
                PublicHost = "127.0.0.1", UdpPort = udpPort, TcpPort = tcpPort,
                HysteriaAuth = baseProfile.DeviceId + ":testpassword", VlessId = Guid.NewGuid(),
                RealityServerName = settings.RealityServerName, RealityPublicKey = publicKey,
                RealityShortId = settings.RealityShortId
            };
            var device = new DeviceRecord { Id = profile.DeviceId, Name = profile.DeviceName, VirtualIp = profile.VirtualIp,
                HysteriaPassword = "testpassword", VlessId = profile.VlessId, CertificatePath = certFile, Active = true };
            var hyServer = JsonNode.Parse(ConfigRenderer.ServerHysteria(settings, [device]))!;
            hyServer["listen"] = "127.0.0.1:" + udpPort;
            var xrServer = JsonNode.Parse(ConfigRenderer.ServerXray(settings, [device]))!;
            xrServer["inbounds"]![0]!["listen"] = "127.0.0.1";
            xrServer["log"]!["loglevel"] = "debug";
            var hyClient = JsonNode.Parse(ConfigRenderer.ClientHysteria(profile, hyProxyPort))!;
            hyClient["tls"]!["ca"] = certFile;
            var xrClient = JsonNode.Parse(ConfigRenderer.ClientXray(profile, xrProxyPort))!;
            xrClient["log"]!["loglevel"] = "debug";
            var hyServerFile = Write("hy-server.json", hyServer.ToJsonString());
            var xrServerFile = Write("xr-server.json", xrServer.ToJsonString());
            var hyClientFile = Write("hy-client.json", hyClient.ToJsonString());
            var xrClientFile = Write("xr-client.json", xrClient.ToJsonString());
            processes.Add(Start(hysteriaExe, "server", "-c", hyServerFile));
            processes.Add(Start(xrayExe, "run", "-c", xrServerFile));
            processes.Add(Start(hysteriaExe, "client", "-c", hyClientFile));
            processes.Add(Start(xrayExe, "run", "-c", xrClientFile));
            await Task.Delay(1000);
            await WaitConnect(hyProxyPort, healthPort, "Hysteria 2");
            await WaitConnect(xrProxyPort, healthPort, "Xray XHTTP REALITY");
            Check(!await ProxyAllowsEcho(hyProxyPort, forbiddenPort), "Hysteria allowed arbitrary destination.");
            Check(!await ProxyAllowsEcho(xrProxyPort, forbiddenPort), "Xray allowed arbitrary destination.");
            var mode = TransportMode.Auto;
            var active = ActivePath.None;
            await using var selector = new ProxySelector(profile, () => mode, (path, _) => { if (path != ActivePath.None) active = path; },
                Token, selectorPort, hyProxyPort, xrProxyPort);
            selector.Start();
            await CheckEcho(selectorPort, ovpnPort);
            Check(active == ActivePath.Udp, "Real Hysteria path was not preferred.");
            processes[2].Kill(true);
            await processes[2].WaitForExitAsync();
            selector.CloseActive();
            await Task.Delay(200);
            await CheckEcho(selectorPort, ovpnPort);
            Check(active == ActivePath.Tcp, "Real outer failover did not reach Xray.");
            Console.WriteLine("Real Hysteria/Xray outer transport and failover: passed");

            string Write(string name, string value) { var path = Path.Combine(dir, name); File.WriteAllText(path, value); return path; }
            Process Start(string exe, params string[] arguments)
            {
                var info = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
                foreach (var arg in arguments) info.ArgumentList.Add(arg);
                var process = Process.Start(info)!;
                var log = new StringBuilder();
                logs[process.Id] = log;
                process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.AppendLine(e.Data); };
                process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.AppendLine(e.Data); };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                return process;
            }
        }
        catch (Exception ex)
        {
            var detail = string.Join("\n", processes.Select(p => { var log = logs.GetValueOrDefault(p.Id); return Path.GetFileName(p.StartInfo.FileName) + ": " + log?.ToString(); }));
            throw new Exception(ex.Message + "\nOuter logs:\n" + detail, ex);
        }
        finally
        {
            foreach (var process in processes)
            {
                try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
                process.Dispose();
            }
            Directory.Delete(dir, true);
        }
    }

    private static async Task WaitConnect(int proxyPort, int targetPort, string name)
    {
        for (var i = 0; i < 4; i++)
        {
            try
            {
                if (await ProxyHealth(proxyPort, targetPort)) return;
            }
            catch (Exception ex) when (ex is SocketException or IOException) { }
            await Task.Delay(500);
        }
        throw new Exception(name + " could not connect to loopback health backend.");
    }

    private static async Task<int> ProxyStatus(int proxyPort, int targetPort)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxyPort);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{targetPort} HTTP/1.1\r\nHost: 127.0.0.1:{targetPort}\r\n\r\n"));
        return int.Parse((await ReadHeader(stream)).Split(' ')[1]);
    }

    private static async Task<bool> ProxyHealth(int proxyPort, int targetPort)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await client.ConnectAsync(IPAddress.Loopback, proxyPort, timeout.Token);
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{targetPort} HTTP/1.1\r\nHost: 127.0.0.1:{targetPort}\r\n\r\n"), timeout.Token);
            if (!(await ReadHeader(stream)).StartsWith("HTTP/1.1 200")) return false;
            await stream.WriteAsync("PING\n"u8.ToArray(), timeout.Token);
            var reply = new byte[5];
            await stream.ReadExactlyAsync(reply, timeout.Token);
            return reply.AsSpan().SequenceEqual("PONG\n"u8);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { return false; }
    }

    private static async Task<bool> ProxyAllowsEcho(int proxyPort, int targetPort)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(IPAddress.Loopback, proxyPort, timeout.Token);
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{targetPort} HTTP/1.1\r\nHost: 127.0.0.1:{targetPort}\r\n\r\n"), timeout.Token);
            if (!(await ReadHeader(stream)).StartsWith("HTTP/1.1 200")) return false;
            await stream.WriteAsync("test"u8.ToArray(), timeout.Token);
            var reply = new byte[4];
            await stream.ReadExactlyAsync(reply, timeout.Token);
            return reply.AsSpan().SequenceEqual("test"u8);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { return false; }
    }
}

internal sealed class TestTlsTarget : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _task;
    private readonly X509Certificate2 _certificate;
    public int Port { get; }
    public TestTlsTarget(int port, string cert, string key)
    {
        Port = port;
        _certificate = X509Certificate2.CreateFromPem(cert, key);
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        _task = Run();
    }
    private async Task Run()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (OperationCanceledException) { break; }
            _ = Handle(client);
        }
    }
    private async Task Handle(TcpClient client)
    {
        using (client)
        using (var ssl = new SslStream(client.GetStream(), false))
        {
            try
            {
                await ssl.AuthenticateAsServerAsync(_certificate, false, System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13, false);
                var buffer = new byte[4096];
                while (await ssl.ReadAsync(buffer, _stop.Token) > 0) { }
            }
            catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException or OperationCanceledException) { }
        }
    }
    public async ValueTask DisposeAsync() { _stop.Cancel(); _listener.Stop(); await _task; _certificate.Dispose(); _stop.Dispose(); }
}
