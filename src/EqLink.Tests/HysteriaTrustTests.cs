using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using EqLink.Core;

namespace EqLink.Tests;

internal static partial class Program
{
    private static X509Certificate2 TrustRoot(string name, DateTimeOffset? until = null)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), until ?? DateTimeOffset.UtcNow.AddDays(2));
    }

    private static void HysteriaTrustChecks()
    {
        using var root = TrustRoot("EqLink-scoped-root-" + Guid.NewGuid().ToString("N"));
        var pem = root.ExportCertificatePem();
        Check(HysteriaTrust.Fingerprints(pem).Single() == Convert.ToHexString(SHA256.HashData(root.RawData)), "Embedded CA fingerprint is incorrect");
        Check(HysteriaTrust.Equivalent(pem, "\n" + pem + "\n"), "Equivalent PEM formatting changed trust identity");
        using var expired = TrustRoot("EqLink-expired-test-root", DateTimeOffset.UtcNow.AddDays(-1));
        var (leaf, _) = TestCertificate();
        foreach (var bad in new[] { pem + "\n-----BEGIN PRIVATE KEY-----\nVEVTVA==\n-----END PRIVATE KEY-----", pem + "unexpected material", leaf, expired.ExportCertificatePem(), string.Concat(Enumerable.Repeat(pem + "\n", 9)) })
        {
            try { HysteriaTrust.Validate(bad); throw new Exception("Invalid CA material accepted"); }
            catch (Exception ex) when (ex is ArgumentException or CryptographicException) { }
        }
        var network = new NetworkConnection { NetworkId = Guid.NewGuid().ToString("N"), PublicHost = "test.invalid", OverlayCidr = "172.30.251.0/24", CaPem = pem, EnableTcp = false, HysteriaServerName = "test.invalid", HysteriaCertificateSha256 = ConfigRenderer.CertificateFingerprint(pem), HysteriaCaPem = pem };
        var grant = Guid.NewGuid().ToString("N");
        var bundle = new EnrollmentBundle { Network = network, GrantId = grant, EnrollmentToken = CredentialSecrets.New(), HysteriaAuth = grant + ":" + CredentialSecrets.New(), VlessId = Guid.NewGuid() };
        var restored = EnrollmentBundle.Parse(EnrollmentBundle.Serialize(bundle));
        Check(restored.Network.HysteriaCaPem == pem, "Universal bundle lost the public HY2 CA");
        var legacyJson = JsonNode.Parse(EnrollmentBundle.Serialize(bundle))!;
        legacyJson["network"]!.AsObject().Remove("hysteriaCaPem");
        var legacy = EnrollmentBundle.Parse(Encoding.UTF8.GetBytes(legacyJson.ToJsonString()));
        Check(legacy.Network.HysteriaCaPem == "", "Existing v2 bundle became incompatible");
        Check(legacy.Network.RetainHysteriaCa(pem).Bootstrap(bundle.HysteriaAuth, bundle.VlessId, "fixture").HysteriaCaPem == pem, "Legacy server response erased imported CA trust");
        var profile = restored.Network.Bootstrap(bundle.HysteriaAuth, bundle.VlessId, "fixture");
        var path = Path.GetFullPath(Path.Combine("artifacts", "test-temp", "scoped ca.pem"));
        var rendered = JsonNode.Parse(ConfigRenderer.ClientHysteria(profile, 18081, path))!;
        Check(rendered["tls"]!["ca"]!.GetValue<string>() == path && !rendered["tls"]!["insecure"]!.GetValue<bool>() && rendered["tls"]!["pinSHA256"]!.GetValue<string>() == network.HysteriaCertificateSha256, "Scoped CA weakened normal TLS or leaf pin verification");
        try { ConfigRenderer.ClientHysteria(profile, 18081); throw new Exception("Embedded CA silently fell back to system trust"); } catch (ArgumentException) { }
        Console.WriteLine("Universal public CA, validation, legacy compatibility and TLS restrictions: passed");
    }

    private static async Task HysteriaScopedCaTransport(string executable)
    {
        var parent = Path.GetFullPath(Path.Combine("artifacts", "test-temp"));
        Directory.CreateDirectory(parent);
        var directory = Path.Combine(parent, "scoped-ca-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var workers = new List<Process>();
        var drains = new List<Task>();
        try
        {
            using var root = TrustRoot("EqLink-native-scoped-root-" + Guid.NewGuid().ToString("N"));
            using var otherRoot = TrustRoot("EqLink-wrong-test-root");
            if (OperatingSystem.IsWindows())
            {
                using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
                store.Open(OpenFlags.ReadOnly);
                Check(!store.Certificates.Any(c => c.RawData.AsSpan().SequenceEqual(root.RawData)), "Test root unexpectedly exists in machine trust");
            }
            using var serverKey = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
            var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); request.CertificateExtensions.Add(san.Build());
            using var leaf = request.Create(root, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), RandomNumberGenerator.GetBytes(16));
            var cert = leaf.ExportCertificatePem(); var key = serverKey.ExportPkcs8PrivateKeyPem();
            var certPath = Path.Combine(directory, "server.pem"); var keyPath = Path.Combine(directory, "server.key"); var caPath = Path.Combine(directory, "ca.pem");
            File.WriteAllText(certPath, cert); File.WriteAllText(keyPath, key); File.WriteAllText(caPath, root.ExportCertificatePem());
            var healthPort = FreePort(); var vpnPort = FreePort(); var udpPort = FreePort();
            await using var backend = new TestBackend(healthPort, true);
            var profile = Profile(cert, key, vpnPort, healthPort) with { PublicHost = "127.0.0.1", UdpPort = udpPort, HysteriaAuth = "fixture:scoped-test-password", HysteriaCaPem = root.ExportCertificatePem() };
            var settings = Settings("unused", "unused", vpnPort, healthPort) with { EnableTcp = false, UdpPort = udpPort, HysteriaCertificate = certPath, HysteriaPrivateKey = keyPath };
            var device = new DeviceRecord { Id = "fixture", Name = "fixture", VirtualIp = "172.30.251.10", HysteriaPassword = "scoped-test-password", VlessId = Guid.NewGuid(), CertificatePath = certPath, Active = true };
            var server = JsonNode.Parse(ConfigRenderer.ServerHysteria(settings, [device]))!; server["listen"] = "127.0.0.1:" + udpPort;
            var serverPath = Path.Combine(directory, "server.json"); File.WriteAllText(serverPath, server.ToJsonString());
            _ = Start("server", serverPath, new StringBuilder());
            var port = FreePort(); var clientPath = Path.Combine(directory, "client.json");
            File.WriteAllText(clientPath, ConfigRenderer.ClientHysteria(profile, port, caPath));
            _ = Start("client", clientPath, new StringBuilder());
            await WaitConnect(port, healthPort, "Hysteria with bundle-only CA");
            Check(true, "Hysteria trusted a bundle-only CA without OS root installation");
            foreach (var (name, negative, expected) in new[] {
                ("wrong-ca", profile with { HysteriaCaPem = otherRoot.ExportCertificatePem() }, "OUTER_CA_UNTRUSTED"),
                ("wrong-pin", profile with { HysteriaCertificateSha256 = string.Join(':', Enumerable.Repeat("00", 32)) }, "OUTER_CERT_PIN_MISMATCH") })
            {
                var negativeCa = Path.Combine(directory, name + ".pem"); File.WriteAllText(negativeCa, negative.HysteriaCaPem);
                var path = Path.Combine(directory, name + ".json"); File.WriteAllText(path, ConfigRenderer.ClientHysteria(negative, FreePort(), negativeCa));
                var log = new StringBuilder(); var firstDrain = drains.Count; var child = Start("client", path, log);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await child.WaitForExitAsync(timeout.Token);
                await Task.WhenAll(drains.Skip(firstDrain));
                Check(child.ExitCode != 0 && OuterFailureClassifier.Classify(log.ToString())?.Code == expected, "Hysteria accepted an invalid CA or leaf pin: " + name);
            }
            Console.WriteLine("Native Hysteria bundle-only CA, wrong-CA and wrong-pin checks: passed");

            Process Start(string mode, string config, StringBuilder log)
            {
                var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                info.ArgumentList.Add(mode); info.ArgumentList.Add("-c"); info.ArgumentList.Add(config);
                var process = Process.Start(info)!; workers.Add(process);
                drains.Add(Drain(process.StandardOutput)); drains.Add(Drain(process.StandardError));
                return process;
                async Task Drain(StreamReader reader) { string? line; while ((line = await reader.ReadLineAsync()) is not null) lock (log) log.AppendLine(line); }
            }
        }
        finally
        {
            foreach (var worker in workers) { if (!worker.HasExited) worker.Kill(true); await worker.WaitForExitAsync(); worker.Dispose(); }
            await Task.WhenAll(drains);
            if (!Path.GetFullPath(directory).StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unsafe test cleanup path");
            Directory.Delete(directory, true);
        }
    }
}
