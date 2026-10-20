using System.IO;
using System.Net.Http;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using EqLink.Core;

namespace EqLink.ClientService;

internal sealed partial class EqLinkDaemon
{
    private LocalInstallation? _installation;
    private AddressPreference _address = new();
    private string IdentityFile => Path.Combine(_root, "identity.dpapi");

    private async Task<HealthProbeResult> ProbeHealthAsync(string? path)
    {
        if (_installation?.Runtime is not { } profile) return new(false, "DEVICE_NOT_REGISTERED", DateTimeOffset.UtcNow);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            int? port = path switch { "UDP" => UdpProxyPort, "TCP" => TcpProxyPort, "INNER" => null, _ => throw new ArgumentException("PROBE_PATH_INVALID") };
            if (port is null && (!_vpnInitialized || _lastNetwork is not { VirtualReady: true })) return new(false, "VIRTUAL_ADAPTER_UNAVAILABLE", DateTimeOffset.UtcNow);
            using var api = new ControlApiClient(profile, port, port is null ? (int?)_lastNetwork?.VirtualIndex : null); await api.HealthAsync(deadline.Token);
            return new(true, null, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException)
        { return new(false, ex is ControlApiException denied ? denied.Code : "HEALTH_UNAVAILABLE", DateTimeOffset.UtcNow); }
    }

    private void LoadInstallation()
    {
        if (!File.Exists(IdentityFile))
        {
            if (File.Exists(Path.Combine(_root, "profile.dpapi"))) _message = "旧设备配置已保留，请导入 v2 通用入网包以重新登记。";
            return;
        }
        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(IdentityFile), "EqLink-identity-v2"u8.ToArray(), DataProtectionScope.LocalMachine);
            try
            {
                var saved = JsonSerializer.Deserialize<LocalInstallation>(plain, Json) ?? throw new InvalidDataException("IDENTITY_INVALID");
                saved.Bundle.Validate(); ValidateLocalIdentity(saved.Identity);
                _installation = saved;
                _profile = saved.Runtime ?? saved.Bundle.Network.Bootstrap(saved.Bundle.HysteriaAuth, saved.Bundle.VlessId, Environment.MachineName);
                _message = saved.Runtime is null ? "已导入，待登记" : "设备已登记，尚未连接";
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
            var path = Path.Combine(_root, "address.json");
            if (File.Exists(path)) _address = JsonSerializer.Deserialize<AddressPreference>(File.ReadAllText(path), Json) ?? new();
            _address.Validate(_profile!.OverlayCidr);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or ArgumentException or IOException)
        { _installation = null; _profile = null; _message = "本机身份配置不可用，请以管理员身份重新导入通用入网包。"; }
    }

    private static void ValidateLocalIdentity(LocalDeviceIdentity identity)
    {
        using var key = RSA.Create(); key.ImportFromPem(identity.PrivateKeyPem);
        var csr = CertificateRequest.LoadSigningRequestPem(identity.CsrPem, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.Default, RSASignaturePadding.Pkcs1);
        using var publicKey = csr.PublicKey.GetRSAPublicKey();
        if (publicKey is null || !key.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(publicKey.ExportSubjectPublicKeyInfo()) ||
            Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())) != identity.PublicKeyFingerprint) throw new CryptographicException("IDENTITY_INVALID");
    }

    private void PersistInstallation(LocalInstallation installation)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(installation, Json);
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(plain, "EqLink-identity-v2"u8.ToArray(), DataProtectionScope.LocalMachine); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        var temporary = IdentityFile + ".tmp";
        File.WriteAllBytes(temporary, encrypted); File.Move(temporary, IdentityFile, true);
        _installation = installation;
    }

    private void ImportUniversal(byte[] bytes)
    {
        if (_wantConnection || _connecting) throw new InvalidOperationException("请先断开连接，再导入通用包。");
        var bundle = EnrollmentBundle.Parse(bytes);
        var previous = _installation;
        var sameNetwork = previous?.Bundle.Network.NetworkId == bundle.Network.NetworkId;
        if (sameNetwork && previous!.Bundle.Network.CaPem != bundle.Network.CaPem) throw new InvalidOperationException("TRUST_ANCHOR_CHANGED: 同一网络的信任锚发生变化，请联系管理员。");
        if (sameNetwork && !string.IsNullOrEmpty(previous!.Bundle.Network.HysteriaCaPem))
        {
            if (string.IsNullOrEmpty(bundle.Network.HysteriaCaPem)) bundle = bundle with { Network = bundle.Network with { HysteriaCaPem = previous.Bundle.Network.HysteriaCaPem } };
            else if (!HysteriaTrust.Equivalent(previous.Bundle.Network.HysteriaCaPem, bundle.Network.HysteriaCaPem)) throw new InvalidOperationException("TRUST_ANCHOR_CHANGED: 同一网络的 HY2 专用 CA 发生变化，请联系管理员。");
        }
        DeviceProfile? refreshed = null;
        if (sameNetwork && previous!.Runtime is { } current)
        {
            var address = current.VirtualIp;
            try { AddressAllocation.Validate(bundle.Network.OverlayCidr, address); } catch (ArgumentException) { address = ""; }
            refreshed = bundle.Network.Bootstrap(current.HysteriaAuth, current.VlessId, current.DeviceName) with { DeviceId = current.DeviceId,
                CertificatePem = current.CertificatePem,
                PrivateKeyPem = current.PrivateKeyPem,
                TlsCryptV2Key = current.TlsCryptV2Key, VirtualIp = address };
        }
        var next = sameNetwork ? previous! with { Bundle = bundle, Runtime = refreshed } : new LocalInstallation(bundle, LocalDeviceIdentity.Create());
        var profile = next.Runtime ?? bundle.Network.Bootstrap(bundle.HysteriaAuth, bundle.VlessId, Environment.MachineName);
        CheckOverlayCollision(profile);
        lock (_policyGate)
        {
            var old = _profile;
            if (_protectionEnabled) EnsureProtection(profile);
            try { PersistInstallation(next); }
            catch { if (old is not null && _protectionEnabled) EnsureProtection(old); throw; }
            _profile = profile;
        }
        if (!sameNetwork) { _address = new(); File.Delete(Path.Combine(_root, "address.json")); }
        if (_mode == TransportMode.UdpOnly && !profile.EnableUdp || _mode == TransportMode.TcpOnly && !profile.EnableTcp) _mode = TransportMode.Auto;
        SetState("Disconnected", null, next.Runtime is null ? "已导入，待登记；点击连接自动登记设备。" : "通用包已更新，保留本机设备身份。");
    }

    private async Task<DeviceProfile> PrepareDeviceAsync(DeviceProfile initial, int generation)
    {
        var installation = _installation ?? throw new InvalidOperationException("BUNDLE_V2_REQUIRED");
        if (installation.Runtime is { } enrolled)
        {
            using var certificate = X509Certificate2.CreateFromPem(enrolled.CertificatePem);
            if (DateTime.UtcNow < certificate.NotBefore.ToUniversalTime() || DateTime.UtcNow >= certificate.NotAfter.ToUniversalTime()) throw new ControlApiException("DEVICE_CERT_EXPIRED",403);
        }
        var transport = _physicalEgress?.RuntimeProfile ?? initial;
        Process? hy = null, xr = null;
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        // A generation change is checked during every bounded attempt; no temporary OpenVPN is started.
        try
        {
            if (initial.EnableUdp && ModeAllows(ActivePath.Udp))
            {
                WriteHysteriaConfiguration(transport);
                hy = StartProcess(Path.Combine(AppContext.BaseDirectory, "bin", "hysteria.exe"), "client", "-c", Path.Combine(_root, "hysteria.json"));
                _ = DrainAsync(hy.StandardOutput, ActivePath.Udp, generation); _ = DrainAsync(hy.StandardError, ActivePath.Udp, generation);
            }
            if (initial.EnableTcp && ModeAllows(ActivePath.Tcp))
            {
                File.WriteAllText(Path.Combine(_root, "xray.json"), ConfigRenderer.ClientXray(transport, TcpProxyPort));
                xr = StartProcess(Path.Combine(AppContext.BaseDirectory, "bin", "xray.exe"), "run", "-c", Path.Combine(_root, "xray.json"));
                _ = DrainAsync(xr.StandardOutput, ActivePath.Tcp, generation); _ = DrainAsync(xr.StandardError, ActivePath.Tcp, generation);
            }
            SetState(installation.Runtime is null ? "Enrolling" : "Configuring", null, installation.Runtime is null ? "正在自动登记本机身份" : "正在取得服务器确认的地址");
            var paths = new List<int>();
            if (initial.EnableUdp && _mode != TransportMode.TcpOnly) paths.Add(UdpProxyPort);
            if (initial.EnableTcp && _mode != TransportMode.UdpOnly) paths.Add(TcpProxyPort);
            Exception? failure = null;
            for (var retry = 0; retry < 3; retry++)
            {
                foreach (var proxy in paths)
                {
                    if (generation != Volatile.Read(ref _generation) || !_wantConnection) throw new OperationCanceledException();
                    if (_udpAuthFailure || _tcpAuthFailure) throw new ControlApiException(SelectedOuterFailure()?.Code ?? "OUTER_AUTH_REJECTED", 403);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancelled.Token); deadline.CancelAfter(TimeSpan.FromSeconds(4));
                    try
                    {
                        var p = installation.Runtime;
                        if (p is null)
                        {
                            using var enroll = new ControlApiClient(initial, proxy, enrollmentToken: installation.Bundle.EnrollmentToken);
                            var result = await enroll.EnrollAsync(new(installation.Identity.CsrPem, Environment.MachineName), deadline.Token);
                            if (result.Network.NetworkId != installation.Bundle.Network.NetworkId || result.Network.CaPem != installation.Bundle.Network.CaPem) throw new InvalidDataException("NETWORK_IDENTITY_CHANGED");
                            var issuedNetwork = result.Network.RetainHysteriaCa(installation.Bundle.Network.HysteriaCaPem);
                            issuedNetwork.Validate();
                            p = issuedNetwork.Bootstrap(result.HysteriaAuth, result.VlessId, result.DeviceName) with { DeviceId = result.DeviceId,
                                CertificatePem = result.CertificatePem,
                                PrivateKeyPem = installation.Identity.PrivateKeyPem,
                                TlsCryptV2Key = result.TlsCryptV2Key }; // gitleaks:allow -- Copies a runtime property; no credential literal.
                            using var issued = X509Certificate2.CreateFromPem(p.CertificatePem, p.PrivateKeyPem);
                            installation = installation with { Runtime = p };
                            PersistInstallation(installation); // Retry after a lost response reuses the locally generated key.
                            _profile = p;
                        }
                        using var api = new ControlApiClient(p, proxy);
                        var network = await api.NetworkAsync(deadline.Token);
                        if (network.NetworkId != installation.Bundle.Network.NetworkId || network.CaPem != installation.Bundle.Network.CaPem) throw new InvalidDataException("NETWORK_IDENTITY_CHANGED");
                        network = network.RetainHysteriaCa(p.HysteriaCaPem);
                        network.Validate();
                        var assignment = await api.AddressAsync(_address, deadline.Token);
                        if (assignment.OverlayCidr != network.OverlayCidr || assignment.ServerIp != network.ServerIp) throw new InvalidDataException("ADDRESS_RESPONSE_INVALID");
                        var confirmed = network.Bootstrap(p.HysteriaAuth, p.VlessId, p.DeviceName) with { DeviceId = p.DeviceId, CertificatePem = p.CertificatePem,
                            PrivateKeyPem = p.PrivateKeyPem,
                            TlsCryptV2Key = p.TlsCryptV2Key, VirtualIp = assignment.VirtualIp };
                        confirmed.Validate();
                        await UpdateHostsNetworkAsync(confirmed);
                        PersistInstallation(installation with { Runtime = confirmed });
                        _profile = confirmed;
                        if (_protectionEnabled) EnsureProtection(confirmed);
                        return confirmed;
                    }
                    catch (ControlApiException) { throw; } // Rejections must not alternate forever between transports.
                    catch (HttpRequestException ex) when (ex.InnerException is System.Security.Authentication.AuthenticationException) { throw new ControlApiException("CONTROL_TRUST_REJECTED", 403); }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException) { failure = ex; }
                }
                await Task.Delay(TimeSpan.FromMilliseconds(300 + Random.Shared.Next(300)), cancelled.Token);
            }
            throw new IOException("CONTROL_UNREACHABLE", failure);
        }
        finally
        {
            foreach (var process in new[] { hy, xr })
            {
                if (process is null) continue;
                try { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); } finally { process.Dispose(); }
            }
            _physicalEgress?.ResetUdpPeer();
        }
    }

    private void WriteHysteriaConfiguration(DeviceProfile profile)
    {
        var path = Path.Combine(_root, "hysteria-ca.pem");
        string? caFile = null;
        if (!string.IsNullOrEmpty(profile.HysteriaCaPem))
        {
            HysteriaTrust.Validate(profile.HysteriaCaPem);
            File.WriteAllText(path + ".tmp", profile.HysteriaCaPem);
            File.Move(path + ".tmp", path, true);
            caFile = path;
        }
        else File.Delete(path);
        File.WriteAllText(Path.Combine(_root, "hysteria.json"), ConfigRenderer.ClientHysteria(profile, UdpProxyPort, caFile));
    }
}
