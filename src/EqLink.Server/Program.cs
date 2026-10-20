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
                    throw new ArgumentException("BUNDLE_V2_REQUIRED: use enrollment-create; password device bundles are retired.");
                case "enrollment-create":
                    var bundlePath = Path.GetFullPath(Required(options, "bundle"));
                    if (File.Exists(bundlePath)) throw new IOException("Bundle already exists; choose a new private output file.");
                    var bundle = new EnrollmentService(files).CreateGrant(Required(options, "name"));
                    var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                    if (OperatingSystem.IsLinux()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                    using (var output = new FileStream(bundlePath, fileOptions)) { output.Write(EnrollmentBundle.Serialize(bundle)); output.Flush(true); }
                    Console.WriteLine("Universal enrollment bundle created; protect this bearer credential. Grant: " + bundle.GrantId);
                    return 0;
                case "enrollment-list":
                    foreach (var grant in files.ReadState().Grants) Console.WriteLine($"{grant.Id}  {(grant.Active ? "active" : "revoked")}  {grant.Name}");
                    return 0;
                case "enrollment-revoke":
                    new EnrollmentService(files).RevokeGrant(Guid.Parse(Required(options, "id")).ToString("N"));
                    return 0;
                case "migrate-v2":
                    Migrate(files, Required(options, "backup"));
                    return 0;
                case "list":
                    foreach (var d in files.ReadState().Devices)
                        Console.WriteLine($"{d.Id}  {d.VirtualIp}  {(d.Active ? "active" : "revoked")}  {d.Name}");
                    return 0;
                case "revoke":
                    new EnrollmentService(files).RevokeDevice(Guid.Parse(Required(options, "id")).ToString("N"));
                    return 0;
                case "apply":
                    var state = files.ReadState();
                    EnrollmentService.RequireV2(state);
                    files.Render(state);
                    Console.WriteLine("Validated durable configuration; new identities are applied through dynamic APIs. Public setting changes require a coordinated supervisor restart.");
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

    private const string Usage = "EqLink.Server preflight|init|serve --settings file [--root dir]; enrollment-create --name text --bundle private-file.eqlink; enrollment-list|enrollment-revoke --id uuid; list|revoke --id uuid|apply|refresh-crl; migrate-v2 --backup private-file.tar.gz; inspect --json; backup --output private-file.tar.gz; probe --kind live|ready|status";

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
        foreach (var port in new[] { settings.OpenVpnPort, settings.HealthPort, settings.ManagementPort, settings.AuthPort, settings.XrayApiPort, settings.HysteriaStatsPort })
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
        var state = new ServerState { Settings = settings, SchemaVersion = 2, NetworkId = Guid.NewGuid().ToString("N"), HysteriaApiSecret = CredentialSecrets.New() };
        files.Render(state);
        files.WriteState(state);
    }

    private static void Migrate(ServerFiles files, string backupPath)
    {
        using var offline = new FileStream(Path.Combine(files.Root,".instance.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var state = files.ReadState();
        if (state.SchemaVersion == 2) { EnrollmentService.RequireV2(state); Console.WriteLine("Already migrated."); return; }
        foreach (var port in new[] { state.Settings.OpenVpnPort, state.Settings.HealthPort, state.Settings.ManagementPort }) ServerFiles.CheckPort(port, ProtocolType.Tcp);
        if (state.Settings.EnableTcp) ServerFiles.CheckPort(state.Settings.TcpPort, ProtocolType.Tcp);
        if (state.Settings.EnableUdp) ServerFiles.CheckPort(state.Settings.UdpPort, ProtocolType.Udp);
        var destination = Path.GetFullPath(backupPath);
        if (!OperatingSystem.IsLinux() || destination.StartsWith(files.Root.TrimEnd('/') + "/", StringComparison.Ordinal)) throw new ArgumentException("Private backup must be outside state directory on Linux.");
        using (var backup = new FileStream(destination, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite })) backup.Flush(true);
        ProcessRunner.Run("tar", "--exclude=./.state.lock", "-C", files.Root, "-czf", destination, ".");
        var pki = new Pki(files);
        foreach (var device in state.Devices.Where(d => d.Active)) { pki.Revoke(device.CertificatePath); device.Active = false; }
        state = state with { SchemaVersion = 2, NetworkId = Guid.NewGuid().ToString("N"), HysteriaApiSecret = CredentialSecrets.New(), Grants = [] };
        files.Commit(state);
        Console.WriteLine("Migration complete. Legacy identities disabled. CA, REALITY keys and network preserved. Create a universal enrollment bundle before starting clients.");
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
            if (kicked)
            {
                kicked = false;
                for (var attempt = 0; attempt < 10; attempt++)
                {
                    Thread.Sleep(100); writer.WriteLine("status 3"); var present = false; var complete = false;
                    for (var i = 0; i < 4096; i++)
                    {
                        var statusLine = reader.ReadLine(); if (statusLine is null || statusLine.Contains("ERROR:", StringComparison.Ordinal)) return false;
                        if (statusLine.StartsWith("CLIENT_LIST", StringComparison.Ordinal) && statusLine.Split('\t', ',').Contains(cn, StringComparer.Ordinal)) present = true;
                        if (statusLine == "END") { complete = true; break; }
                    }
                    if (complete && !present) { kicked = true; break; }
                }
            }
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
