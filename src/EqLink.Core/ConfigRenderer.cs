using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace EqLink.Core;

public static class ConfigRenderer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static string Serialize(object value) => JsonSerializer.Serialize(value, Json) + "\n";

    public static string ServerOpenVpn(ServerSettings s, string root)
    {
        s.Validate();
        var overlay = s.Overlay();
        return $"""
            local 127.0.0.1
            port {s.OpenVpnPort}
            proto tcp-server
            dev tun
            topology subnet
            server {overlay.Network} {overlay.Mask}
            client-to-client
            client-config-dir {root}/ccd
            ccd-exclusive
            ca {root}/pki/ca.crt
            cert {root}/pki/server.crt
            key {root}/pki/server.key
            dh none
            tls-crypt-v2 {root}/pki/tls-crypt-v2-server.key
            crl-verify {root}/pki/crl.pem
            management 127.0.0.1 {s.ManagementPort} {root}/pki/management.password
            keepalive 10 30
            persist-key
            persist-tun
            disable-dco
            data-ciphers AES-256-GCM:CHACHA20-POLY1305
            verify-client-cert require
            tls-version-min 1.2
            verb 3
            """ + "\n";
    }

    public static string ClientOpenVpn(DeviceProfile p, int selectorPort, string selectorToken)
    {
        p.Validate();
        if (selectorToken.Length != 64 || selectorToken.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("Invalid selector token.");
        return $"""
            client
            dev tun
            proto tcp-client
            {(OperatingSystem.IsWindows() ? "windows-driver tap-windows6\nip-win32 netsh" : "")}
            remote 127.0.0.1 {p.OpenVpnPort}
            http-proxy 127.0.0.1 {selectorPort}
            http-proxy-option CUSTOM-HEADER X-EqLink-Token {selectorToken}
            nobind
            resolv-retry infinite
            connect-retry 1 5
            connect-timeout 10
            persist-key
            persist-tun
            disable-dco
            remote-cert-tls server
            verify-x509-name server name
            pull-filter ignore "redirect-gateway"
            pull-filter ignore "dhcp-option"
            route-nopull
            route {p.OverlayCidr.Split('/')[0]} 255.255.255.0 {p.ServerIp}
            tls-version-min 1.2
            auth-nocache
            verb 3
            <ca>
            {p.CaPem.Trim()}
            </ca>
            <cert>
            {p.CertificatePem.Trim()}
            </cert>
            <key>
            {p.PrivateKeyPem.Trim()}
            </key>
            <tls-crypt-v2>
            {p.TlsCryptV2Key.Trim()}
            </tls-crypt-v2>
            """ + "\n";
    }

    public static string ServerHysteria(ServerSettings s, IEnumerable<DeviceRecord> devices)
    {
        s.Validate();
        var users = devices.Where(d => d.Active).ToDictionary(d => d.Id, d => d.HysteriaPassword);
        // Hysteria rejects an empty userpass map. A fresh server or a fully revoked
        // pool must keep listening while denying every authentication attempt.
        object authentication = users.Count == 0
            ? new { type = "command", command = "/bin/false" }
            : new { type = "userpass", userpass = users };
        return Serialize(new
        {
            listen = $"0.0.0.0:{s.UdpPort}",
            tls = new { cert = s.HysteriaCertificate, key = s.HysteriaPrivateKey, sniGuard = "strict" },
            auth = authentication,
            disableUDP = true,
            speedTest = false,
            acl = new { inline = new[] {
                $"direct(127.0.0.1, tcp/{s.OpenVpnPort})",
                $"direct(127.0.0.1, tcp/{s.HealthPort})",
                "reject(all)" } }
        });
    }

    public static string ClientHysteria(DeviceProfile p, int localProxyPort, string? caFile = null)
    {
        var tls = new Dictionary<string, object> { ["sni"] = p.HysteriaServerName, ["insecure"] = false, ["pinSHA256"] = p.HysteriaCertificateSha256 };
        if (!string.IsNullOrEmpty(p.HysteriaCaPem))
        {
            HysteriaTrust.Validate(p.HysteriaCaPem);
            if (string.IsNullOrWhiteSpace(caFile)) throw new ArgumentException("HYSTERIA_CA_FILE_REQUIRED");
            tls["ca"] = caFile;
        }
        return Serialize(new
        {
            server = $"{p.PublicHost}:{p.UdpPort}",
            auth = p.HysteriaAuth,
            tls,
            http = new { listen = $"127.0.0.1:{localProxyPort}" }
        });
    }

    public static string ServerXray(ServerSettings s, IEnumerable<DeviceRecord> devices)
    {
        s.Validate();
        var clients = devices.Where(d => d.Active).Select(d => new { id = d.VlessId.ToString(), email = d.Id }).ToArray();
        return Serialize(new
        {
            log = new { loglevel = "warning" },
            inbounds = new[] { new {
                tag = "vless-in", listen = "0.0.0.0", port = s.TcpPort, protocol = "vless",
                settings = new { clients, decryption = "none" },
                streamSettings = new {
                    network = "xhttp", security = "reality",
                    realitySettings = new { target = s.RealityTarget, serverNames = new[] { s.RealityServerName }, privateKey = s.RealityPrivateKey, shortIds = new[] { s.RealityShortId } },
                    xhttpSettings = new { path = s.XhttpPath, mode = "auto" }
                }
            } },
            routing = new {
                domainStrategy = "AsIs",
                rules = new object[] {
                    new { type = "field", inboundTag = new[] { "vless-in" }, network = "tcp", ip = new[] { "127.0.0.1/32" },
                        port = $"{s.OpenVpnPort},{s.HealthPort}", outboundTag = "backend-only" },
                    new { type = "field", inboundTag = new[] { "vless-in" }, network = "tcp,udp", outboundTag = "blocked" }
                }
            },
            outbounds = new object[] {
                new { tag = "backend-only", protocol = "freedom" },
                new { tag = "blocked", protocol = "blackhole" }
            }
        });
    }

    public static string ClientXray(DeviceProfile p, int localProxyPort)
    {
        return Serialize(new
        {
            log = new { loglevel = "warning" },
            inbounds = new[] { new { listen = "127.0.0.1", port = localProxyPort, protocol = "http" } },
            outbounds = new[] { new {
                protocol = "vless",
                settings = new { address = p.PublicHost, port = p.TcpPort, id = p.VlessId.ToString(), encryption = "none" },
                streamSettings = new {
                    network = "xhttp", security = "reality",
                    realitySettings = new { serverName = p.RealityServerName, fingerprint = "chrome", password = p.RealityPublicKey, shortId = p.RealityShortId },
                    xhttpSettings = new { path = p.XhttpPath, mode = "auto" }
                }
            } }
        });
    }

    public static string CertificateFingerprint(string certificatePem)
    {
        using var cert = X509Certificate2.CreateFromPem(certificatePem);
        return string.Join(':', SHA256.HashData(cert.RawData).Select(b => b.ToString("X2")));
    }

    public static string ServerHysteria(ServerState state)
    {
        var s = state.Settings; s.Validate();
        return Serialize(new
        {
            listen = $"0.0.0.0:{s.UdpPort}",
            tls = new { cert = s.HysteriaCertificate, key = s.HysteriaPrivateKey, sniGuard = "strict" },
            auth = new { type = "http", http = new { url = $"http://127.0.0.1:{s.AuthPort}/auth" } },
            trafficStats = new { listen = $"127.0.0.1:{s.HysteriaStatsPort}", secret = state.HysteriaApiSecret },
            disableUDP = true, speedTest = false,
            acl = new { inline = new[] { $"direct(127.0.0.1, tcp/{s.OpenVpnPort})", $"direct(127.0.0.1, tcp/{s.HealthPort})", "reject(all)" } }
        });
    }

    public static string ServerXray(ServerState state)
    {
        var s = state.Settings; s.Validate();
        var clients = state.Devices.Where(d => d.Active).Select(d => new { id = d.VlessId.ToString(), email = d.Id })
            .Concat(state.Grants.Where(g => g.Active).Select(g => new { id = g.VlessId.ToString(), email = g.Id })).ToArray();
        return Serialize(new
        {
            log = new { loglevel = "warning" },
            api = new { tag = "api", listen = $"127.0.0.1:{s.XrayApiPort}", services = new[] { "HandlerService" } },
            inbounds = new[] { new {
                tag = "vless-in", listen = "0.0.0.0", port = s.TcpPort, protocol = "vless",
                settings = new { clients, decryption = "none" },
                streamSettings = new { network = "xhttp", security = "reality",
                    realitySettings = new { target = s.RealityTarget, serverNames = new[] { s.RealityServerName }, privateKey = s.RealityPrivateKey, shortIds = new[] { s.RealityShortId } },
                    xhttpSettings = new { path = s.XhttpPath, mode = "auto" } }
            } },
            routing = new { domainStrategy = "AsIs", rules = new object[] {
                new { type = "field", inboundTag = new[] { "vless-in" }, network = "tcp", ip = new[] { "127.0.0.1/32" }, port = $"{s.OpenVpnPort},{s.HealthPort}", outboundTag = "backend-only" },
                new { type = "field", inboundTag = new[] { "vless-in" }, network = "tcp,udp", outboundTag = "blocked" }
            } },
            outbounds = new object[] { new { tag = "backend-only", protocol = "freedom" }, new { tag = "blocked", protocol = "blackhole" } }
        });
    }
}
