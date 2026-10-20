using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EqLink.ClientGui;
using EqLink.ClientService;
using EqLink.Core;

internal static class Program
{
    private static readonly string Root = Path.GetFullPath(Environment.GetEnvironmentVariable("EQLINK_GUI_TEST_OUTPUT") ?? "artifacts/gui-validation-1.0.0");
    private static readonly List<string> Results = [];

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.SequenceEqual(new[] { "--trust-only" }))
        {
            try { CheckIdentityImportAsync().GetAwaiter().GetResult(); CheckOuterFailuresAsync().GetAwaiter().GetResult(); Console.WriteLine("Isolated DPAPI/import/scoped CA/diagnostic checks passed."); return 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        if (args.SequenceEqual(new[] { "--outer-failure-only" }))
        {
            try { CheckOuterFailuresAsync().GetAwaiter().GetResult(); Console.WriteLine("Isolated daemon failure classification checks passed."); return 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        if (args.SequenceEqual(new[]{"--mode-worker"})) { Thread.Sleep(60000); return 0; }
        var result = 1;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try { await RunAsync(); result = 0; Console.WriteLine("Isolated backend and WPF checks passed."); }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { app.Shutdown(result); }
        };
        app.Run();
        return result;
    }

    private static void Check(bool success, string name)
    {
        if (!success) throw new Exception(name);
        Results.Add(name);
    }

    private static async Task CheckOuterFailuresAsync()
    {
        var type = typeof(EqLinkDaemon);
        foreach (var (line, expected) in new[] {
            ("authentication error: x509: certificate signed by unknown authority", "OUTER_CA_UNTRUSTED"),
            ("authentication error: no certificate matches the pinned hash", "OUTER_CERT_PIN_MISMATCH"),
            ("authentication failed: invalid username/password", "OUTER_AUTH_REJECTED") })
        {
            var directory = Path.Combine(Root, "outer-failure-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var daemon = new EqLinkDaemon();
            void Set(string name, object? value) => type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(daemon, value);
            Set("_root", directory); Set("_protectionEnabled", false); Set("_generation", 1);
            Set("_profile", new DeviceProfile { DeviceId = Guid.NewGuid().ToString("N"), DeviceName = "fixture", VirtualIp = "", OverlayCidr = "172.30.251.0/24", ServerIp = "172.30.251.1", PublicHost = "fixture.invalid", UdpPort = 30444, TcpPort = 30443, OpenVpnPort = 1194, HealthPort = 1195, EnableUdp = true, EnableTcp = true, CaPem = "unused", CertificatePem = "", PrivateKeyPem = "", TlsCryptV2Key = "" });
            Set("_mode", TransportMode.UdpOnly); Set("_wantConnection", true);
            var secret = CredentialSecrets.New();
            try
            {
                async Task Drain(int generation)
                {
                    using var stream = new MemoryStream(Encoding.UTF8.GetBytes(line + " auth=" + secret + "\n"));
                    using var reader = new StreamReader(stream);
                    await (Task)type.GetMethod("DrainAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(daemon, [reader, ActivePath.Udp, generation])!;
                }
                await Drain(0);
                Check(!(bool)type.GetField("_udpAuthFailure", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(daemon)!, "Old generation transport output is ignored");
                await Drain(1);
                await (Task)type.GetMethod("ObserveNetworkAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(daemon, [CancellationToken.None])!;
                var dispatch = type.GetMethod("DispatchAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var response = await (Task<IpcResponse>)dispatch.Invoke(daemon, [new IpcRequest("diagnostics")])!;
                Check(response.Status is { Connected: false, Phase: "Error" } && response.Status.ErrorCode == expected, "Terminal cleanup preserves the specific transport failure: " + expected);
                var json = JsonSerializer.Serialize(response.Diagnostics, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                using var doc = JsonDocument.Parse(json);
                Check(doc.RootElement.GetProperty("outerFailures").GetProperty("udp").GetProperty("code").GetString() == expected && !json.Contains(secret), "Per-path diagnostics preserves only fixed failure fields: " + expected);
            }
            finally { await daemon.StopAsync(); Directory.Delete(directory); }
        }
    }

    private static async Task RunAsync()
    {
        var clientRoot = Path.Combine(Root, "isolated-client-root");
        Directory.CreateDirectory(clientRoot);
        var file = Path.Combine(clientRoot, "egress.json");
        var daemon = new EqLinkDaemon();
        typeof(EqLinkDaemon).GetField("_root", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(daemon, clientRoot);
        var dispatch = typeof(EqLinkDaemon).GetMethod("DispatchAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var handle = typeof(EqLinkDaemon).GetMethod("HandlePipeAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var want = typeof(EqLinkDaemon).GetField("_wantConnection", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var writes = 0;
        async Task<IpcResponse> Send(IpcRequest request)
        {
            if (request.Command == "egress-set") writes++;
            return await (Task<IpcResponse>)dispatch.Invoke(daemon, [request])!;
        }
        async Task<IpcResponse> SendThroughPipe(IpcRequest request)
        {
            var name = "EqLink.EgressTest." + Guid.NewGuid().ToString("N");
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var accepted = server.WaitForConnectionAsync(stop.Token);
            await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
            await client.ConnectAsync(stop.Token);
            await accepted;
            var handling = (Task)handle.Invoke(daemon, [server, stop.Token])!;
            await client.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)) + "\n"), stop.Token);
            using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
            var line = await reader.ReadLineAsync(stop.Token) ?? throw new IOException("Missing pipe response");
            await handling;
            return JsonSerializer.Deserialize<IpcResponse>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        }
        var initial = await Send(new IpcRequest("egress-get"));
        Check(initial.Egress is { Settings: null, Locked: false, Error: null }, "New client uses system routing");
        var nic = initial.Egress!.Interfaces.First(n => n.IsUp && n.Ipv4Addresses.Length > 0);
        var saved = await Send(new IpcRequest("egress-set", Egress: new(nic.Id, ["223.5.5.5"])));
        Check(saved.Ok && saved.Status.Egress == nic.Name && File.Exists(file), "Backend persists adapter and reports its name while disconnected");
        var original = File.ReadAllBytes(file);
        want.SetValue(daemon, true);
        Check((await Send(new IpcRequest("egress-get"))).Egress!.Locked, "Backend reports connection lock during reconnect");
        try { await Send(new IpcRequest("egress-set")); throw new Exception("Connection lock was bypassed"); }
        catch (InvalidOperationException) { }
        Check(File.ReadAllBytes(file).SequenceEqual(original), "Connection lock preserves saved binding");
        want.SetValue(daemon, false);
        try { await Send(new IpcRequest("egress-set", Egress: new("not-present", ["223.5.5.5"]))); throw new Exception("Missing interface accepted"); }
        catch (InvalidOperationException) { }
        Check(File.ReadAllBytes(file).SequenceEqual(original), "Missing interface cannot replace the binding");
        File.WriteAllText(file, "null");
        Check((await Send(new IpcRequest("egress-get"))).Egress!.Error is not null, "Corrupt configuration remains visible and can be repaired");
        await Send(new IpcRequest("egress-set", Egress: new(nic.Id, ["223.5.5.5"])));
        Check((await SendThroughPipe(new IpcRequest("egress-get"))).Egress!.Settings?.InterfaceId == nic.Id, "Read-only settings use the actual named-pipe handler");
        var admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        var authorized = await SendThroughPipe(new IpcRequest("egress-set", Egress: new(nic.Id, ["223.5.5.5"])));
        Check(authorized.Ok == admin, admin ? "Administrator can save through the actual pipe handler" : "Unelevated user cannot save through the actual pipe handler");

        var window = new EgressWindow(Send);
        var load = typeof(EgressWindow).GetMethod("LoadAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)load.Invoke(window, [false])!;
        var box = (ComboBox)window.FindName("InterfaceBox");
        var dns = (TextBox)window.FindName("DnsBox");
        var save = (Button)window.FindName("SaveButton");
        string? SelectedId() => (string?)box.SelectedItem.GetType().GetProperty("Id")!.GetValue(box.SelectedItem);
        Check(SelectedId() == nic.Id && dns.Text == "223.5.5.5" && save.IsEnabled, "GUI loads the persisted adapter and DNS");
        Render(window, "egress-settings.png");
        var preview = new ClientStatus(true, true, "本机测试设备", "172.21.10.10", TransportMode.Auto, ActivePath.Udp, "虚拟网络已连接", "以太网 3", "Connected", Protection: new(true,true,true,"172.21.10.0/24"), Metrics: new(3,123456,2097152,DateTimeOffset.UtcNow), Registration: "Registered", Address: new(), Server: "eqlink-test.chensnetwork.com");
        var inFlight = 0; var maximum = 0;
        var model = new MainViewModel(async _ => { var active = Interlocked.Increment(ref inFlight); maximum = Math.Max(maximum,active); await Task.Delay(20); Interlocked.Decrement(ref inFlight); return new IpcResponse(true,null,preview); });
        await Task.WhenAll(Enumerable.Range(0,8).Select(_ => model.RefreshAsync()));
        Check(maximum == 1 && model.TcpConnections == "3" && model.Upload.EndsWith("KiB/s") && model.Download == "2.0 MiB/s", "Status refresh is single-flight and statistics use the documented units");
        var action = model.ExecuteAsync(new("connect"));
        try { await model.ExecuteAsync(new("connect")); throw new Exception("Duplicate action accepted"); } catch(InvalidOperationException) { }
        await action;
        Check(!model.CanConfigure && !model.CanConnect && model.CanDisconnect, "Connected state locks settings and repeated connection actions");
        var main = new MainWindow(model);
        model.HostsText = "172.21.10.2 rancher.example.test\n172.21.10.10 files.example.test";
        model.HostsEnabled = true;
        for (var section = 0; section < 4; section++)
        {
            ((TabControl)main.FindName("Sections")).SelectedIndex = section;
            Render(main, $"main-960-tab-{section}.png", 944, 640);
            Render(main, $"main-820-tab-{section}.png", 804, 560);
            Check(((Button)main.FindName("ConnectButton")).ActualHeight >= 36 && ((Button)main.FindName("DisconnectButton")).ActualHeight >= 36, "Persistent actions remain laid out at minimum size: " + section);
        }
        Check(!Descendants(main).OfType<PasswordBox>().Any(), "Password import controls are removed");
        Check(((Button)main.FindName("HostsSaveButton")).IsEnabled && model.CanEditHosts, "Hosts editor stays available on a connected VPN");
        var hostsWrites = 0;
        var hostSettings = HostsOverrideSettings.Parse(true, "172.21.10.2 rancher.example.test", "172.21.10.0/24");
        Task<IpcResponse> HostsTransport(IpcRequest request)
        {
            if (request.Command == "hosts-set") { hostsWrites++; hostSettings = request.Hosts! with { Revision = hostSettings.Revision + 1 }; }
            return Task.FromResult(new IpcResponse(true, null, preview, Hosts: hostSettings));
        }
        var hostsModel = new MainViewModel(HostsTransport); await hostsModel.RefreshAsync(); await hostsModel.LoadHostsAsync();
        Check(hostsModel.HostsText.Contains("rancher.example.test") && hostsModel.HostsEnabled, "Hosts GUI loads the independent settings");
        hostsModel.HostsText = "192.168.1.2 invalid.example.test";
        try { await hostsModel.SaveHostsAsync(HostsTransport); throw new Exception("Out-of-overlay mapping accepted"); } catch (ArgumentException) { }
        Check(hostsWrites == 0, "Hosts GUI validates before elevation or IPC write");
        hostsModel.HostsText = "172.21.10.1 server.example.test"; await hostsModel.SaveHostsAsync(HostsTransport);
        Check(hostsWrites == 1 && hostSettings.Revision == 1 && hostsModel.Status.Connected, "Hosts save preserves connected state and adopts the new revision");
        if (!admin) Check(!(await SendThroughPipe(new("hosts-set", Hosts: hostSettings))).Ok, "Unelevated user cannot modify hosts through actual IPC");
        var hostRoot = Path.Combine(Root, "hosts-manager"); Directory.CreateDirectory(hostRoot);
        var managedHostsPath = Path.Combine(hostRoot, "system-hosts-fixture"); File.WriteAllText(managedHostsPath, "# Foreign fixture\r\n");
        await using (var manager = new HostsOverrideManager(hostRoot, new WindowsHostsFile(managedHostsPath, Guid.NewGuid())))
        {
            var networkA = Guid.NewGuid().ToString(); var networkB = Guid.NewGuid().ToString();
            await manager.SetNetworkAsync(networkA, "172.21.10.0/24");
            await manager.SaveAsync(HostsOverrideSettings.Parse(false, "172.21.10.2 rancher.example.test", "172.21.10.0/24"));
            try { await manager.SaveAsync(manager.Settings with { Revision = 0 }); throw new Exception("Stale hosts revision accepted"); } catch (InvalidOperationException) { }
            Check(manager.Settings.Revision == 1, "Concurrent hosts editor revision rejects stale overwrite");
            await manager.SetNetworkAsync(networkB, "172.21.10.0/24");
            Check(manager.Settings.Entries.Length == 0, "Changing networks does not apply another network's hosts");
            await manager.SetNetworkAsync(networkA, "172.21.10.0/24");
            Check(manager.Settings.Entries.Length == 1 && !manager.Settings.Enabled, "Disabled mappings survive network switch and retain entries");
            await manager.SaveAsync(manager.Settings with { Enabled = true });
            Check(manager.Status.Applied && File.ReadAllText(managedHostsPath).Contains("rancher.example.test"), "Enabling hosts applies the managed block");
            await manager.SetNetworkAsync(networkB, "172.21.10.0/24");
            Check(File.ReadAllText(managedHostsPath) == "# Foreign fixture\r\n", "Switching network removes only the previous managed block");
            await manager.SetNetworkAsync(networkA, "172.21.10.0/24");
            Check(manager.Status.Applied, "Saved enabled hosts return when the same network is loaded");
        }
        Check(File.ReadAllText(managedHostsPath) == "# Foreign fixture\r\n", "Manager disposal cleans its own block");
        Check(MainViewModel.Rate(null) == "—" && MainViewModel.Rate(0) == "0 B/s", "Unavailable statistics are distinct from disconnected zero");
        Check((await Send(new IpcRequest("diagnostics"))).Diagnostics is not null, "Read-only diagnostics is available through dispatch");
        typeof(EqLinkDaemon).GetField("_authFailure",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(daemon,true);
        want.SetValue(daemon,true);
        var rejected=(Task)typeof(EqLinkDaemon).GetMethod("RunOpenVpnLoopAsync",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(daemon,[0])!;
        await rejected;
        var failedAuth=(await Send(new IpcRequest("status"))).Status;
        Check(failedAuth is {Connected:false,Path:ActivePath.None,Phase:"Error",ErrorCode:"AUTH_FAILED"} && !(bool)want.GetValue(daemon)!,"Inner certificate rejection stays terminal after transport cleanup");
        if(!admin)Check(!(await SendThroughPipe(new IpcRequest("protection-set",ProtectionEnabled:false))).Ok,"Unelevated user cannot disable subnet protection");
        main.Close();
        box.SelectedIndex = 0;
        dns.Text = "192.168.1.1";
        await (Task)load.Invoke(window, [true])!;
        Check(SelectedId() is null && dns.Text == "192.168.1.1" && !dns.IsEnabled, "Refreshing adapters preserves unsaved system-route choice and DNS");
        want.SetValue(daemon, true);
        await (Task)load.Invoke(window, [false])!;
        Check(!save.IsEnabled && !box.IsEnabled && !dns.IsEnabled, "GUI locks editing while connected or reconnecting");
        Render(window, "egress-settings-locked.png");
        want.SetValue(daemon, false);
        await (Task)load.Invoke(window, [false])!;
        var click = typeof(EgressWindow).GetMethod("Save_Click", BindingFlags.NonPublic | BindingFlags.Instance)!;
        dns.Text = "invalid";
        var before = writes;
        click.Invoke(window, [save, new RoutedEventArgs()]);
        await Task.Yield();
        Check(writes == before && ((TextBlock)window.FindName("ResultText")).Text.Length > 0, "GUI rejects invalid DNS before asking the service to save");
        box.SelectedIndex = 0;
        click.Invoke(window, [save, new RoutedEventArgs()]);
        await Task.Yield();
        Check(!File.Exists(file) && (await Send(new IpcRequest("status"))).Status.Egress == "系统路由", "GUI can explicitly restore system routing");
        await CheckIdentityImportAsync();
        await CheckModeProcessesAsync();
        await CheckOuterFailuresAsync();
        File.WriteAllText(Path.Combine(Root, "results.json"), JsonSerializer.Serialize(new
        {
            checkedAt = DateTimeOffset.Now,
            scope = "Temporary configuration root; daemon dispatch and WPF controls only. Installed service and active connection were not changed.",
            results = Results
        }, new JsonSerializerOptions { WriteIndented = true }));
        window.Close();
        Directory.Delete(clientRoot);
    }

    private static async Task CheckModeProcessesAsync()
    {
        var directory=Path.Combine(Root,"mode-process-fixture");Directory.CreateDirectory(directory);
        var daemon=new EqLinkDaemon();var type=typeof(EqLinkDaemon);
        void Set(string name,object? value)=>type.GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(daemon,value);
        var profile=new DeviceProfile{DeviceId=Guid.NewGuid().ToString("N"),DeviceName="fixture",VirtualIp="172.30.251.10",OverlayCidr="172.30.251.0/24",ServerIp="172.30.251.1",PublicHost="fixture.invalid",UdpPort=30444,TcpPort=30443,OpenVpnPort=1194,HealthPort=1195,EnableUdp=true,EnableTcp=true,CaPem="unused",CertificatePem="unused",PrivateKeyPem="unused",TlsCryptV2Key="unused"};
        var workers=new List<System.Diagnostics.Process>();
        System.Diagnostics.Process Worker()
        {
            var start=new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true};
            if(Path.GetFileNameWithoutExtension(Environment.ProcessPath)=="dotnet")start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory,"EqLink.GuiTests.dll"));
            start.ArgumentList.Add("--mode-worker");var p=System.Diagnostics.Process.Start(start)!;workers.Add(p);return p;
        }
        var hy=Worker();var xr=Worker();
        await using var selector=new ProxySelector(profile,()=>TransportMode.Auto,(_,_)=>{},CredentialSecrets.New());
        try
        {
            Set("_root",directory);Set("_profile",profile);Set("_selector",selector);Set("_wantConnection",true);Set("_vpnConnected",true);Set("_path",ActivePath.Tcp);Set("_hysteria",hy);Set("_xray",xr);
            async Task Mode(string value)=>await (Task<IpcResponse>)type.GetMethod("DispatchAsync",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(daemon,[new IpcRequest("mode",Mode:value)])!;
            await Mode("TcpOnly");
            Check(hy.HasExited && !xr.HasExited && (bool)type.GetField("_vpnConnected",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(daemon)!,"TCP-only stops UDP worker and preserves a healthy TCP session");
            var nextHy=Worker();Set("_hysteria",nextHy);await Mode("Auto");
            Check(!xr.HasExited && (bool)type.GetField("_vpnConnected",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(daemon)!,"Auto mode preserves the existing healthy TCP session");
            await Mode("UdpOnly");
            Check(xr.HasExited && !nextHy.HasExited && !(bool)type.GetField("_vpnConnected",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(daemon)!,"UDP-only stops TCP worker and requests controlled reconnect");
        }
        finally
        {
            foreach(var worker in workers){if(!worker.HasExited)worker.Kill(true);await worker.WaitForExitAsync();worker.Dispose();}
            Directory.Delete(directory,true);
        }
    }

    private static async Task CheckIdentityImportAsync()
    {
        using var rsa=RSA.Create(3072);
        var request=new CertificateRequest("CN=EqLink-fixture",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true,false,0,true));
        using var root=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddDays(1));
        var id=Guid.NewGuid().ToString("N");
        var bundle=new EnrollmentBundle {GrantId=id,EnrollmentToken=CredentialSecrets.New(),HysteriaAuth=id+":"+CredentialSecrets.New(),VlessId=Guid.NewGuid(),Network=new(){NetworkId=Guid.NewGuid().ToString("N"),PublicHost="fixture.invalid",OverlayCidr="172.30.251.0/24",CaPem=root.ExportCertificatePem(),EnableTcp=false,HysteriaServerName="fixture.invalid",HysteriaCertificateSha256=ConfigRenderer.CertificateFingerprint(root.ExportCertificatePem())}};
        var import=new IpcRequest("import",BundleBase64:Convert.ToBase64String(EnrollmentBundle.Serialize(bundle)));
        var roots=new List<string>();
        async Task<LocalInstallation> Import(EqLinkDaemon daemon)
        {
            var method=typeof(EqLinkDaemon).GetMethod("DispatchAsync",BindingFlags.NonPublic|BindingFlags.Instance)!;
            var reply=await (Task<IpcResponse>)method.Invoke(daemon,[import])!;
            Check(reply.Status is {Imported:true,Registration:"Pending",VirtualIp:""},"Universal import stays pending without inventing an IP");
            return (LocalInstallation)typeof(EqLinkDaemon).GetField("_installation",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(daemon)!;
        }
        EqLinkDaemon Client()
        {
            var path=Path.Combine(Root,"identity-fixture-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);roots.Add(path);
            var daemon=new EqLinkDaemon();typeof(EqLinkDaemon).GetField("_root",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(daemon,path);
            typeof(EqLinkDaemon).GetField("_protectionEnabled",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(daemon,false); // This fixture never touches the product WFP provider.
            return daemon;
        }
        try
        {
            var a=Client();var b=Client();var first=await Import(a);var second=await Import(b);var repeated=await Import(a);
            Check(first.Identity.PublicKeyFingerprint != second.Identity.PublicKeyFingerprint,"Same bundle creates separate local identities");
            Check(first.Identity == repeated.Identity,"Reimport retains local key/CSR identity");
            var embedded = bundle with { Network = bundle.Network with { HysteriaCaPem = root.ExportCertificatePem() } };
            import = new IpcRequest("import", BundleBase64: Convert.ToBase64String(EnrollmentBundle.Serialize(embedded)));
            var updated = await Import(a);
            Check(updated.Identity == first.Identity && updated.Bundle.Network.HysteriaCaPem == embedded.Network.HysteriaCaPem, "Adding a bundled CA retains the existing machine identity");
            var writeHy = typeof(EqLinkDaemon).GetMethod("WriteHysteriaConfiguration", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var scoped = embedded.Network.Bootstrap(embedded.HysteriaAuth, embedded.VlessId, "fixture");
            writeHy.Invoke(a, [scoped]);
            var caPath = Path.Combine(roots[0], "hysteria-ca.pem");
            using (var rendered = JsonDocument.Parse(File.ReadAllText(Path.Combine(roots[0], "hysteria.json"))))
                Check(rendered.RootElement.GetProperty("tls").GetProperty("ca").GetString() == caPath && File.ReadAllText(caPath) == embedded.Network.HysteriaCaPem, "Hysteria runtime uses only the derived CA file in the isolated EqLink directory");
            using (var machineStore = new X509Store(StoreName.Root, StoreLocation.LocalMachine))
            {
                machineStore.Open(OpenFlags.ReadOnly);
                Check(!machineStore.Certificates.Any(c => c.RawData.AsSpan().SequenceEqual(root.RawData)), "Import does not install the fixture root into Windows machine trust");
            }
            var diagnosticsReply = await (Task<IpcResponse>)typeof(EqLinkDaemon).GetMethod("DispatchAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(a, [new IpcRequest("diagnostics")])!;
            var diagnosticJson = JsonSerializer.Serialize(diagnosticsReply.Diagnostics, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            using (var diagnostic = JsonDocument.Parse(diagnosticJson))
                Check(diagnostic.RootElement.GetProperty("hysteriaTrust").GetProperty("source").GetString() == "Bundle" && !diagnosticJson.Contains(embedded.EnrollmentToken) && !diagnosticJson.Contains(embedded.Network.HysteriaCaPem), "Diagnostics reports bundled trust without tokens or raw PEM");
            import = new IpcRequest("import", BundleBase64: Convert.ToBase64String(EnrollmentBundle.Serialize(bundle)));
            Check((await Import(a)).Bundle.Network.HysteriaCaPem == embedded.Network.HysteriaCaPem, "Reimporting an older v2 bundle cannot erase scoped trust");
            using var differentKey = RSA.Create(3072);
            var differentRequest = new CertificateRequest("CN=other-fixture-root", differentKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            differentRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            using var differentCa = differentRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            import = new IpcRequest("import", BundleBase64: Convert.ToBase64String(EnrollmentBundle.Serialize(bundle with { Network = bundle.Network with { HysteriaCaPem = differentCa.ExportCertificatePem() } })));
            try { await Import(a); throw new Exception("Same-network CA replacement was accepted"); } catch (InvalidOperationException ex) when (ex.Message.StartsWith("TRUST_ANCHOR_CHANGED", StringComparison.Ordinal)) { }
            Check(true, "Unexpected same-network CA replacement is rejected before identity persistence");
            import = new IpcRequest("import", BundleBase64: Convert.ToBase64String(EnrollmentBundle.Serialize(embedded)));
            var restored=Client();typeof(EqLinkDaemon).GetField("_root",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(restored,roots[0]);
            typeof(EqLinkDaemon).GetMethod("LoadInstallation",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(restored,[]);
            var saved=(LocalInstallation)typeof(EqLinkDaemon).GetField("_installation",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(restored)!;
            Check(saved.Identity == first.Identity,"DPAPI reload retains the generated device identity");
            Check(saved.Bundle.Network.HysteriaCaPem == embedded.Network.HysteriaCaPem, "DPAPI reload preserves the public scoped CA");
            var encrypted=File.ReadAllBytes(Path.Combine(roots[0],"identity.dpapi"));
            Check(!Encoding.UTF8.GetString(encrypted).Contains(first.Identity.PrivateKeyPem,StringComparison.Ordinal),"Private key is not stored in plaintext");
            var connected=typeof(EqLinkDaemon).GetField("_vpnConnected",BindingFlags.NonPublic|BindingFlags.Instance)!;connected.SetValue(a,true);
            using var cancelled=new CancellationTokenSource();cancelled.Cancel();
            var observe=typeof(EqLinkDaemon).GetMethod("ObserveIcmpAsync",BindingFlags.NonPublic|BindingFlags.Instance)!;
            var profile=(DeviceProfile)typeof(EqLinkDaemon).GetField("_profile",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(a)!;
            for(var attempt=0;attempt<2;attempt++) await (Task)observe.Invoke(a,[profile,0,cancelled.Token])!;
            Check((bool)connected.GetValue(a)!,"Failed ICMP diagnostic tasks never revoke an established connection (isolated cancellation fixture)");
            var mode=typeof(EqLinkDaemon).GetField("_mode",BindingFlags.NonPublic|BindingFlags.Instance)!;
            var drain=typeof(EqLinkDaemon).GetMethod("DrainAsync",BindingFlags.NonPublic|BindingFlags.Instance)!;
            async Task AuthLog()
            { using var input=new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes("authentication error, HTTP status code: 404\n")));await (Task)drain.Invoke(a,[input,ActivePath.Udp,0])!; }
            mode.SetValue(a,TransportMode.TcpOnly);await AuthLog();
            var rejected=typeof(EqLinkDaemon).GetField("_udpAuthFailure",BindingFlags.NonPublic|BindingFlags.Instance)!;
            Check(!(bool)rejected.GetValue(a)!,"Disabled-path auth logs cannot reject a TCP-only session");
            mode.SetValue(a,TransportMode.Auto);await AuthLog();
            Check((bool)rejected.GetValue(a)!,"Explicit HY2 authentication error is classified as a rejection");
        }
        finally {foreach(var path in roots) { if (!Path.GetFullPath(path).StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unsafe fixture cleanup path"); Directory.Delete(path,true); }}
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        { var child = VisualTreeHelper.GetChild(parent,i); yield return child; foreach(var nested in Descendants(child)) yield return nested; }
    }

    private static void Render(Window window, string name, double width = 620, double? height = null)
    {
        var content = (FrameworkElement)window.Content;
        if (content is Panel panel) panel.Background = new SolidColorBrush(Color.FromRgb(246, 248, 251));
        content.Measure(new Size(width, height ?? double.PositiveInfinity));
        content.Arrange(new Rect(new Point(0, 0), new Size(width, height ?? content.DesiredSize.Height)));
        content.UpdateLayout();
        var image = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * 1.5), (int)Math.Ceiling(content.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen()) drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(246, 248, 251)), null, new Rect(new Point(0, 0), content.DesiredSize));
        image.Render(background);
        image.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(Path.Combine(Root, name));
        encoder.Save(output);
    }
}
