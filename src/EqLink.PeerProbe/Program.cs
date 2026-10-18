using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

// A bounded payload echo tool. Bind only to an assigned EqLink virtual IPv4 address.
var options = new Dictionary<string,string>();
try
{
    if (args.Length<3 || args.Length%2!=1 || args[0] is not ("serve" or "check")) throw new ArgumentException("Usage: serve|check --bind VIRTUAL_IP [--peer OTHER_VIRTUAL_IP] [--port 36555] [--seconds 120] [--repeat 5] [--output report.json]");
    for(var i=1;i<args.Length;i+=2) if(!options.TryAdd(args[i],args[i+1]))throw new ArgumentException("Duplicate option");
    if(options.Keys.Except(new[]{"--bind","--peer","--port","--seconds","--repeat","--output"}).Any())throw new ArgumentException("Unknown option");
    var bind=IPAddress.Parse(options["--bind"]);
    var b=bind.GetAddressBytes();
    if(bind.AddressFamily!=AddressFamily.InterNetwork || !(b[0]==10 || b[0]==172 && b[1]>=16 && b[1]<=31 || b[0]==192 && b[1]==168) || b[3] is 0 or 255 ||
       !NetworkInterface.GetAllNetworkInterfaces().Any(n=>n.GetIPProperties().UnicastAddresses.Any(a=>a.Address.Equals(bind))))throw new ArgumentException("--bind must be an assigned private virtual IPv4 address");
    var port=int.Parse(options.GetValueOrDefault("--port","36555"));
    if(port is <1024 or >65535)throw new ArgumentException("Use a high port (1024..65535)");
    if(args[0]=="serve")
    {
        var seconds=int.Parse(options.GetValueOrDefault("--seconds","120"));
        if(seconds is <1 or >3600)throw new ArgumentException("--seconds must be 1..3600");
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;stop.Cancel();};
        var tcp=new TcpListener(bind,port);tcp.Start();using var udp=new UdpClient(new IPEndPoint(bind,port));
        Console.WriteLine($"Echo listening only on {bind}:{port} TCP/UDP for {seconds}s. Run check from the other EqLink device.");
        try {await Task.WhenAll(TcpServer(tcp,stop.Token),UdpServer(udp,stop.Token));}
        catch(OperationCanceledException) when(stop.IsCancellationRequested) { }
        finally {tcp.Stop();}
        return;
    }
    var peer=IPAddress.Parse(options["--peer"]);
    if(peer.AddressFamily!=AddressFamily.InterNetwork || peer.Equals(bind) || !peer.GetAddressBytes().AsSpan(0,3).SequenceEqual(b.AsSpan(0,3)))throw new ArgumentException("--peer must be another virtual IP in the same /24");
    var repeat=int.Parse(options.GetValueOrDefault("--repeat","5"));
    if(repeat is <1 or >100)throw new ArgumentException("--repeat must be 1..100");
    var results=new List<object>();var success=true;
    for(var i=1;i<=repeat;i++)
    foreach(var protocol in new[]{"ICMP","TCP","UDP"})
    {
        var watch=Stopwatch.StartNew();var ok=false;string? error=null;
        try
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var payload=RandomNumberGenerator.GetBytes(64);
            if(protocol=="ICMP") {using var ping=new Ping();ok=(await ping.SendPingAsync(peer,TimeSpan.FromSeconds(2),payload,null,timeout.Token)).Status==IPStatus.Success;}
            else if(protocol=="TCP")
            {
                using var client=new TcpClient(new IPEndPoint(bind,0));
                await client.ConnectAsync(peer,port,timeout.Token);await client.GetStream().WriteAsync(payload,timeout.Token);
                var answer=new byte[payload.Length];await client.GetStream().ReadExactlyAsync(answer,timeout.Token);ok=answer.AsSpan().SequenceEqual(payload);
            }
            else
            {
                using var client=new UdpClient(new IPEndPoint(bind,0));client.Connect(peer,port);
                await client.SendAsync(payload,timeout.Token);var answer=await client.ReceiveAsync(timeout.Token);ok=answer.Buffer.AsSpan().SequenceEqual(payload);
            }
        }
        catch(Exception ex) when(ex is SocketException or IOException or OperationCanceledException or PingException) {error=ex.GetType().Name;}
        results.Add(new {iteration=i,protocol,result=ok?"passed":"failed",elapsedMs=Math.Round(watch.Elapsed.TotalMilliseconds,1),error});success&=ok;
    }
    var json=JsonSerializer.Serialize(new {schemaVersion=1,version="0.2.0",timestamp=DateTimeOffset.UtcNow,bind=bind.ToString(),peer=peer.ToString(),port,result=success?"passed":"failed",checks=results},new JsonSerializerOptions{WriteIndented=true});
    Console.WriteLine(json);if(options.TryGetValue("--output",out var output))File.WriteAllText(output,json);
    Environment.ExitCode=success?0:1;
}
catch(Exception ex) {Console.Error.WriteLine(ex is ArgumentException ? ex.Message : "Probe unavailable: "+ex.GetType().Name);Environment.ExitCode=2;}

static async Task TcpServer(TcpListener listener,CancellationToken stop)
{
    var pending=new List<Task>();
    try
    {
        while(!stop.IsCancellationRequested)
        {
            var client=await listener.AcceptTcpClientAsync(stop);
            pending.RemoveAll(t=>t.IsCompleted);
            if(pending.Count>=32){client.Dispose();continue;}
            pending.Add(Echo(client,stop));
        }
    }
    finally {await Task.WhenAll(pending);}
}
static async Task Echo(TcpClient client,CancellationToken stop)
{
    using(client)
    using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(stop))
    {
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try {var data=new byte[64];await client.GetStream().ReadExactlyAsync(data,timeout.Token);await client.GetStream().WriteAsync(data,timeout.Token);}
        catch(Exception ex) when(ex is IOException or OperationCanceledException or SocketException) { }
    }
}
static async Task UdpServer(UdpClient udp,CancellationToken stop)
{
    while(!stop.IsCancellationRequested)
    {
        var packet=await udp.ReceiveAsync(stop);
        if(packet.Buffer.Length<=1024)await udp.SendAsync(packet.Buffer,packet.RemoteEndPoint,stop);
    }
}
