using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using EqLink.Core;

namespace EqLink.Server;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0) throw new ArgumentException(Usage);
            var command = args[0];
            var options = Parse(args.Skip(1).ToArray());
            if (command == "health")
            {
                await Health.RunAsync(int.Parse(Required(options, "port")));
                return 0;
            }
            var files = new ServerFiles(options.GetValueOrDefault("root", "/etc/eqlink"));
            if (command == "inspect")
            {
                Console.WriteLine(JsonSerializer.Serialize(ServerInspection.Read(files), new JsonSerializerOptions(Json) { WriteIndented=true }));
                return 0;
            }
            if (command is "serve" or "probe")
            {
                if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Container runtime requires Linux.");
                if (command == "probe") return ContainerSupervisor.Probe(options.GetValueOrDefault("kind", "ready"));
                await new ContainerSupervisor(files).RunAsync(Required(options, "settings"));
                return 0;
            }
            using var stateLock = new ServerStateLock(files);
            switch (command)
            {
                case "preflight":
                    Preflight(ReadSettings(Required(options, "settings")), files);
                    Console.WriteLine("Preflight passed. Firewall/NAT and remote reachability still require external verification.");
                    return 0;
                case "init":
                    Init(files, ReadSettings(Required(options, "settings")));
                    Console.WriteLine($"Initialized {files.Root}. Install systemd units, open only outer ports, then start services.");
                    return 0;
                case "add":
                    Add(files, Required(options, "name"), Required(options, "bundle"), Required(options, "passphrase-file"), options.GetValueOrDefault("id"));
                    return 0;
                case "list":
                    foreach (var d in files.ReadState().Devices)
                        Console.WriteLine($"{d.Id}  {d.VirtualIp}  {(d.Active ? "active" : "revoked")}  {d.Name}");
                    return 0;
                case "revoke":
                    Revoke(files, Required(options, "id"));
                    return 0;
                case "apply":
                    var state = files.ReadState();
                    files.Render(state);
                    ServerFiles.RestartOuter(state.Settings);
                    return 0;
                case "refresh-crl":
                    _ = files.ReadState();
                    new Pki(files).RefreshCrl();
                    return 0;
                case "backup":
                    var destination = Path.GetFullPath(Required(options, "output"));
                    if (!OperatingSystem.IsLinux() || destination.StartsWith(files.Root.TrimEnd('/') + "/", StringComparison.Ordinal)) throw new ArgumentException("Backup requires Linux and an output path outside the state directory.");
                    using (var backup = new FileStream(destination, new FileStreamOptions { Mode=FileMode.CreateNew, Access=FileAccess.Write, UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite })) backup.Flush(true);
                    try { ProcessRunner.Run("tar", "--exclude=./.state.lock", "-C", files.Root, "-czf", destination, "."); }
                    catch { File.Delete(destination); throw; }
                    Console.WriteLine("Private state backup created.");
                    return 0;
                default: throw new ArgumentException(Usage);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(args.FirstOrDefault() == "inspect" ? "Server inspection unavailable (" + ex.GetType().Name + ")." : ex.Message);
            return 1;
        }
    }

    private const string Usage = "EqLink.Server preflight|init|serve --settings file [--root dir]; add --name text --bundle file --passphrase-file file [--root dir]; list|revoke --id uuid|apply|refresh-crl [--root dir]; inspect --json [--root dir]; backup --output private-file.tar.gz [--root dir]; health --port N; probe --kind live|ready|status";

    private static Dictionary<string, string> Parse(string[] args)
    {
        args = args.SelectMany(a => a == "--json" ? new[] { "--json", "true" } : new[] { a }).ToArray();
        if (args.Length % 2 != 0) throw new ArgumentException(Usage);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || !result.TryAdd(args[i][2..], args[i + 1]))
                throw new ArgumentException(Usage);
        }
        return result;
    }

    private static string Required(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException($"Missing --{name}.");

    internal static ServerSettings ReadSettings(string path)
    {
        var settings = JsonSerializer.Deserialize<ServerSettings>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("Empty settings.");
        settings.Validate();
        return settings;
    }

    private static void Preflight(ServerSettings settings, ServerFiles files)
    {
        settings.Validate();
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Server installation requires Linux.");
        var osRelease = File.ReadAllText("/etc/os-release");
        if (!osRelease.Contains("ID=ubuntu") && !osRelease.Contains("ID=debian"))
            throw new PlatformNotSupportedException("Only Debian and Ubuntu are supported in v1.");
        if (System.Runtime.InteropServices.RuntimeInformation.OSArchitecture is not (System.Runtime.InteropServices.Architecture.X64 or System.Runtime.InteropServices.Architecture.Arm64))
            throw new PlatformNotSupportedException("Only amd64 and arm64 are supported.");
        var commands = new[] { "openssl", "openvpn", "xray", "hysteria", "ip" }.AsEnumerable();
        if (!ServerRuntime.IsContainer) commands = commands.Append("systemctl");
        foreach (var name in commands)
            if (!ProcessRunner.Exists(name)) throw new FileNotFoundException($"Required command is missing: {name}");
        if (settings.EnableUdp)
        {
            using var cert = X509Certificate2.CreateFromPemFile(settings.HysteriaCertificate, settings.HysteriaPrivateKey);
            if (!cert.HasPrivateKey || DateTime.UtcNow < cert.NotBefore.ToUniversalTime() || DateTime.UtcNow > cert.NotAfter.ToUniversalTime())
                throw new InvalidDataException("Hysteria TLS certificate/key invalid or expired.");
        }
        CheckRoutes(settings.OverlayCidr);
        if (settings.EnableTcp) ServerFiles.CheckPort(settings.TcpPort, ProtocolType.Tcp);
        if (settings.EnableUdp) ServerFiles.CheckPort(settings.UdpPort, ProtocolType.Udp);
        foreach (var port in new[] { settings.OpenVpnPort, settings.HealthPort, settings.ManagementPort })
            ServerFiles.CheckPort(port, ProtocolType.Tcp);
        if (File.Exists(files.StateFile)) throw new InvalidOperationException("Server is already initialized; use apply, not init.");
    }

    private static void CheckRoutes(string overlay)
    {
        var network = IPAddress.Parse(overlay.Split('/')[0]).GetAddressBytes();
        var routes = ProcessRunner.Run("ip", "-4", "route", "show", "table", "all");
        foreach (var line in routes.Split('\n'))
        {
            var token = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (token is null || token == "default") continue;
            var parts = token.Split('/');
            if (!IPAddress.TryParse(parts[0], out var routeIp) || routeIp.AddressFamily != AddressFamily.InterNetwork) continue;
            var prefix = parts.Length == 2 && int.TryParse(parts[1], out var p) ? p : 32;
            if (prefix is < 0 or > 32) continue;
            var route = routeIp.GetAddressBytes();
            var bits = Math.Min(prefix, 24);
            var overlaps = true;
            for (var i = 0; i < 4; i++)
            {
                var mask = bits >= 8 ? 255 : bits <= 0 ? 0 : 256 - (1 << (8 - bits));
                if ((network[i] & mask) != (route[i] & mask)) { overlaps = false; break; }
                bits -= 8;
            }
            if (overlaps) throw new InvalidOperationException($"Overlay {overlay} overlaps host route {token}.");
        }
    }

    internal static void Init(ServerFiles files, ServerSettings settings)
    {
        Preflight(settings, files);
        files.EnsureDirectories();
        new Pki(files).Init();
        var state = new ServerState { Settings = settings };
        files.Render(state);
        files.WriteState(state);
    }

    private static void Add(ServerFiles files, string name, string bundlePath, string passphraseFile, string? requestedId)
    {
        if (name.Length is < 1 or > 64 || name.Any(char.IsControl)) throw new ArgumentException("Invalid device name.");
        if (File.Exists(bundlePath)) throw new IOException("Bundle file already exists.");
        if (OperatingSystem.IsLinux())
        {
            var mode = File.GetUnixFileMode(passphraseFile);
            if ((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) != 0)
                throw new UnauthorizedAccessException("Passphrase file must be readable only by its owner (chmod 600).");
        }
        var passphrase = File.ReadAllText(passphraseFile).TrimEnd('\r', '\n');
        if (passphrase.Length < 12) throw new ArgumentException("Bundle passphrase must contain at least 12 characters.");
        var state = files.ReadState();
        var id = requestedId is null ? Guid.NewGuid().ToString("N") : Guid.Parse(requestedId).ToString("N");
        if (state.Devices.Any(d => d.Id == id)) throw new InvalidOperationException("Device ID already exists. Retrieve its original bundle or use a new operation ID after revoking it.");
        var overlay = state.Settings.Overlay();
        var prefix = overlay.ServerIp[..overlay.ServerIp.LastIndexOf('.')];
        var used = state.Devices.Select(d => d.VirtualIp).ToHashSet(StringComparer.Ordinal);
        var host = Enumerable.Range(10, 245).FirstOrDefault(i => !used.Contains($"{prefix}.{i}"));
        if (host == 0) throw new InvalidOperationException("Overlay address pool exhausted.");
        var cn = "device-" + id;
        var pki = new Pki(files);
        var (cert, key, tls) = pki.AddDevice(cn);
        var record = new DeviceRecord
        {
            Id = id, Name = name, VirtualIp = $"{prefix}.{host}",
            HysteriaPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
            VlessId = Guid.NewGuid(), CertificatePath = cert, Active = true
        };
        var s = state.Settings;
        var profile = new DeviceProfile
        {
            DeviceId = id, DeviceName = name, VirtualIp = record.VirtualIp, OverlayCidr = s.OverlayCidr,
            ServerIp = overlay.ServerIp, PublicHost = s.PublicHost, UdpPort = s.UdpPort, TcpPort = s.TcpPort,
            OpenVpnPort = s.OpenVpnPort, HealthPort = s.HealthPort, EnableUdp = s.EnableUdp, EnableTcp = s.EnableTcp,
            HysteriaServerName = s.HysteriaServerName,
            HysteriaCertificateSha256 = s.EnableUdp ? ConfigRenderer.CertificateFingerprint(File.ReadAllText(s.HysteriaCertificate)) : "",
            HysteriaAuth = id + ":" + record.HysteriaPassword, VlessId = record.VlessId,
            RealityServerName = s.RealityServerName, RealityPublicKey = s.RealityPublicKey,
            RealityShortId = s.RealityShortId, XhttpPath = s.XhttpPath,
            CaPem = File.ReadAllText(Path.Combine(files.Pki, "ca.crt")), CertificatePem = File.ReadAllText(cert),
            PrivateKeyPem = File.ReadAllText(key), TlsCryptV2Key = File.ReadAllText(tls)
        };
        var encrypted = BundleCrypto.Encrypt(profile, passphrase);
        state.Devices.Add(record);
        var committed = false;
        var bundleCreated = false;
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var output = new FileStream(bundlePath, options))
            {
                bundleCreated = true;
                output.Write(encrypted);
            }
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(bundlePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            files.Render(state);
            ServerFiles.AtomicWrite(Path.Combine(files.Ccd, cn), $"ifconfig-push {record.VirtualIp} {overlay.Mask}\n");
            files.WriteState(state);
            committed = true;
        }
        finally { if (bundleCreated && !committed) File.Delete(bundlePath); }
        File.Delete(key);
        File.Delete(tls);
        ServerFiles.RestartOuter(s);
        Console.WriteLine($"Device {id} assigned {record.VirtualIp}; encrypted bundle written to {bundlePath}.");
    }

    private static void Revoke(ServerFiles files, string id)
    {
        var state = files.ReadState();
        var record = state.Devices.SingleOrDefault(d => d.Id == id && d.Active) ?? throw new ArgumentException("Active device not found.");
        new Pki(files).Revoke(record.CertificatePath);
        record.Active = false;
        files.Render(state);
        File.Delete(Path.Combine(files.Ccd, "device-" + record.Id));
        files.WriteState(state);
        var kicked = OpenVpnManagement.Kill(state.Settings.ManagementPort, Path.Combine(files.Pki, "management.password"), "device-" + record.Id);
        if (!kicked)
        {
            if (ServerRuntime.IsContainer) ServerRuntime.Request("restart-openvpn");
            else if (ProcessRunner.Exists("systemctl") && ProcessRunner.RunResult("systemctl", "is-active", "--quiet", "eqlink-openvpn").ExitCode == 0)
                ProcessRunner.Run("systemctl", "restart", "eqlink-openvpn");
        }
        ServerFiles.RestartOuter(state.Settings);
        Console.WriteLine($"Revoked device {id}.");
    }
}

internal static class OpenVpnManagement
{
    internal static bool Kill(int port, string passwordFile, string cn)
    {
        try
        {
            using var client = new TcpClient();
            client.Connect(IPAddress.Loopback, port);
            client.ReceiveTimeout = 3000;
            client.SendTimeout = 3000;
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
            writer.WriteLine(File.ReadAllText(passwordFile).Trim());
            if (!ReadUntil(reader, "SUCCESS: password is correct", 10)) return false;
            writer.WriteLine("status 3");
            var connected = false;
            var statusComplete = false;
            for (var i = 0; i < 4096; i++)
            {
                var statusLine = reader.ReadLine();
                if (statusLine is null || statusLine.Contains("ERROR:", StringComparison.Ordinal)) return false;
                if (statusLine.StartsWith("CLIENT_LIST", StringComparison.Ordinal) &&
                    statusLine.Split('\t', ',').Contains(cn, StringComparer.Ordinal)) connected = true;
                if (statusLine == "END") { statusComplete = true; break; }
            }
            if (!statusComplete) return false;
            if (!connected) { writer.WriteLine("quit"); return true; }
            writer.WriteLine("kill " + cn);
            var kicked = ReadUntil(reader, "SUCCESS:", 10);
            writer.WriteLine("quit");
            return kicked;
        }
        catch { return false; }
    }

    private static bool ReadUntil(StreamReader reader, string prefix, int maxLines)
    {
        for (var i = 0; i < maxLines; i++)
        {
            var line = reader.ReadLine();
            if (line is null || line.Contains("ERROR:", StringComparison.Ordinal)) return false;
            if (line.Contains(prefix, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}

internal static class Health
{
    internal static async Task RunAsync(int port, CancellationToken stop = default)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop);
                _ = RespondAsync(client, stop);
            }
        }
        finally { listener.Stop(); }
    }

    private static async Task RespondAsync(TcpClient client, CancellationToken stop)
    {
        using (client)
        {
            client.ReceiveTimeout = 3000;
            using var stream = client.GetStream();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var buffer = new byte[5];
            try
            {
                await stream.ReadExactlyAsync(buffer, timeout.Token);
                if (Encoding.ASCII.GetString(buffer) == "PING\n") await stream.WriteAsync("PONG\n"u8.ToArray(), timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        }
    }
}
