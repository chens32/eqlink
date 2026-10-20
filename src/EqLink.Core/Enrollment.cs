using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EqLink.Core;

[JsonConverter(typeof(JsonStringEnumConverter<AddressMode>))]
public enum AddressMode { Auto, Static }
public sealed record AddressPreference(AddressMode Mode = AddressMode.Auto, string? RequestedIp = null)
{
    public void Validate(string cidr)
    {
        if (!Enum.IsDefined(Mode)) throw new ArgumentException("ADDRESS_MODE_INVALID");
        if (!Enum.IsDefined(Mode)) throw new ArgumentException("ADDRESS_MODE_INVALID");
        if (Mode == AddressMode.Auto) { if (!string.IsNullOrEmpty(RequestedIp)) throw new ArgumentException("ADDRESS_MODE_INVALID"); return; }
        AddressAllocation.Validate(cidr, RequestedIp ?? "");
    }
}

public sealed record EnrollmentGrant(string Id, string Name, string TokenHash, string HysteriaPassword, Guid VlessId, bool Active = true);

public sealed record NetworkConnection
{
    public required string NetworkId { get; init; }
    public required string PublicHost { get; init; }
    public required string OverlayCidr { get; init; }
    public int UdpPort { get; init; } = 443;
    public int TcpPort { get; init; } = 443;
    public int OpenVpnPort { get; init; } = 1194;
    public int HealthPort { get; init; } = 1195;
    public bool EnableUdp { get; init; } = true;
    public bool EnableTcp { get; init; } = true;
    public string HysteriaServerName { get; init; } = "";
    public string HysteriaCertificateSha256 { get; init; } = "";
    public string HysteriaCaPem { get; init; } = "";
    public string RealityServerName { get; init; } = "";
    public string RealityPublicKey { get; init; } = "";
    public string RealityShortId { get; init; } = "";
    public string XhttpPath { get; init; } = "/eqlink";
    public required string CaPem { get; init; }
    public string ControlServerName { get; init; } = "server";
    public int Revision { get; init; } = 1;
    public string ServerIp => new ServerSettings { PublicHost = PublicHost, OverlayCidr = OverlayCidr }.Overlay().ServerIp;
    public void Validate()
    {
        if (!Guid.TryParse(NetworkId, out _) || Uri.CheckHostName(PublicHost) is not (UriHostNameType.Dns or UriHostNameType.IPv4) ||
            string.IsNullOrWhiteSpace(ControlServerName) || ControlServerName != "server" || !EnableUdp && !EnableTcp)
            throw new ArgumentException("NETWORK_CONFIG_INVALID");
        _ = ServerIp;
        foreach (var port in new[] { UdpPort, TcpPort, OpenVpnPort, HealthPort })
            if (port is < 1 or > 65535) throw new ArgumentException("NETWORK_PORT_INVALID");
        if (OpenVpnPort == HealthPort) throw new ArgumentException("NETWORK_PORT_CONFLICT");
        using var ca = X509Certificate2.CreateFromPem(CaPem);
        if (!ca.Extensions.OfType<X509BasicConstraintsExtension>().Any(e => e.CertificateAuthority)) throw new ArgumentException("NETWORK_TRUST_INVALID");
        if (EnableUdp && (string.IsNullOrWhiteSpace(HysteriaServerName) || !System.Text.RegularExpressions.Regex.IsMatch(HysteriaCertificateSha256, "^(?:[0-9a-fA-F]{2}:?){32}$"))) throw new ArgumentException("NETWORK_HY2_INVALID");
        HysteriaTrust.Validate(HysteriaCaPem);
        if (EnableTcp && (string.IsNullOrWhiteSpace(RealityPublicKey) || Uri.CheckHostName(RealityServerName) != UriHostNameType.Dns ||
            !System.Text.RegularExpressions.Regex.IsMatch(RealityShortId,"^(?:[0-9a-fA-F]{2}){1,8}$") ||
            !System.Text.RegularExpressions.Regex.IsMatch(XhttpPath,"^/[a-zA-Z0-9/_-]{1,80}$"))) throw new ArgumentException("NETWORK_REALITY_INVALID");
    }
    // Older v2 servers omit this additive field. Keep the locally imported trust
    // anchor through enrollment, address requests and later configuration refreshes.
    public NetworkConnection RetainHysteriaCa(string previousPem) => string.IsNullOrEmpty(HysteriaCaPem) && !string.IsNullOrEmpty(previousPem)
        ? this with { HysteriaCaPem = previousPem } : this;

    public DeviceProfile Bootstrap(string auth, Guid vless, string name) => new()
    {
        DeviceId = Guid.Empty.ToString("N"), DeviceName = name, VirtualIp = "", OverlayCidr = OverlayCidr, ServerIp = ServerIp,
        PublicHost = PublicHost, UdpPort = UdpPort, TcpPort = TcpPort, OpenVpnPort = OpenVpnPort, HealthPort = HealthPort,
        EnableUdp = EnableUdp, EnableTcp = EnableTcp, HysteriaServerName = HysteriaServerName,
        HysteriaCertificateSha256 = HysteriaCertificateSha256, HysteriaCaPem = HysteriaCaPem, HysteriaAuth = auth, VlessId = vless,
        RealityServerName = RealityServerName, RealityPublicKey = RealityPublicKey, RealityShortId = RealityShortId, XhttpPath = XhttpPath,
        CaPem = CaPem, CertificatePem = "", PrivateKeyPem = "", TlsCryptV2Key = "", AuthenticatedHealth = true
    };
}

public sealed record EnrollmentBundle
{
    public int SchemaVersion { get; init; } = 2;
    public required NetworkConnection Network { get; init; }
    public required string GrantId { get; init; }
    public required string EnrollmentToken { get; init; }
    public required string HysteriaAuth { get; init; }
    public required Guid VlessId { get; init; }
    public void Validate()
    {
        if (Network is null || HysteriaAuth is null || SchemaVersion != 2 || !Guid.TryParseExact(GrantId, "N", out _) || !IsSecret(EnrollmentToken) ||
            Network.EnableUdp && (!HysteriaAuth.StartsWith(GrantId + ":",StringComparison.Ordinal) || HysteriaAuth.Length != 97 || !IsSecret(HysteriaAuth[33..])) || Network.EnableTcp && VlessId == Guid.Empty)
            throw new ArgumentException("BUNDLE_INVALID");
        Network.Validate();
    }
    public static byte[] Serialize(EnrollmentBundle bundle) { bundle.Validate(); return JsonSerializer.SerializeToUtf8Bytes(bundle, Json); }
    public static EnrollmentBundle Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length is < 2 or > 128_000) throw new InvalidDataException("BUNDLE_SIZE_INVALID");
        using var document = JsonDocument.Parse(data.ToArray());
        if (!document.RootElement.TryGetProperty("schemaVersion", out var schema) || schema.GetInt32() != 2)
            throw new InvalidDataException("BUNDLE_V2_REQUIRED: 请重新导入通用入网包；旧加密包不再支持。");
        var bundle = JsonSerializer.Deserialize<EnrollmentBundle>(data, Json) ?? throw new InvalidDataException("BUNDLE_INVALID");
        bundle.Validate(); return bundle;
    }
    public static bool IsSecret(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}

public sealed record LocalDeviceIdentity(string PrivateKeyPem, string CsrPem, string PublicKeyFingerprint)
{
    public static LocalDeviceIdentity Create()
    {
        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest("CN=EqLink-device", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return new(rsa.ExportPkcs8PrivateKeyPem(), request.CreateSigningRequestPem(), Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo())));
    }
}
public sealed record LocalInstallation(EnrollmentBundle Bundle, LocalDeviceIdentity Identity, DeviceProfile? Runtime = null);
public sealed record EnrollmentRequest(string CsrPem, string DeviceName);
public sealed record EnrollmentResponse(string DeviceId, string DeviceName, string CertificatePem, string HysteriaAuth, Guid VlessId, string TlsCryptV2Key, NetworkConnection Network);
public sealed record AddressAssignment(string VirtualIp, string OverlayCidr, string ServerIp, AddressMode Mode);

public static class AddressAllocation
{
    public static void Validate(string cidr, string ip)
    {
        var network = new ServerSettings { PublicHost = "validation.invalid", OverlayCidr = cidr }.Overlay();
        if (!IPAddress.TryParse(ip, out var address) || address.ToString() != ip || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            !address.GetAddressBytes().AsSpan(0,3).SequenceEqual(IPAddress.Parse(network.Network).GetAddressBytes().AsSpan(0,3))) throw new ArgumentException("ADDRESS_OUTSIDE_NETWORK");
        if (address.GetAddressBytes()[3] is < 10 or > 254) throw new ArgumentException("ADDRESS_RESERVED");
    }
    public static string Choose(ServerState state, DeviceRecord device, AddressPreference preference)
    {
        preference.Validate(state.Settings.OverlayCidr);
        var used = state.Devices.Where(d => d.Active && d.Id != device.Id && d.AddressAssigned).Select(d => d.VirtualIp)
            .Concat(state.Settings.ReservedAddresses).ToHashSet(StringComparer.Ordinal);
        if (preference.Mode == AddressMode.Static)
        {
            if (used.Contains(preference.RequestedIp!)) throw new InvalidOperationException("ADDRESS_IN_USE");
            return preference.RequestedIp!;
        }
        if (device.AddressAssigned && !used.Contains(device.VirtualIp)) return device.VirtualIp;
        var prefix = state.Settings.Overlay().ServerIp[..state.Settings.Overlay().ServerIp.LastIndexOf('.')];
        return Enumerable.Range(10,245).Select(i => $"{prefix}.{i}").FirstOrDefault(ip => !used.Contains(ip))
            ?? throw new InvalidOperationException("ADDRESS_POOL_EXHAUSTED");
    }
}

public static class CredentialSecrets
{
    public static string New() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static bool Matches(string expected, string value) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(value));
}
