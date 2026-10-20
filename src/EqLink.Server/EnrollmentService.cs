using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using EqLink.Core;

namespace EqLink.Server;

internal sealed class EnrollmentService(ServerFiles files)
{
    internal static void RequireV2(ServerState state)
    {
        if (state.SchemaVersion != 2 || !Guid.TryParse(state.NetworkId, out _) || !EnrollmentBundle.IsSecret(state.HysteriaApiSecret))
            throw new InvalidOperationException("SERVER_MIGRATION_REQUIRED");
    }

    internal NetworkConnection Network(ServerState? state = null)
    {
        state ??= files.ReadState(); RequireV2(state); var s = state.Settings;
        return new NetworkConnection
        {
            NetworkId = state.NetworkId, PublicHost = s.PublicHost, OverlayCidr = s.OverlayCidr,
            UdpPort = s.UdpPort, TcpPort = s.TcpPort, OpenVpnPort = s.OpenVpnPort, HealthPort = s.HealthPort,
            EnableUdp = s.EnableUdp, EnableTcp = s.EnableTcp, HysteriaServerName = s.HysteriaServerName,
            HysteriaCertificateSha256 = s.EnableUdp ? ConfigRenderer.CertificateFingerprint(File.ReadAllText(s.HysteriaCertificate)) : "",
            RealityServerName = s.RealityServerName, RealityPublicKey = s.RealityPublicKey,
            RealityShortId = s.RealityShortId, XhttpPath = s.XhttpPath, CaPem = File.ReadAllText(Path.Combine(files.Pki, "ca.crt"))
        };
    }

    // Caller holds the cross-process state lock. The reusable token is returned only in the private export file.
    internal EnrollmentBundle CreateGrant(string name)
    {
        if (name.Length is < 1 or > 64 || name.Any(char.IsControl)) throw new ArgumentException("GRANT_NAME_INVALID");
        var state = files.ReadState(); RequireV2(state);
        var token = CredentialSecrets.New();
        var grant = new EnrollmentGrant(Guid.NewGuid().ToString("N"), name, CredentialSecrets.Hash(token), CredentialSecrets.New(), Guid.NewGuid());
        state.Grants.Add(grant); files.Commit(state);
        ApplyUser(state.Settings, grant.Id, grant.VlessId);
        return new EnrollmentBundle { Network = Network(state), GrantId = grant.Id, EnrollmentToken = token, HysteriaAuth = grant.Id + ":" + grant.HysteriaPassword, VlessId = grant.VlessId };
    }

    internal EnrollmentResponse Enroll(string token, EnrollmentRequest request)
    {
        if (!EnrollmentBundle.IsSecret(token)) throw new ControlApiException("ENROLLMENT_DENIED", 401);
        if (request.CsrPem is null || request.DeviceName is null || request.CsrPem.Length is < 64 or > 16384 || request.DeviceName.Length is < 1 or > 64 || request.DeviceName.Any(char.IsControl)) throw new ArgumentException("ENROLLMENT_REQUEST_INVALID");
        // LoadSigningRequest verifies the CSR signature. Requested extensions and subject are not copied to the issued certificate.
        var csr = CertificateRequest.LoadSigningRequestPem(request.CsrPem, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.Default, RSASignaturePadding.Pkcs1);
        using var rsa = csr.PublicKey.GetRSAPublicKey();
        if (rsa is null || rsa.KeySize is < 3072 or > 4096) throw new ArgumentException("DEVICE_KEY_INVALID");
        var fingerprint = Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()));
        using var mutation = new ServerStateLock(files);
        var state = files.ReadState(); RequireV2(state);
        var hash = CredentialSecrets.Hash(token);
        var grant = state.Grants.SingleOrDefault(g => g.Active && CredentialSecrets.Matches(g.TokenHash, hash)) ?? throw new ControlApiException("ENROLLMENT_DENIED", 401);
        var device = state.Devices.SingleOrDefault(d => d.PublicKeyFingerprint == fingerprint);
        if (device is { Active: false }) throw new ControlApiException("DEVICE_REVOKED", 403);
        if (device is null)
        {
            if (state.Devices.Count(d => d.Active) >= 1000) throw new InvalidOperationException("DEVICE_LIMIT_REACHED");
            var id = Guid.NewGuid().ToString("N");
            var issued = new Pki(files).SignDevice("device-" + id, request.CsrPem);
            device = new DeviceRecord { Id = id, Name = request.DeviceName, VirtualIp = "", AddressAssigned = false,
                HysteriaPassword = CredentialSecrets.New(), VlessId = Guid.NewGuid(), CertificatePath = issued.Cert,
                TlsCryptV2Key = issued.TlsKey, Active = true, PublicKeyFingerprint = fingerprint, EnrollmentGrantId = grant.Id };
            state.Devices.Add(device); files.Commit(state);
        }
        ApplyUser(state.Settings, device.Id, device.VlessId);
        return new(device.Id, device.Name, File.ReadAllText(device.CertificatePath), device.Id + ":" + device.HysteriaPassword, device.VlessId, device.TlsCryptV2Key, Network(state));
    }

    internal DeviceRecord Authenticate(X509Certificate2? certificate)
    {
        if (certificate is null) throw new ControlApiException("DEVICE_AUTH_REQUIRED", 401);
        using var rsa = certificate.GetRSAPublicKey();
        if (rsa is null) throw new ControlApiException("DEVICE_AUTH_REQUIRED", 401);
        var fp = Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()));
        var state = files.ReadState(); RequireV2(state);
        var device = state.Devices.SingleOrDefault(d => d.Active && d.PublicKeyFingerprint == fp);
        if (device is null || !File.Exists(device.CertificatePath)) throw new ControlApiException("DEVICE_REVOKED", 403);
        using var stored = X509Certificate2.CreateFromPem(File.ReadAllText(device.CertificatePath));
        if (!certificate.RawData.AsSpan().SequenceEqual(stored.RawData)) throw new ControlApiException("DEVICE_REVOKED", 403);
        return device;
    }

    internal AddressAssignment Assign(string deviceId, AddressPreference preference)
    {
        using var mutation = new ServerStateLock(files);
        var state = files.ReadState(); RequireV2(state);
        var device = state.Devices.SingleOrDefault(d => d.Id == deviceId && d.Active) ?? throw new ControlApiException("DEVICE_REVOKED", 403);
        var ip = AddressAllocation.Choose(state, device, preference);
        if (!device.AddressAssigned || device.VirtualIp != ip)
        {
            if (device.AddressAssigned)
            {
                var ccd = Path.Combine(files.Ccd, "device-" + device.Id);
                File.Delete(ccd); // ccd-exclusive refuses new sessions during the address transaction.
                if (!OpenVpnManagement.Kill(state.Settings.ManagementPort, Path.Combine(files.Pki, "management.password"), "device-" + device.Id))
                {
                    ServerFiles.AtomicWrite(ccd, $"ifconfig-push {device.VirtualIp} {state.Settings.Overlay().Mask}\n");
                    throw new InvalidOperationException("ADDRESS_SESSION_ACTIVE");
                }
            }
            state.Devices[state.Devices.IndexOf(device)] = device with { VirtualIp = ip, AddressAssigned = true, AddressMode = preference.Mode };
            files.Commit(state);
        }
        return new(ip, state.Settings.OverlayCidr, state.Settings.Overlay().ServerIp, preference.Mode);
    }

    internal bool AuthenticateHysteria(string auth, out string id)
    {
        id = ""; var separator = auth.IndexOf(':');
        if (separator != 32 || auth.Length != 97) return false;
        var suppliedId = auth[..separator]; var password = auth[(separator + 1)..]; var state = files.ReadState();
        var expected = state.Devices.FirstOrDefault(d => d.Active && d.Id == suppliedId)?.HysteriaPassword
            ?? state.Grants.FirstOrDefault(g => g.Active && g.Id == suppliedId)?.HysteriaPassword;
        if (expected is null || !CredentialSecrets.Matches(expected, password)) return false;
        id = suppliedId; return true;
    }

    internal void RevokeGrant(string id)
    {
        var state = files.ReadState(); RequireV2(state);
        var grant = state.Grants.SingleOrDefault(g => g.Id == id) ?? throw new ArgumentException("GRANT_NOT_FOUND");
        state.Grants[state.Grants.IndexOf(grant)] = grant with { Active = false }; files.Commit(state);
        RemoveUser(state.Settings, id); KickHysteria(state, id);
    }

    internal void RevokeDevice(string id)
    {
        var state = files.ReadState(); RequireV2(state);
        var device = state.Devices.SingleOrDefault(d => d.Id == id) ?? throw new ArgumentException("DEVICE_NOT_FOUND");
        if (device.Active) { new Pki(files).Revoke(device.CertificatePath); device.Active = false; files.Commit(state); }
        RemoveUser(state.Settings, id); KickHysteria(state, id);
        if (!OpenVpnManagement.Kill(state.Settings.ManagementPort, Path.Combine(files.Pki, "management.password"), "device-" + id))
            throw new InvalidOperationException("DEVICE_REVOKED_SESSION_KICK_PENDING");
    }

    internal void ApplyUser(ServerSettings settings, string id, Guid vless)
    {
        if (!settings.EnableTcp || !ApiListening(settings)) return; // On process restart, the durable config includes all committed users.
        RemoveUser(settings, id);
        var path = Path.Combine(files.Root, ".xray-user-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            ServerFiles.AtomicWrite(path, ConfigRenderer.Serialize(new { inbounds = new[] { new { tag = "vless-in", listen = "127.0.0.1", port = settings.TcpPort, protocol = "vless", settings = new { decryption = "none", clients = new[] { new { id = vless.ToString(), email = id } } } } } }));
            var result = ProcessRunner.RunResult("xray", "api", "adu", "--server=127.0.0.1:" + settings.XrayApiPort, path);
            // This pinned Xray command can exit zero after an operation failed. Check the operation count too.
            if (result.ExitCode != 0 || !result.StandardOutput.Contains("Added 1 user(s) in total.", StringComparison.Ordinal)) throw new IOException("XRAY_USER_APPLY_FAILED");
        }
        finally { File.Delete(path); }
    }

    internal void ReconcileUsers(ServerState state)
    {
        if (!state.Settings.EnableTcp || !ApiListening(state.Settings)) return;
        var result = ProcessRunner.RunResult("xray", "api", "inbounduser", "--server=127.0.0.1:" + state.Settings.XrayApiPort, "-tag=vless-in");
        if (result.ExitCode != 0) throw new IOException("XRAY_USER_QUERY_FAILED");
        using var document = JsonDocument.Parse(result.StandardOutput);
        var actual = document.RootElement.TryGetProperty("users",out var users)
            ? users.EnumerateArray().Select(u=>u.GetProperty("email").GetString() ?? "").ToHashSet(StringComparer.Ordinal) : [];
        if (actual.Any(id=>!Guid.TryParseExact(id,"N",out _))) throw new IOException("XRAY_USER_STATE_UNEXPECTED");
        var desired = state.Devices.Where(d=>d.Active).ToDictionary(d=>d.Id,d=>d.VlessId,StringComparer.Ordinal);
        foreach(var grant in state.Grants.Where(g=>g.Active)) desired.Add(grant.Id,grant.VlessId);
        foreach(var id in actual.Except(desired.Keys)) RemoveUser(state.Settings,id);
        foreach(var user in desired.Where(pair=>!actual.Contains(pair.Key))) ApplyUser(state.Settings,user.Key,user.Value);
    }

    private static bool ApiListening(ServerSettings s) => System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(p => p.Address.Equals(IPAddress.Loopback) && p.Port == s.XrayApiPort);
    private static void RemoveUser(ServerSettings settings, string id)
    {
        if (!settings.EnableTcp || !ApiListening(settings)) return;
        var result = ProcessRunner.RunResult("xray", "api", "rmu", "--server=127.0.0.1:" + settings.XrayApiPort, "-tag=vless-in", id);
        if (result.ExitCode != 0 || !result.StandardOutput.Contains("Removed 1 user(s) in total.", StringComparison.Ordinal) &&
            !(result.StandardOutput.Contains("Removed 0 user(s) in total.", StringComparison.Ordinal) && result.StandardOutput.Contains("not found", StringComparison.OrdinalIgnoreCase))) throw new IOException("XRAY_USER_REMOVE_FAILED");
    }
    private static void KickHysteria(ServerState state, string id)
    {
        if (!state.Settings.EnableUdp) return;
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
        client.DefaultRequestHeaders.Add("Authorization", state.HysteriaApiSecret);
        try { using var response = client.PostAsJsonAsync($"http://127.0.0.1:{state.Settings.HysteriaStatsPort}/kick", new[] { id }).GetAwaiter().GetResult(); response.EnsureSuccessStatusCode(); }
        catch (HttpRequestException) when (!System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(p => p.Port == state.Settings.HysteriaStatsPort)) { }
    }
}
