using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using EqLink.Core;

namespace EqLink.Tests;

internal static partial class Program
{
    // A fresh synthetic token for isolated loopback tests; never a deployed credential.
    private static readonly string Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.FirstOrDefault() == "--hosts-only") { await HostsOverrides(); return 0; }
            if (args.FirstOrDefault() == "--outer-failure-only") { OuterFailures(); return 0; }
            if (args.FirstOrDefault() == "--hysteria-trust-only") { HysteriaTrustChecks(); OuterFailures(); if (args.Length > 1) await HysteriaScopedCaTransport(args[1]); return 0; }
            if (args.Length > 0 && args[0] == "--container-smoke")
            {
                await ContainerSmoke.RunAsync();
                return 0;
            }
            var (cert, key) = TestCertificate();
            var ovpnPort = FreePort();
            var healthPort = FreePort();
            var profile = Profile(cert, key, ovpnPort, healthPort);
            if (args.FirstOrDefault() == "--traffic-only") { await TrafficCounters(profile); return 0; }
            if (args.FirstOrDefault() == "--tls-only") { await ControlTls(profile); return 0; }
            Bundle(profile);
            OuterFailures();
            HysteriaTrustChecks();
            EnrollmentAndHealth();
            await ControlTls(profile);
            await ControlResponseLimits();
            await TrafficTools();
            Config(profile);
            EgressSettings();
            await HostsOverrides();
            await Selector(profile);
            await SelectorDeadlinesAndHealth(profile);
            if (OperatingSystem.IsWindows()) await PhysicalEgressGuards(profile);
            if (OperatingSystem.IsWindows()) await TrafficCounters(profile);
            if (args.Length >= 1) XrayConfig(args[0]);
            if (args.Length >= 2) await HysteriaConfig(args[1], cert, key);
            if (args.Length >= 3 && args[2] == "--real") await RealOuterTests(args[0], args[1], cert, key);
            Console.WriteLine("EqLink tests passed.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static (string Cert, string Key) TestCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        return (cert.ExportCertificatePem(), rsa.ExportPkcs8PrivateKeyPem());
    }

    private static DeviceProfile Profile(string cert, string key, int ovpnPort, int healthPort) => new()
    {
        DeviceId = Guid.NewGuid().ToString("N"), DeviceName = "test-device", VirtualIp = "10.88.0.10",
        OverlayCidr = "10.88.0.0/24", ServerIp = "10.88.0.1", PublicHost = "localhost",
        UdpPort = 443, TcpPort = 443, OpenVpnPort = ovpnPort, HealthPort = healthPort,
        EnableUdp = true, EnableTcp = true, HysteriaServerName = "localhost",
        HysteriaCertificateSha256 = ConfigRenderer.CertificateFingerprint(cert), HysteriaAuth = "test:password",
        VlessId = Guid.NewGuid(), RealityServerName = "www.example.com", RealityPublicKey = "test", RealityShortId = "0123456789abcdef",
        XhttpPath = "/eqlink", CaPem = cert, CertificatePem = cert, PrivateKeyPem = key,
        TlsCryptV2Key = "-----BEGIN OpenVPN tls-crypt-v2 client key-----\nVEVTVA==\n-----END OpenVPN tls-crypt-v2 client key-----\n"
    };

    private static void Bundle(DeviceProfile profile)
    {
        var bytes = BundleCrypto.Encrypt(profile, "test-password-long");
        var restored = BundleCrypto.Decrypt(bytes, "test-password-long");
        Check(restored.DeviceId == profile.DeviceId, "Bundle roundtrip failed.");
        try { BundleCrypto.Decrypt(bytes, "wrong-password"); throw new Exception("Wrong password was accepted."); }
        catch (CryptographicException) { }
        bytes[^20] ^= 1;
        try { BundleCrypto.Decrypt(bytes, "test-password-long"); throw new Exception("Tampered bundle was accepted."); }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException) { }
        Console.WriteLine("Bundle encryption: passed");
    }

    private static void Config(DeviceProfile profile)
    {
        var config = ConfigRenderer.ClientOpenVpn(profile, 18080, Token);
        Check(config.Contains($"remote 127.0.0.1 {profile.OpenVpnPort}") && !config.Contains(profile.PublicHost), "OpenVPN endpoint is not loopback-only.");
        Check(config.Contains("http-proxy 127.0.0.1 18080") && config.Contains("X-EqLink-Token " + Token), "OpenVPN selector is missing.");
        Check(config.Contains("route-nopull") && config.Contains("route 10.88.0.0 255.255.255.0 10.88.0.1"), "Client route restriction is missing.");
        Check(OverlayGuard.Overlaps("10.88.0.0/24", [(IPAddress.Parse("10.88.0.8"), 24)]) &&
              !OverlayGuard.Overlaps("10.88.0.0/24", [(IPAddress.Parse("192.168.1.4"), 24)]), "CIDR collision detection failed.");
        var settings = Settings("private", "public", profile.OpenVpnPort, profile.HealthPort);
        settings.Validate();
        Check(ConfigRenderer.ServerOpenVpn(settings, "/etc/eqlink").Contains("local 127.0.0.1"), "Server OpenVPN is public.");
        using var hy = JsonDocument.Parse(ConfigRenderer.ServerHysteria(settings, []));
        var acl = hy.RootElement.GetProperty("acl").GetProperty("inline");
        Check(acl[acl.GetArrayLength() - 1].GetString() == "reject(all)", "Hysteria default deny is absent.");
        using var xr = JsonDocument.Parse(ConfigRenderer.ServerXray(settings, []));
        var rules = xr.RootElement.GetProperty("routing").GetProperty("rules");
        Check(rules[rules.GetArrayLength() - 1].GetProperty("outboundTag").GetString() == "blocked" &&
              xr.RootElement.GetProperty("outbounds")[1].GetProperty("protocol").GetString() == "blackhole", "Xray default deny is absent.");
        Console.WriteLine("Configuration invariants: passed");
    }

    private static ServerSettings Settings(string privateKey, string publicKey, int ovpnPort, int healthPort) => new()
    {
        PublicHost = "localhost", UdpPort = 2443, TcpPort = 3443, OpenVpnPort = ovpnPort, HealthPort = healthPort,
        HysteriaCertificate = "cert.pem", HysteriaPrivateKey = "key.pem", HysteriaServerName = "localhost",
        RealityTarget = "www.example.com:443", RealityServerName = "www.example.com",
        RealityPrivateKey = privateKey, RealityPublicKey = publicKey, RealityShortId = "0123456789abcdef"
    };

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task Selector(DeviceProfile profile)
    {
        await using var vpn = new TestBackend(profile.OpenVpnPort, false);
        await using var health = new TestBackend(profile.HealthPort, true);
        await using var udp = new TestProxy(FreePort());
        await using var tcp = new TestProxy(FreePort());
        var selectorPort = FreePort();
        var mode = TransportMode.Auto;
        ActivePath active = ActivePath.None;
        await using var selector = new ProxySelector(profile, () => mode, (path, _) => { if (path != ActivePath.None) active = path; }, Token, selectorPort, udp.Port, tcp.Port);
        selector.Start();
        await CheckEcho(selectorPort, profile.OpenVpnPort);
        Check(active == ActivePath.Udp, "Auto mode did not prefer UDP.");
        udp.Enabled = false;
        selector.CloseActive();
        await Task.Delay(100);
        await CheckEcho(selectorPort, profile.OpenVpnPort);
        Check(active == ActivePath.Tcp, "Auto mode did not fail over to TCP.");
        mode = TransportMode.UdpOnly;
        selector.CloseActive();
        await Task.Delay(100);
        Check(await ConnectStatus(selectorPort, profile.OpenVpnPort, Token) == 502, "UDP-only mode used TCP.");
        Check(await ConnectStatus(selectorPort, 1, Token) == 403, "Selector allowed a different target.");
        Check(await ConnectStatus(selectorPort, profile.OpenVpnPort, "bad") == 403, "Selector accepted a bad token.");
        Console.WriteLine("Selector failover and target restrictions: passed");
    }

    private static async Task CheckEcho(int selectorPort, int targetPort)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, selectorPort);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{targetPort} HTTP/1.1\r\nX-EqLink-Token: {Token}\r\n\r\n"));
        var header = await ReadHeader(stream);
        Check(header.StartsWith("HTTP/1.1 200"), "Selector could not connect through outer path: " + header);
        await stream.WriteAsync("test"u8.ToArray());
        var response = new byte[4];
        await stream.ReadExactlyAsync(response);
        Check(response.AsSpan().SequenceEqual("test"u8), "VPN payload was not forwarded.");
    }

    private static async Task<int> ConnectStatus(int selectorPort, int targetPort, string token)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, selectorPort);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{targetPort} HTTP/1.1\r\nX-EqLink-Token: {token}\r\n\r\n"));
        return int.Parse((await ReadHeader(stream)).Split(' ')[1]);
    }

    private static async Task<string> ReadHeader(NetworkStream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var bytes = new List<byte>();
        var b = new byte[1];
        while (bytes.Count < 8192)
        {
            await stream.ReadExactlyAsync(b, timeout.Token);
            bytes.Add(b[0]);
            var n = bytes.Count;
            if (n >= 4 && bytes[n - 4] == 13 && bytes[n - 3] == 10 && bytes[n - 2] == 13 && bytes[n - 1] == 10)
                return Encoding.ASCII.GetString(bytes.ToArray());
        }
        throw new IOException("Header too long.");
    }

    private static void XrayConfig(string exe)
    {
        var keys = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "x25519") { RedirectStandardOutput = true, UseShellExecute = false })!;
        var output = keys.StandardOutput.ReadToEnd();
        keys.WaitForExit();
        var privateKey = output.Split('\n').Single(x => x.StartsWith("PrivateKey:")).Split(':', 2)[1].Trim();
        var publicKey = output.Split('\n').Single(x => x.StartsWith("Password (PublicKey):")).Split(':', 2)[1].Trim();
        var config = ConfigRenderer.ServerXray(Settings(privateKey, publicKey, 1194, 1195), []);
        var path = Path.Combine(Path.GetTempPath(), "eqlink-test-xray-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, config);
        try
        {
            var test = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) {
                ArgumentList = { "run", "-test", "-c", path }, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false })!;
            var error = test.StandardError.ReadToEnd();
            test.WaitForExit();
            Check(test.ExitCode == 0, "Xray rejected generated server config: " + error);
        }
        finally { File.Delete(path); }
        Console.WriteLine("Xray parser: passed");
    }

    private static async Task HysteriaConfig(string exe, string cert, string key)
    {
        var dir = Path.Combine(Path.GetTempPath(), "eqlink-test-hy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var certFile = Path.Combine(dir, "cert.pem");
            var keyFile = Path.Combine(dir, "key.pem");
            File.WriteAllText(certFile, cert);
            File.WriteAllText(keyFile, key);
            var settings = Settings("private", "public", 1194, 1195) with { UdpPort = FreePort(), HysteriaCertificate = certFile, HysteriaPrivateKey = keyFile };
            var configFile = Path.Combine(dir, "hysteria.json");
            var device = new DeviceRecord { Id = "parser-test", Name = "parser-test", VirtualIp = "10.88.0.10", HysteriaPassword = "parser-test-password", VlessId = Guid.NewGuid(), CertificatePath = "unused", Active = true };
            File.WriteAllText(configFile, ConfigRenderer.ServerHysteria(settings, [device]));
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) {
                ArgumentList = { "server", "-c", configFile }, RedirectStandardError = true, UseShellExecute = false })!;
            try
            {
                await Task.Delay(1200);
                if (process.HasExited)
                    throw new Exception("Hysteria rejected generated server config: " + await process.StandardError.ReadToEndAsync());
            }
            finally
            {
                if (!process.HasExited) process.Kill(true);
                await process.WaitForExitAsync();
            }
        }
        finally { Directory.Delete(dir, true); }
        Console.WriteLine("Hysteria parser: passed");
    }
}

internal sealed class TestBackend : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _task;
    private readonly bool _health;
    public TestBackend(int port, bool health)
    {
        _health = health;
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
        {
            var stream = client.GetStream();
            var buffer = new byte[8192];
            try
            {
                int count;
                while ((count = await stream.ReadAsync(buffer, _stop.Token)) > 0)
                {
                    if (_health) await stream.WriteAsync("PONG\n"u8.ToArray(), _stop.Token);
                    else await stream.WriteAsync(buffer.AsMemory(0, count), _stop.Token);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        }
    }
    public async ValueTask DisposeAsync() { _stop.Cancel(); _listener.Stop(); await _task; _stop.Dispose(); }
}

internal sealed class TestProxy : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _task;
    public int Port { get; }
    public bool Enabled { get; set; } = true;
    public bool StallNewConnections { get; set; }
    public TestProxy(int port)
    {
        Port = port;
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
        {
            try
            {
                var stream = client.GetStream();
                var header = new List<byte>();
                var b = new byte[1];
                while (header.Count < 8192)
                {
                    await stream.ReadExactlyAsync(b, _stop.Token);
                    header.Add(b[0]);
                    var n = header.Count;
                    if (n >= 4 && header[n - 4] == 13 && header[n - 3] == 10 && header[n - 2] == 13 && header[n - 1] == 10) break;
                }
                if (!Enabled) { await stream.WriteAsync("HTTP/1.1 502 Bad Gateway\r\n\r\n"u8.ToArray()); return; }
                if (StallNewConnections) { await Task.Delay(Timeout.Infinite, _stop.Token); return; }
                var request = Encoding.ASCII.GetString(header.ToArray()).Split(' ')[1];
                var port = int.Parse(request.Split(':')[1]);
                using var target = new TcpClient();
                await target.ConnectAsync(IPAddress.Loopback, port, _stop.Token);
                await stream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray());
                using var relayStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                var a = stream.CopyToAsync(target.GetStream(), relayStop.Token);
                var c = target.GetStream().CopyToAsync(stream, relayStop.Token);
                await Task.WhenAny(a, c);
                relayStop.Cancel();
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { }
        }
    }
    public async ValueTask DisposeAsync() { _stop.Cancel(); _listener.Stop(); await _task; _stop.Dispose(); }
}
