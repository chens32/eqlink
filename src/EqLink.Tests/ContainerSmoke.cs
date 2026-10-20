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
            var stablePids = ServicePids();
            Run("EqLink.Server", "enrollment-create", "--name", "smoke-network", "--bundle", Work + "/network.eqlink");
            var bundle = EnrollmentBundle.Parse(File.ReadAllBytes(Work + "/network.eqlink"));
            var bootstrap = bundle.Network.Bootstrap(bundle.HysteriaAuth,bundle.VlessId,"bootstrap");
            StartClients(bootstrap,"bootstrap",18081,18082);
            await Task.Delay(1200);
            var localA = LocalDeviceIdentity.Create(); var localB = LocalDeviceIdentity.Create();
            EnrollmentResponse enrolledA, enrolledB;
            using(var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
            {
                using var first = new ControlApiClient(bootstrap,18081,enrollmentToken:bundle.EnrollmentToken,connectTimeout:TimeSpan.FromSeconds(30));
                enrolledA = await first.EnrollAsync(new(localA.CsrPem,"smoke-A"),deadline.Token);
                var repeated = await first.EnrollAsync(new(localA.CsrPem,"smoke-A"),deadline.Token);
                Require(enrolledA.DeviceId == repeated.DeviceId && enrolledA.CertificatePem == repeated.CertificatePem,"CSR enrollment was not idempotent");
                using var second = new ControlApiClient(bootstrap,18082,enrollmentToken:bundle.EnrollmentToken,connectTimeout:TimeSpan.FromSeconds(30));
                enrolledB = await second.EnrollAsync(new(localB.CsrPem,"smoke-B"),deadline.Token);
            }
            var profile = Registered(enrolledA,localA); var profileB = Registered(enrolledB,localB);
            Require(profile.DeviceId != profileB.DeviceId && profile.VlessId != profileB.VlessId && profile.HysteriaAuth != profileB.HysteriaAuth && profile.CertificatePem != profileB.CertificatePem,"Two machines received shared credentials");
            using(var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45)))
            {
                using var a = new ControlApiClient(profile,18081,connectTimeout:TimeSpan.FromSeconds(30)); using var b = new ControlApiClient(profileB,18082,connectTimeout:TimeSpan.FromSeconds(30));
                var assignedA = await a.AddressAsync(new(),deadline.Token); var assignedB = await b.AddressAsync(new(),deadline.Token);
                Require(assignedA.VirtualIp == "10.88.0.10" && assignedB.VirtualIp == "10.88.0.11","Unexpected auto leases");
                Require((await a.AddressAsync(new(),deadline.Token)).VirtualIp == assignedA.VirtualIp,"Auto lease changed on retry");
                profile = profile with {VirtualIp=assignedA.VirtualIp}; profileB=profileB with {VirtualIp=assignedB.VirtualIp};
                foreach(var invalid in new[]{"10.88.0.1","10.88.1.10","10.88.0.11"})
                {
                    try { await a.AddressAsync(new(AddressMode.Static,invalid),deadline.Token); throw new Exception("Invalid/occupied address accepted"); }
                    catch(ControlApiException ex) when(ex.Code is "ADDRESS_RESERVED" or "ADDRESS_OUTSIDE_NETWORK" or "ADDRESS_IN_USE") { }
                }
                async Task<bool> Claim(ControlApiClient client)
                { try { await client.AddressAsync(new(AddressMode.Static,"10.88.0.42"),deadline.Token); return true; } catch(ControlApiException ex) when(ex.Code=="ADDRESS_IN_USE") {return false;} }
                var race = await Task.WhenAll(Claim(a),Claim(b)); Require(race.Count(v=>v)==1,"Concurrent address requests did not have exactly one winner");
                profile = profile with {VirtualIp=(await a.AddressAsync(new(AddressMode.Static,"10.88.0.10"),deadline.Token)).VirtualIp};
                profileB = profileB with {VirtualIp=(await b.AddressAsync(new(AddressMode.Static,"10.88.0.11"),deadline.Token)).VirtualIp};
            }
            Require(stablePids.SequenceEqual(ServicePids()),"Registration or address allocation restarted a server process");
            Console.WriteLine("ARM64 common bundle, independent CSR identities, idempotency, leases, concurrency and stable process IDs: passed");
            foreach(var item in Processes.Where(p=>p.Name.StartsWith("bootstrap-"))) {item.Process.Kill(true); await item.Process.WaitForExitAsync();}
            StartClients(profile,"device-A",18081,18082); StartClients(profileB,"device-B",18083,18084);
            await Task.Delay(1200);
            Require(await AuthenticatedHealth(profile,18081) && await AuthenticatedHealth(profile,18082) && await AuthenticatedHealth(profileB,18083) && await AuthenticatedHealth(profileB,18084),"Independent credentials failed on a real outer path");
            Console.WriteLine("ARM64 independent HY2 and XHTTP/REALITY credentials with TLS client authentication: passed");
            var selectorToken = CredentialSecrets.New();
            await using var selector = new ProxySelector(profile, () => TransportMode.Auto, (_,_)=>{}, selectorToken);
            selector.Start();
            File.WriteAllText(Work+"/client-openvpn.conf",ConfigRenderer.ClientOpenVpn(profile,18080,selectorToken)+"ifconfig-noexec\nroute-noexec\n");
            var innerClient = Start("openvpn-client","openvpn","--config",Work+"/client-openvpn.conf","--log",Work+"/client-openvpn.log");
            var initialized = false;
            for(var attempt=0;attempt<90;attempt++)
            {
                if(innerClient.HasExited) throw new IOException("OpenVPN fixture exited before initialization; inspect private fixture log");
                if(File.Exists(Work+"/client-openvpn.log") && File.ReadAllText(Work+"/client-openvpn.log").Contains("Initialization Sequence Completed",StringComparison.Ordinal)) { initialized=true; break; }
                await Task.Delay(1000);
            }
            Require(initialized,"Signed CSR/OpenVPN handshake timed out");
            Console.WriteLine("ARM64 signed CSR, tls-crypt-v2 and real OpenVPN TLS session over selected outer: passed (interface/route execution suppressed)");
            var inspection = Run("EqLink.Server","inspect","--json").Output;
            using(var parsed=JsonDocument.Parse(inspection)) Require(parsed.RootElement.GetProperty("checks").EnumerateObject().All(c=>c.Value.GetBoolean()),"Runtime inspection invariants failed: "+string.Join(",",parsed.RootElement.GetProperty("checks").EnumerateObject().Where(c=>!c.Value.GetBoolean()).Select(c=>c.Name)));
            foreach(var secret in new[]{bundle.EnrollmentToken,bundle.HysteriaAuth.Split(':')[1],profile.HysteriaAuth.Split(':')[1],profile.VlessId.ToString(),privateKey,profile.PrivateKeyPem,profile.TlsCryptV2Key}) Require(!inspection.Contains(secret,StringComparison.Ordinal),"Inspection exposed credentials");
            File.WriteAllText(Work+"/inspection.json",inspection);
            Run("EqLink.Server","backup","--output",Work+"/state-backup.tar.gz");
            Require(OperatingSystem.IsLinux() && File.GetUnixFileMode(Work+"/state-backup.tar.gz")== (UnixFileMode.UserRead|UnixFileMode.UserWrite),"Backup is not private");
            Console.WriteLine("ARM64 diagnostic whitelist and private state backup: passed");
            await using var forbidden = new TestBackend(18099,true);
            Require(!await HealthThroughAsync(18081,18099) && !await HealthThroughAsync(18082,18099),"Outer target allowlist was bypassed");
            Console.WriteLine("ARM64 outer target restrictions: passed");
            Run("EqLink.Server","enrollment-revoke","--id",bundle.GrantId);
            Require(await AuthenticatedHealth(profile,18081) && await AuthenticatedHealth(profileB,18084),"Enrollment revocation disabled registered devices");
            using(var denied = new ControlApiClient(bootstrap,18081,enrollmentToken:bundle.EnrollmentToken,connectTimeout:TimeSpan.FromSeconds(30)))
            using(var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                try { await denied.EnrollAsync(new(LocalDeviceIdentity.Create().CsrPem,"denied"),deadline.Token); throw new Exception("Revoked enrollment token accepted"); }
                catch(ControlApiException ex) when(ex.AuthenticationFailure) { }
            }
            Console.WriteLine("ARM64 enrollment revocation isolates new registration: passed");
            Run("EqLink.Server","revoke","--id",profile.DeviceId);
            Require(!await AuthenticatedHealth(profile,18081) && !await AuthenticatedHealth(profile,18082),"Revoked device retained control access");
            Require(await AuthenticatedHealth(profileB,18084),"Device revocation affected another identity");
            Require(!File.Exists("/etc/eqlink/ccd/device-"+profile.DeviceId),"Revoked device retained CCD");
            Console.WriteLine("ARM64 single-device revocation, CRL and unaffected peer: passed");
            using(var status=JsonDocument.Parse(Run("EqLink.Server","probe","--kind","status").Output))
            {
                var hyPid=status.RootElement.GetProperty("services").EnumerateArray().Single(v=>v.GetProperty("name").GetString()=="hysteria").GetProperty("pid").GetInt32();
                Process.GetProcessById(hyPid).Kill();
            }
            await Task.Delay(800);
            Require(Run("EqLink.Server","probe","--kind","ready",allowFailure:true).Code==0 && await AuthenticatedHealth(profileB,18084),"Single outer failure removed working TCP readiness");
            Console.WriteLine("ARM64 readiness with one failed outer path: passed");
            // SIGTERM is used rather than a forced kill to verify child cleanup.
            Run("kill", "-TERM", supervisor.Id.ToString());
            await supervisor.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Require(supervisor.ExitCode == 0, "Supervisor did not stop cleanly");
            Require(!File.Exists("/run/eqlink/control.sock"), "Control socket survived shutdown");
            var stateBefore = File.ReadAllText("/etc/eqlink/state.json");
            File.WriteAllText("/etc/eqlink/pending-state.json",stateBefore);
            File.WriteAllText("/etc/eqlink/ccd/device-"+profileB.DeviceId,"ifconfig-push 10.88.0.99 255.255.255.0\n");
            var restarted = Start("restart", "EqLink.Server", "serve", "--settings", "/run/eqlink-bootstrap/server-settings.json");
            await ReadyAsync();
            Require(File.ReadAllText("/etc/eqlink/state.json") == stateBefore, "Restart changed persistent devices/addresses");
            Require(!File.Exists("/etc/eqlink/pending-state.json") && File.ReadAllText("/etc/eqlink/ccd/device-"+profileB.DeviceId).Contains("10.88.0.11",StringComparison.Ordinal),"Interrupted journal/CCD commit was not recovered");
            Console.WriteLine("ARM64 graceful shutdown and persistent restart: passed");
            Console.WriteLine("ARM64 interrupted commit journal and CCD recovery: passed (interruption fixture)");
            Run("kill","-TERM",restarted.Id.ToString());await restarted.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            var legacy = JsonNode.Parse(stateBefore)!;legacy["schemaVersion"]=1;legacy["networkId"]="";legacy["hysteriaApiSecret"]="";
            File.WriteAllText("/etc/eqlink/state.json",legacy.ToJsonString());
            var caBefore = SHA256.HashData(File.ReadAllBytes("/etc/eqlink/pki/ca.crt"));
            Run("EqLink.Server","migrate-v2","--backup",Work+"/legacy-before-v2.tar.gz");
            var migrated = JsonSerializer.Deserialize<ServerState>(File.ReadAllText("/etc/eqlink/state.json"),new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Require(migrated.SchemaVersion==2 && migrated.Devices.All(d=>!d.Active) && migrated.Settings.RealityPrivateKey==settings.RealityPrivateKey && migrated.Settings.OverlayCidr==settings.OverlayCidr && SHA256.HashData(File.ReadAllBytes("/etc/eqlink/pki/ca.crt")).SequenceEqual(caBefore),"Offline migration changed CA/network/REALITY or kept legacy identities active");
            Console.WriteLine("ARM64 offline legacy migration, backup and CA/REALITY/network preservation: passed (legacy-state fixture)");
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

    private static DeviceProfile Registered(EnrollmentResponse response, LocalDeviceIdentity identity) => response.Network.Bootstrap(response.HysteriaAuth,response.VlessId,response.DeviceName) with
    {DeviceId=response.DeviceId,CertificatePem=response.CertificatePem,PrivateKeyPem=identity.PrivateKeyPem,TlsCryptV2Key=response.TlsCryptV2Key};
    private static int[] ServicePids()
    { using var status=JsonDocument.Parse(Run("EqLink.Server","probe","--kind","status").Output); return status.RootElement.GetProperty("services").EnumerateArray().Select(s=>s.GetProperty("pid").GetInt32()).ToArray(); }
    private static void StartClients(DeviceProfile profile,string name,int hyPort,int xrPort)
    {
        var hy=JsonNode.Parse(ConfigRenderer.ClientHysteria(profile,hyPort))!; hy["tls"]!["insecure"]=true; // Isolated self-signed fixture only.
        File.WriteAllText(Work+"/"+name+"-hy.json",hy.ToJsonString()); File.WriteAllText(Work+"/"+name+"-xr.json",ConfigRenderer.ClientXray(profile,xrPort));
        Start(name+"-hy","hysteria","client","-c",Work+"/"+name+"-hy.json"); Start(name+"-xr","xray","run","-c",Work+"/"+name+"-xr.json");
    }
    private static async Task<bool> AuthenticatedHealth(DeviceProfile profile,int proxy)
    { using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(12)); try {using var api=new ControlApiClient(profile,proxy,connectTimeout:TimeSpan.FromSeconds(30)); await api.HealthAsync(deadline.Token); return true;} catch(Exception ex) when(ex is IOException or HttpRequestException or OperationCanceledException) {return false;} }

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
