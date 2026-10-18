using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EqLink.Core;

namespace EqLink.Tests;

internal static class ContainerSmoke
{
    private static readonly List<(Process Process, Task<string> Stdout, Task<string> Stderr, string Name)> Processes = [];
    private const string Password = "container-smoke-test-only-password";
    private static readonly string Work = "/tmp/eqlink-smoke";

    internal static async Task RunAsync()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Run this smoke test inside an isolated Linux test namespace.");
        Directory.CreateDirectory(Work);
        Directory.CreateDirectory("/run/eqlink-bootstrap");
        Directory.CreateDirectory("/run/eqlink-tls");
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); request.CertificateExtensions.Add(san.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        File.WriteAllText("/run/eqlink-tls/tls.crt", certificate.ExportCertificatePem());
        File.WriteAllText("/run/eqlink-tls/tls.key", key.ExportPkcs8PrivateKeyPem());
        var keys = Run("xray", "x25519").Output.Split('\n');
        var privateKey = keys.Single(x => x.StartsWith("PrivateKey:")).Split(':', 2)[1].Trim();
        var publicKey = keys.Single(x => x.StartsWith("Password (PublicKey):")).Split(':', 2)[1].Trim();
        var settings = new ServerSettings { PublicHost = "127.0.0.1", TcpPort = 30443, UdpPort = 30444,
            HysteriaCertificate = "/run/eqlink-tls/tls.crt", HysteriaPrivateKey = "/run/eqlink-tls/tls.key", HysteriaServerName = "localhost",
            RealityTarget = "127.0.0.1:9443", RealityServerName = "localhost", RealityPrivateKey = privateKey, RealityPublicKey = publicKey,
            RealityShortId = "0123456789abcdef" };
        File.WriteAllText("/run/eqlink-bootstrap/server-settings.json", ConfigRenderer.Serialize(settings));
        try
        {
            Start("target", "openssl", "s_server", "-accept", "127.0.0.1:9443", "-cert", settings.HysteriaCertificate, "-key", settings.HysteriaPrivateKey, "-quiet", "-tls1_3");
            var supervisor = Start("supervisor", "EqLink.Server", "serve", "--settings", "/run/eqlink-bootstrap/server-settings.json");
            await ReadyAsync();
            for (var i = 0; i < 10 && !AllListeners(); i++) await Task.Delay(500);
            Require(AllListeners(), "A fresh zero-device server did not keep both outer listeners available");
            Console.WriteLine("ARM64 supervisor, OpenVPN TUN and loopback listeners: passed");
            File.WriteAllText(Work + "/password", Password);
            File.SetUnixFileMode(Work + "/password", UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var id = Guid.NewGuid().ToString("N");
            Run("EqLink.Server", "add", "--id", id, "--name", "smoke-device", "--bundle", Work + "/device.eqlink", "--passphrase-file", Work + "/password");
            var profile = BundleCrypto.Decrypt(File.ReadAllBytes(Work + "/device.eqlink"), Password);
            var inspection = Run("EqLink.Server", "inspect", "--json").Output;
            File.WriteAllText(Work + "/inspection.json",inspection);
            using (var parsed = JsonDocument.Parse(inspection))
                Require(parsed.RootElement.GetProperty("checks").EnumerateObject().All(c=>c.Value.GetBoolean()), "Runtime inspection invariants failed: "+string.Join(", ",parsed.RootElement.GetProperty("checks").EnumerateObject().Where(c=>!c.Value.GetBoolean()).Select(c=>c.Name)));
            foreach (var secret in new[] { profile.HysteriaAuth.Split(':')[1], profile.VlessId.ToString(), privateKey, profile.PrivateKeyPem, profile.TlsCryptV2Key, File.ReadAllText("/etc/eqlink/pki/management.password").Trim() })
                Require(!inspection.Contains(secret,StringComparison.Ordinal), "Inspection exposed a credential");
            File.WriteAllText(Work + "/inspection.json",inspection);
            Run("EqLink.Server", "backup", "--output", Work + "/state-backup.tar.gz");
            Require(File.GetUnixFileMode(Work + "/state-backup.tar.gz")== (UnixFileMode.UserRead|UnixFileMode.UserWrite), "Backup permissions are not private");
            Console.WriteLine("ARM64 runtime inspection, secret whitelist and private backup: passed");
            Require(profile.VirtualIp == "10.88.0.10", "Unexpected fixed virtual address");
            Require(File.ReadAllText("/etc/eqlink/ccd/device-" + id).Contains("10.88.0.10"), "Missing fixed CCD address");
            var hy = JsonNode.Parse(ConfigRenderer.ClientHysteria(profile, 18081))!;
            // Test-only self-signed certificate; the shipped renderer keeps certificate validation enabled.
            hy["tls"]!["insecure"] = true;
            File.WriteAllText(Work + "/hy.json", hy.ToJsonString());
            File.WriteAllText(Work + "/xr.json", ConfigRenderer.ClientXray(profile, 18082));
            Start("hy-client", "hysteria", "client", "-c", Work + "/hy.json");
            Start("xr-client", "xray", "run", "-c", Work + "/xr.json");
            await Task.Delay(1500);
            Require(await HealthThroughAsync(18081, settings.HealthPort), "HY2 could not reach the permitted health target");
            Require(await HealthThroughAsync(18082, settings.HealthPort), "XHTTP/REALITY could not reach the permitted health target");
            Console.WriteLine("ARM64 add/reload and real HY2/XHTTP-REALITY authentication: passed");
            await using var forbidden = new TestBackend(18099, true);
            Require(!await HealthThroughAsync(18081, 18099), "HY2 allowed a forbidden target");
            Require(!await HealthThroughAsync(18082, 18099), "Xray allowed a forbidden target");
            Console.WriteLine("ARM64 outer target restrictions: passed");
            using (var status = JsonDocument.Parse(Run("EqLink.Server", "probe", "--kind", "status").Output))
            {
                var hyPid = status.RootElement.GetProperty("services").EnumerateArray().Single(s => s.GetProperty("name").GetString() == "hysteria").GetProperty("pid").GetInt32();
                Process.GetProcessById(hyPid).Kill();
            }
            await Task.Delay(1000);
            Require(Run("EqLink.Server", "probe", "--kind", "ready", allowFailure: true).Code == 0, "UDP failure incorrectly removed TCP readiness");
            Require(await HealthThroughAsync(18082, settings.HealthPort), "TCP path was lost after UDP failure");
            Console.WriteLine("ARM64 readiness with one failed outer path: passed");
            Run("EqLink.Server", "revoke", "--id", id);
            Require(AllListeners(), "Revoking the last device stopped an outer listener");
            Require(!await HealthThroughAsync(18081, settings.HealthPort), "Revoked HY2 credentials were still accepted");
            Require(!await HealthThroughAsync(18082, settings.HealthPort), "Revoked VLESS credentials were still accepted");
            Require(!File.Exists("/etc/eqlink/ccd/device-" + id), "Revoked CCD remains");
            Console.WriteLine("ARM64 revocation and outer reload: passed");
            // SIGTERM is used rather than a forced kill to verify child cleanup.
            Run("kill", "-TERM", supervisor.Id.ToString());
            await supervisor.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Require(supervisor.ExitCode == 0, "Supervisor did not stop cleanly");
            Require(!File.Exists("/run/eqlink/control.sock"), "Control socket survived shutdown");
            var stateBefore = File.ReadAllText("/etc/eqlink/state.json");
            Start("restart", "EqLink.Server", "serve", "--settings", "/run/eqlink-bootstrap/server-settings.json");
            await ReadyAsync();
            Require(File.ReadAllText("/etc/eqlink/state.json") == stateBefore, "Restart changed persistent devices/addresses");
            Console.WriteLine("ARM64 graceful shutdown and persistent restart: passed");
            Console.WriteLine("ARM64 container smoke passed.");
        }
        finally
        {
            foreach (var item in Processes.AsEnumerable().Reverse())
            {
                if (!item.Process.HasExited) item.Process.Kill(true);
                await item.Process.WaitForExitAsync();
                File.WriteAllText(Work + "/" + item.Name + ".log", await item.Stdout + "\n" + await item.Stderr);
                item.Process.Dispose();
            }
        }
    }

    private static Process Start(string name, string executable, params string[] arguments)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        var process = Process.Start(info)!;
        Processes.Add((process, process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync(), name));
        return process;
    }

    private static (int Code, string Output) Run(string executable, params string[] arguments) => Run(executable, arguments, false);
    private static (int Code, string Output) Run(string executable, string a, string b, string c, bool allowFailure) => Run(executable, [a, b, c], allowFailure);
    private static (int Code, string Output) Run(string executable, string[] arguments, bool allowFailure)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120000)) { process.Kill(true); throw new TimeoutException(executable); }
        if (process.ExitCode != 0 && !allowFailure) throw new IOException(executable + ": " + error.GetAwaiter().GetResult());
        return (process.ExitCode, output.GetAwaiter().GetResult());
    }

    private static async Task ReadyAsync()
    {
        for (var i = 0; i < 90; i++)
        {
            var current = Processes.Last(p => p.Name is "supervisor" or "restart");
            if (current.Process.HasExited)
                throw new IOException("Supervisor exited during startup: " + await current.Stdout + "\n" + await current.Stderr);
            if (Run("EqLink.Server", ["probe", "--kind", "ready"], true).Code == 0) return;
            await Task.Delay(1000);
        }
        throw new TimeoutException("Supervisor did not become ready");
    }

    private static bool AllListeners()
    {
        using var status = JsonDocument.Parse(Run("EqLink.Server", "probe", "--kind", "status").Output);
        return status.RootElement.GetProperty("services").EnumerateArray().All(s => s.GetProperty("listening").GetBoolean());
    }

    private static async Task<bool> HealthThroughAsync(int proxyPort, int targetPort)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, proxyPort, stop.Token);
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{targetPort} HTTP/1.1\r\nHost: 127.0.0.1:{targetPort}\r\n\r\n"), stop.Token);
            var header = new List<byte>(); var one = new byte[1];
            while (header.Count < 8192)
            {
                await stream.ReadExactlyAsync(one, stop.Token); header.Add(one[0]);
                if (header.Count >= 4 && header.TakeLast(4).SequenceEqual("\r\n\r\n"u8.ToArray())) break;
            }
            if (!Encoding.ASCII.GetString(header.ToArray()).Contains(" 200 ")) return false;
            await stream.WriteAsync("PING\n"u8.ToArray(), stop.Token);
            var response = new byte[5]; await stream.ReadExactlyAsync(response, stop.Token);
            return response.AsSpan().SequenceEqual("PONG\n"u8);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { return false; }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
