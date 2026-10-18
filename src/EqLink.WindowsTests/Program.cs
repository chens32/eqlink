using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
using EqLink.Core;
using EqLink.ClientService;

if (args.FirstOrDefault() == "--job-sleep") { await Task.Delay(Timeout.Infinite); return; }
if (args.FirstOrDefault() == "--job-holder")
{
    using var job = new OwnedProcessJob();
    using var owned = Process.Start(Self("--job-sleep"))!;
    job.Add(owned);
    Console.WriteLine(owned.Id);
    await Task.Delay(Timeout.Infinite);
    return;
}
if(args.FirstOrDefault() is not ("--isolated" or "--live" or "--hold"))
{ Console.Error.WriteLine("Usage: --isolated|--live OVERLAY_CIDR LOCAL_VIRTUAL_IP OUTPUT_JSON (administrator required)");Environment.ExitCode=2;return; }

var owner = new Guid("e7251ea7-a2f7-4453-975d-d41776ae6ac4"); // Dedicated acceptance provider, never the product provider.
var prefix = args.Length > 1 ? args[1] : "172.30.250.0/24";
var localIp = args.Length > 2 ? args[2] : "172.30.250.10";
var serverIp = prefix.Split('/')[0][..^1] + "1";
var profile = new DeviceProfile { DeviceId=Guid.NewGuid().ToString(), DeviceName="acceptance", VirtualIp=localIp, OverlayCidr=prefix, ServerIp=serverIp,
    PublicHost="example.invalid",UdpPort=30444,TcpPort=30443,OpenVpnPort=1194,HealthPort=1195,EnableUdp=true,EnableTcp=true,CaPem="unused",CertificatePem="unused",PrivateKeyPem="unused",TlsCryptV2Key="unused" };
if (args.FirstOrDefault() == "--hold")
{
    using var policy = new WindowsNetworkProtection(owner);
    policy.EnsureBlocked(prefix,localIp);
    var network=WindowsNetworkEnvironment.Read(profile,null);
    policy.Allow(network.VirtualLuid ?? throw new Exception("No virtual interface"));
    Console.WriteLine("READY");
    await Task.Delay(Timeout.Infinite);
    return;
}
var report = new Dictionary<string,object?> { ["timestamp"]=DateTimeOffset.UtcNow, ["scope"]="Dedicated WFP acceptance provider only", ["prefix"]=prefix };
WindowsNetworkProtection? guard=null;
Process? child=null;
try
{
    guard=new WindowsNetworkProtection(owner);
    guard.Remove();
    var filter=typeof(WindowsNetworkProtection).GetNestedType("Filter",System.Reflection.BindingFlags.NonPublic)!;
    if(Marshal.SizeOf(filter)!=200) throw new Exception("FWPM_FILTER0 ABI mismatch");
    guard.EnsureBlocked(prefix,localIp);
    report["baseline"]=guard.Inspect();
    if(!guard.Installed || guard.Allowing) throw new Exception("WFP baseline was not installed");
    using (var holder = Process.Start(Self("--job-holder"))!)
    {
        try
        {
        var pid = int.Parse((await holder.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)))!);
        using var owned = Process.GetProcessById(pid);
        holder.Kill(); await holder.WaitForExitAsync();
        await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        report["jobCrashTerminatesOwnedChild"] = true;
        }
        finally {if(!holder.HasExited){holder.Kill(true);await holder.WaitForExitAsync();}}
    }
    if(args.FirstOrDefault()=="--live")
    {
        var network=WindowsNetworkEnvironment.Read(profile,null);
        report["network"]=network;
        if(!network.VirtualReady) throw new Exception("Expected virtual network is not available");
        if(!await Ping(localIp)) throw new Exception("Protection blocked the device's own virtual IP");
        report["localVirtualIpPreserved"]=true;
        if(await Ping(serverIp)) throw new Exception("Persistent block did not block virtual IP traffic");
        report["persistentDenial"]=true;
        guard.Allow(network.VirtualLuid!.Value);
        if(!await Ping(serverIp)) throw new Exception("Interface-bound permit did not restore communication");
        report["interfaceBoundPermit"]=true;
        guard.Deny();
        var executable=Environment.ProcessPath ?? throw new Exception("No process path");
        var start=new ProcessStartInfo(executable) {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        if(Path.GetFileNameWithoutExtension(executable).Equals("dotnet",StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory,"EqLink.WindowsTests.dll"));
        foreach(var a in new[]{"--hold",prefix,localIp})start.ArgumentList.Add(a);
        child=Process.Start(start) ?? throw new Exception("Could not create WFP session owner");
        var ready=await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        if(ready!="READY" || !await Ping(serverIp)) throw new Exception("Child dynamic permit was not established");
        child.Kill(true);await child.WaitForExitAsync();
        await Task.Delay(1500);
        if(await Ping(serverIp)) throw new Exception("Dynamic allow survived owner crash");
        report["ownerCrashRemovesPermit"]=true;
    }
    report["result"]="passed";
}
catch(Exception ex){report["result"]=ex is System.ComponentModel.Win32Exception?"unavailable":"failed";report["error"]=ex.Message;Environment.ExitCode=1;}
finally
{
    if(child is not null){if(!child.HasExited)child.Kill(true);await child.WaitForExitAsync();child.Dispose();}
    try {guard?.Remove();report["cleanup"]=guard?.Inspect();guard?.Dispose();}
    catch(Exception ex){report["cleanupError"]=ex.Message;Environment.ExitCode=1;}
}
var output=JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true});
Console.WriteLine(output);
if(args.Length>3)File.WriteAllText(args[3],output);
static async Task<bool> Ping(string ip){using var p=new Ping();try{return(await p.SendPingAsync(ip,1000)).Status==IPStatus.Success;}catch(PingException){return false;}}
static ProcessStartInfo Self(string argument)
{
    var executable = Environment.ProcessPath!;
    var start = new ProcessStartInfo(executable) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true };
    if(Path.GetFileNameWithoutExtension(executable).Equals("dotnet",StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory,"EqLink.WindowsTests.dll"));
    start.ArgumentList.Add(argument);
    return start;
}
