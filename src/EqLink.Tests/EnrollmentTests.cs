using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using EqLink.Core;

namespace EqLink.Tests;

internal static partial class Program
{
    private static void EnrollmentAndHealth()
    {
        using var key = RSA.Create(3072);
        var request = new CertificateRequest("CN=unit-test-root", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var ca = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var grantId = Guid.NewGuid().ToString("N");
        var bundle = new EnrollmentBundle { GrantId = grantId, EnrollmentToken = CredentialSecrets.New(), HysteriaAuth = grantId + ":" + CredentialSecrets.New(), VlessId = Guid.NewGuid(),
            Network = new NetworkConnection { NetworkId = Guid.NewGuid().ToString("N"), PublicHost = "test.invalid", OverlayCidr = "172.21.10.0/24", CaPem = ca.ExportCertificatePem(),
                HysteriaServerName = "test.invalid", HysteriaCertificateSha256 = ConfigRenderer.CertificateFingerprint(ca.ExportCertificatePem()), RealityServerName = "example.com", RealityPublicKey = "test-key", RealityShortId = "0123456789abcdef" } };
        var serialized = EnrollmentBundle.Serialize(bundle);
        var restored = EnrollmentBundle.Parse(serialized);
        Check(restored.Network.NetworkId == bundle.Network.NetworkId, "Universal bundle roundtrip failed");
        var json = Encoding.UTF8.GetString(serialized);
        foreach (var forbidden in new[] { "PRIVATE KEY", "certificatePem", "virtualIp", "passphrase" }) Check(!json.Contains(forbidden), "Universal bundle contains device material");
        try { EnrollmentBundle.Parse("{\"schemaVersion\":1}"u8); throw new Exception("Legacy bundle accepted"); } catch (InvalidDataException) { }
        var first = LocalDeviceIdentity.Create(); var second = LocalDeviceIdentity.Create();
        Check(first.PublicKeyFingerprint != second.PublicKeyFingerprint && first.PrivateKeyPem != second.PrivateKeyPem, "Machines shared an identity");
        var settings = new ServerSettings { PublicHost = "test.invalid", OverlayCidr = "172.21.10.0/24", EnableTcp = false, EnableUdp = true, HysteriaCertificate = "cert", HysteriaPrivateKey = "key", HysteriaServerName = "test.invalid", ReservedAddresses = ["172.21.10.12"] };
        DeviceRecord Device(string ip) => new() { Id = Guid.NewGuid().ToString("N"), Name = "unit", VirtualIp = ip, HysteriaPassword = "test", VlessId = Guid.NewGuid(), CertificatePath = "test", Active = true, AddressAssigned = ip.Length > 0 };
        var a = Device("172.21.10.10"); var b = Device("172.21.10.11"); var c = Device("");
        var state = new ServerState { Settings = settings, Devices = [a,b,c] };
        Check(AddressAllocation.Choose(state,a,new()) == a.VirtualIp, "Auto lease changed on reconnect");
        Check(AddressAllocation.Choose(state,c,new()) == "172.21.10.13", "Reserved address was allocated");
        try { AddressAllocation.Choose(state,c,new((AddressMode)99,"172.21.10.13")); throw new Exception("Undefined address mode accepted"); }
        catch (ArgumentException ex) when (ex.Message == "ADDRESS_MODE_INVALID") { }
        foreach (var ip in new[] { "172.21.10.1", "172.21.10.9", "172.21.10.255", "172.21.11.10", "172.21.10.0xA", "172.21.10.012", "172.21.10.10 " })
        { try { AddressAllocation.Validate(settings.OverlayCidr,ip); throw new Exception("Invalid address accepted"); } catch (ArgumentException) { } }
        foreach (var ip in new[] { a.VirtualIp, "172.21.10.12" })
        { try { AddressAllocation.Choose(state,c,new(AddressMode.Static,ip)); throw new Exception("Occupied address accepted"); } catch (InvalidOperationException) { } }
        var health = new ReachabilityTracker();
        Check(!health.RecordAuthenticatedHealth(true) && health.Verified, "First authenticated health failed");
        Check(!health.RecordAuthenticatedHealth(false) && health.Verified, "One health timeout disconnected the network");
        Check(!health.RecordAuthenticatedHealth(true) && health.Failures == 0, "Success did not reset failure count");
        Check(!health.RecordAuthenticatedHealth(false) && health.RecordAuthenticatedHealth(false), "Two failed checks did not request reconnect");
        var rates = new RateWindow();
        Check(rates.Add("tap-a",0,0,0).Up is null, "First sample displayed zero");
        Check(rates.Add("tap-a",1,3000,5000) == (3000d,5000d), "Traffic direction or monotonic delta incorrect");
        _ = rates.Add("tap-a",2,6000,10000); _ = rates.Add("tap-a",3,9000,15000);
        Check(rates.Add("tap-a",4,12000,20000) == (3000d,5000d), "Three-second average incorrect");
        Check(rates.Add("tap-b",5,90000,200000).Up is null && rates.Add("tap-b",6,0,0).Down is null, "NIC/counter reset reused old baseline");
        Console.WriteLine("Universal bundle, local keys, lease boundaries, authenticated health and statistics: passed");
    }
}
