using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

internal static class TrafficProbe
{
    internal static async Task RunAsync(string command, Dictionary<string,string> options, IPAddress bind, int port)
    {
        var seconds = int.Parse(options.GetValueOrDefault("--seconds","30"));
        if (seconds is < 1 or > 3600) throw new ArgumentException("--seconds must be 1..3600");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        if (command == "stream-serve")
        {
            using var listener = new TcpListener(bind,port); listener.Start();
            Console.WriteLine($"Traffic listener on {bind}:{port}; bounded to {seconds}s. Supports hold and verified directional transfer.");
            var active = new List<Task>();
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(stop.Token); active.RemoveAll(t=>t.IsCompleted);
                    if(active.Count>=16) {client.Dispose();continue;}
                    active.Add(HandleAsync(client,stop.Token));
                }
            }
            catch(OperationCanceledException) when(stop.IsCancellationRequested) { }
            finally { await Task.WhenAll(active); }
            return;
        }
        var peer = IPAddress.Parse(options["--peer"]);
        if (peer.AddressFamily != AddressFamily.InterNetwork || peer.Equals(bind) || !peer.GetAddressBytes().AsSpan(0,3).SequenceEqual(bind.GetAddressBytes().AsSpan(0,3))) throw new ArgumentException("Peer must be another address in the same virtual /24");
        if (command == "hold")
        {
            var connections = int.Parse(options.GetValueOrDefault("--connections","3"));
            if(connections is <1 or >10) throw new ArgumentException("--connections must be 1..10");
            var clients = new List<TcpClient>();
            try
            {
                for(var i=0;i<connections;i++) { var c=new TcpClient(new IPEndPoint(bind,0));clients.Add(c);await c.ConnectAsync(peer,port,stop.Token);await c.GetStream().WriteAsync(new byte[]{(byte)'H'},stop.Token); }
                Console.WriteLine($"Holding {connections} established connections for {seconds}s. At one-third time closes one; at two-thirds closes all (3→2→0 with default count).");
                await Task.Delay(TimeSpan.FromSeconds(seconds/3d),stop.Token); clients[0].Dispose(); Console.WriteLine("One connection closed.");
                await Task.Delay(TimeSpan.FromSeconds(seconds/3d),stop.Token); foreach(var c in clients)c.Dispose(); Console.WriteLine("All connections closed.");
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1,seconds/3d-0.5)),stop.Token);
            }
            catch(OperationCanceledException) when(stop.IsCancellationRequested) { }
            finally { foreach(var c in clients)c.Dispose(); }
            return;
        }
        var direction=options.GetValueOrDefault("--direction","upload");
        if(direction is not("upload" or "download")) throw new ArgumentException("Direction must be upload or download");
        var bytes=long.Parse(options.GetValueOrDefault("--bytes","33554432")); ValidateLength(bytes);
        using var clientSocket=new TcpClient(new IPEndPoint(bind,0)); await clientSocket.ConnectAsync(peer,port,stop.Token);
        var stream=clientSocket.GetStream(); var header=new byte[9];header[0]=(byte)(direction=="upload"?'U':'D');BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(1),bytes);
        await stream.WriteAsync(header,stop.Token); var watch=Stopwatch.StartNew();
        byte[] hash;
        if(direction=="upload") hash=await SendAsync(stream,bytes,stop.Token); else hash=await ReceiveAsync(stream,bytes,stop.Token);
        var expected=new byte[32];await stream.ReadExactlyAsync(expected,stop.Token);
        var ok=CryptographicOperations.FixedTimeEquals(hash,expected);
        var report=new {schemaVersion=1,version=typeof(TrafficProbe).Assembly.GetName().Version?.ToString(3),timestamp=DateTimeOffset.UtcNow,result=ok?"passed":"failed",bind=bind.ToString(),peer=peer.ToString(),direction,bytes,elapsedSeconds=watch.Elapsed.TotalSeconds,payloadBytesPerSecond=bytes/watch.Elapsed.TotalSeconds,sha256=Convert.ToHexString(hash)};
        var json=JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true});Console.WriteLine(json);
        if(options.TryGetValue("--output",out var output))File.WriteAllText(output,json);
        Environment.ExitCode=ok?0:1;
    }
    private static void ValidateLength(long length) {if(length is <65536 or >536870912)throw new ArgumentException("Transfer must be 64 KiB..512 MiB");}
    private static async Task HandleAsync(TcpClient client,CancellationToken stop)
    {
        using(client)
        using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop))
        {
            deadline.CancelAfter(TimeSpan.FromMinutes(10)); var token=deadline.Token;
            try
            {
                var stream=client.GetStream();var command=new byte[1];await stream.ReadExactlyAsync(command,token);
                if(command[0]==(byte)'H') {var one=new byte[1];while(await stream.ReadAsync(one,token)>0){}return;}
                var length=new byte[8];await stream.ReadExactlyAsync(length,token);var bytes=BinaryPrimitives.ReadInt64BigEndian(length);ValidateLength(bytes);
                var hash=command[0] switch { (byte)'U'=>await ReceiveAsync(stream,bytes,token),(byte)'D'=>await SendAsync(stream,bytes,token),_=>throw new IOException("Invalid stream operation")};
                await stream.WriteAsync(hash,token);
            }
            catch(Exception ex) when(ex is IOException or SocketException or OperationCanceledException or ArgumentException) { }
        }
    }
    private static async Task<byte[]> SendAsync(Stream stream,long bytes,CancellationToken stop)
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var block=RandomNumberGenerator.GetBytes(65536);
        while(bytes>0) {var count=(int)Math.Min(block.Length,bytes);hash.AppendData(block,0,count);await stream.WriteAsync(block.AsMemory(0,count),stop);bytes-=count;}
        return hash.GetHashAndReset();
    }
    private static async Task<byte[]> ReceiveAsync(Stream stream,long bytes,CancellationToken stop)
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var block=new byte[65536];
        while(bytes>0) {var count=await stream.ReadAsync(block.AsMemory(0,(int)Math.Min(block.Length,bytes)),stop);if(count==0)throw new IOException("Transfer closed early");hash.AppendData(block,0,count);bytes-=count;}
        return hash.GetHashAndReset();
    }
}
