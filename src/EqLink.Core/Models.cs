using System.Net;
using System.Text.RegularExpressions;

namespace EqLink.Core;

public enum TransportMode { Auto, UdpOnly, TcpOnly }
public enum ActivePath { None, Udp, Tcp }

public sealed record ServerSettings
{
    public required string PublicHost { get; init; }
    public int UdpPort { get; init; } = 443;
    public int TcpPort { get; init; } = 443;
    public int OpenVpnPort { get; init; } = 1194;
    public int HealthPort { get; init; } = 1195;
    public int ManagementPort { get; init; } = 7505;
    public int AuthPort { get; init; } = 1196;
    public int XrayApiPort { get; init; } = 7506;
    public int HysteriaStatsPort { get; init; } = 9999;
    public string[] ReservedAddresses { get; init; } = [];
    public string OverlayCidr { get; init; } = "10.88.0.0/24";
    public bool EnableUdp { get; init; } = true;
    public bool EnableTcp { get; init; } = true;
    public string HysteriaCertificate { get; init; } = "";
    public string HysteriaPrivateKey { get; init; } = "";
    public string HysteriaServerName { get; init; } = "";
    public string RealityTarget { get; init; } = "";
    public string RealityServerName { get; init; } = "";
    public string RealityPrivateKey { get; init; } = "";
    public string RealityPublicKey { get; init; } = "";
    public string RealityShortId { get; init; } = "";
    public string XhttpPath { get; init; } = "/eqlink";

    public (string Network, string Mask, string ServerIp) Overlay()
    {
        var parts = OverlayCidr.Split('/');
        if (parts.Length != 2 || parts[1] != "24" || !IPAddress.TryParse(parts[0], out var ip) ||
            ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new ArgumentException("OverlayCidr must be an IPv4 /24 network.");
        var bytes = ip.GetAddressBytes();
        if (bytes[3] != 0 || bytes[0] is not (10 or 172 or 192) ||
            (bytes[0] == 172 && bytes[1] is < 16 or > 31) ||
            (bytes[0] == 192 && bytes[1] != 168))
            throw new ArgumentException("OverlayCidr must be a private /24 network address.");
        return (ip.ToString(), "255.255.255.0", $"{bytes[0]}.{bytes[1]}.{bytes[2]}.1");
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(PublicHost) ||
            Uri.CheckHostName(PublicHost) != UriHostNameType.Dns &&
            (!IPAddress.TryParse(PublicHost, out var publicIp) || publicIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork))
            throw new ArgumentException("Public host is required.");
        foreach (var port in new[] { UdpPort, TcpPort, OpenVpnPort, HealthPort, ManagementPort, AuthPort, XrayApiPort, HysteriaStatsPort })
            if (port is < 1 or > 65535) throw new ArgumentException("Port outside 1..65535.");
        if (OpenVpnPort == HealthPort || OpenVpnPort == ManagementPort || HealthPort == ManagementPort)
            throw new ArgumentException("Loopback service ports must differ.");
        var internalPorts = new[] { OpenVpnPort, HealthPort, ManagementPort, AuthPort, XrayApiPort, HysteriaStatsPort };
        if (internalPorts.Distinct().Count() != internalPorts.Length || EnableTcp && internalPorts.Contains(TcpPort))
            throw new ArgumentException("Service ports must not conflict.");
        if (EnableTcp && (TcpPort == OpenVpnPort || TcpPort == HealthPort || TcpPort == ManagementPort))
            throw new ArgumentException("Public TCP port conflicts with a loopback TCP port.");
        if (!EnableUdp && !EnableTcp) throw new ArgumentException("Enable at least one outer path.");
        if (EnableUdp && (string.IsNullOrWhiteSpace(HysteriaCertificate) || string.IsNullOrWhiteSpace(HysteriaPrivateKey) ||
            string.IsNullOrWhiteSpace(HysteriaServerName)))
            throw new ArgumentException("Hysteria TLS settings are incomplete.");
        var targetParts = RealityTarget.Split(':');
        if (EnableTcp && (string.IsNullOrWhiteSpace(RealityPrivateKey) || string.IsNullOrWhiteSpace(RealityPublicKey) ||
            Uri.CheckHostName(RealityServerName) != UriHostNameType.Dns || targetParts.Length != 2 ||
            (Uri.CheckHostName(targetParts[0]) != UriHostNameType.Dns && !IPAddress.TryParse(targetParts[0], out _)) ||
            !int.TryParse(targetParts[1], out var targetPort) || targetPort is < 1 or > 65535 ||
            !Regex.IsMatch(RealityShortId, "^(?:[0-9a-fA-F]{2}){1,8}$") ||
            !Regex.IsMatch(XhttpPath, "^/[a-zA-Z0-9/_-]{1,80}$")))
            throw new ArgumentException("TLS/REALITY/XHTTP settings are incomplete or invalid.");
        Overlay();
        foreach (var reserved in ReservedAddresses) AddressAllocation.Validate(OverlayCidr, reserved);
    }
}

public sealed record DeviceRecord
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string VirtualIp { get; init; }
    public required string HysteriaPassword { get; init; }
    public required Guid VlessId { get; init; }
    public required string CertificatePath { get; init; }
    public required bool Active { get; set; }
    public string PublicKeyFingerprint { get; init; } = "";
    public string EnrollmentGrantId { get; init; } = "";
    public string TlsCryptV2Key { get; init; } = "";
    public bool AddressAssigned { get; init; } = true;
    public AddressMode AddressMode { get; init; } = AddressMode.Auto;
}

public sealed record ServerState
{
    public int SchemaVersion { get; init; } = 1;
    public string NetworkId { get; init; } = "";
    public string HysteriaApiSecret { get; init; } = "";
    public required ServerSettings Settings { get; init; }
    public List<DeviceRecord> Devices { get; init; } = [];
    public List<EnrollmentGrant> Grants { get; init; } = [];
}

public sealed record DeviceProfile
{
    public int SchemaVersion { get; init; } = 1;
    public required string DeviceId { get; init; }
    public required string DeviceName { get; init; }
    public required string VirtualIp { get; init; }
    public required string OverlayCidr { get; init; }
    public required string ServerIp { get; init; }
    public required string PublicHost { get; init; }
    public required int UdpPort { get; init; }
    public required int TcpPort { get; init; }
    public required int OpenVpnPort { get; init; }
    public required int HealthPort { get; init; }
    public required bool EnableUdp { get; init; }
    public required bool EnableTcp { get; init; }
    public string HysteriaServerName { get; init; } = "";
    public string HysteriaCertificateSha256 { get; init; } = "";
    public string HysteriaCaPem { get; init; } = "";
    public string HysteriaAuth { get; init; } = "";
    public Guid VlessId { get; init; }
    public string RealityServerName { get; init; } = "";
    public string RealityPublicKey { get; init; } = "";
    public string RealityShortId { get; init; } = "";
    public string XhttpPath { get; init; } = "";
    public required string CaPem { get; init; }
    public required string CertificatePem { get; init; }
    public required string PrivateKeyPem { get; init; }
    public required string TlsCryptV2Key { get; init; }
    public bool AuthenticatedHealth { get; init; }

    public void Validate()
    {
        if (SchemaVersion != 1 || !Guid.TryParse(DeviceId, out _) ||
            !IPAddress.TryParse(VirtualIp, out var virtualIp) || virtualIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            !IPAddress.TryParse(ServerIp, out var serverIp) || serverIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            !Uri.CheckHostName(PublicHost).Equals(UriHostNameType.Dns) && !IPAddress.TryParse(PublicHost, out _) ||
            string.IsNullOrWhiteSpace(CaPem) || string.IsNullOrWhiteSpace(CertificatePem) ||
            string.IsNullOrWhiteSpace(PrivateKeyPem) || string.IsNullOrWhiteSpace(TlsCryptV2Key) ||
            !EnableTcp && !EnableUdp)
            throw new ArgumentException("Invalid or incomplete device profile.");
        var cidr = OverlayCidr.Split('/');
        if (cidr.Length != 2 || cidr[1] != "24" || !IPAddress.TryParse(cidr[0], out var networkIp) ||
            networkIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new ArgumentException("Invalid overlay network.");
        var network = networkIp.GetAddressBytes();
        var server = serverIp.GetAddressBytes();
        var device = virtualIp.GetAddressBytes();
        if (network[3] != 0 || network[0] is not (10 or 172 or 192) ||
            (network[0] == 172 && network[1] is < 16 or > 31) ||
            (network[0] == 192 && network[1] != 168) ||
            server[3] != 1 || device[3] is < 10 or > 254 ||
            !network.AsSpan(0, 3).SequenceEqual(server.AsSpan(0, 3)) ||
            !network.AsSpan(0, 3).SequenceEqual(device.AsSpan(0, 3)))
            throw new ArgumentException("Device address is outside the overlay pool.");
        if (!IsSinglePem(CaPem, "CERTIFICATE") || !IsSinglePem(CertificatePem, "CERTIFICATE") ||
            !(IsSinglePem(PrivateKeyPem, "PRIVATE KEY") || IsSinglePem(PrivateKeyPem, "RSA PRIVATE KEY")) ||
            TlsCryptV2Key.Contains("</tls-crypt-v2>", StringComparison.OrdinalIgnoreCase) ||
            !TlsCryptV2Key.StartsWith("-----BEGIN OpenVPN tls-crypt-v2 client key-----", StringComparison.Ordinal) ||
            !TlsCryptV2Key.TrimEnd().EndsWith("-----END OpenVPN tls-crypt-v2 client key-----", StringComparison.Ordinal))
            throw new ArgumentException("Invalid device key material.");
        foreach (var port in new[] { UdpPort, TcpPort, OpenVpnPort, HealthPort })
            if (port is < 1 or > 65535) throw new ArgumentException("Invalid device port.");
        if (OpenVpnPort == HealthPort) throw new ArgumentException("Loopback ports must differ.");
        if (EnableUdp && (string.IsNullOrWhiteSpace(HysteriaServerName) || string.IsNullOrWhiteSpace(HysteriaCertificateSha256) || string.IsNullOrWhiteSpace(HysteriaAuth)))
            throw new ArgumentException("Hysteria device settings are incomplete.");
        HysteriaTrust.Validate(HysteriaCaPem);
        if (EnableTcp && (VlessId == Guid.Empty || string.IsNullOrWhiteSpace(RealityServerName) || string.IsNullOrWhiteSpace(RealityPublicKey) ||
            !Regex.IsMatch(RealityShortId, "^(?:[0-9a-fA-F]{2}){1,8}$") ||
            !Regex.IsMatch(XhttpPath, "^/[a-zA-Z0-9/_-]{1,80}$")))
            throw new ArgumentException("Xray device settings are incomplete.");
        using var certificate = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(CertificatePem, PrivateKeyPem);
        if (!certificate.HasPrivateKey)
            throw new ArgumentException("Certificate and private key do not match.");
    }

    private static bool IsSinglePem(string text, string label) => Regex.IsMatch(text.Trim(),
        $"\\A-----BEGIN {Regex.Escape(label)}-----\\r?\\n(?:[A-Za-z0-9+/=]+\\r?\\n)+-----END {Regex.Escape(label)}-----\\z");
}
