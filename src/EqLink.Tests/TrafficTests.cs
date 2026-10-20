using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using EqLink.Core;

namespace EqLink.Tests;

internal static partial class Program
{
    private static async Task TrafficCounters(DeviceProfile fixture)
    {
        using var listener = new TcpListener(IPAddress.Parse("127.77.88.2"),0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var clients = new List<TcpClient>(); var accepted = new List<TcpClient>();
        try
        {
            for (var i = 0; i < 3; i++)
            {
                var client = new TcpClient(new IPEndPoint(IPAddress.Parse("127.77.88.1"),0));
                await client.ConnectAsync(IPAddress.Parse("127.77.88.2"),port); clients.Add(client); accepted.Add(await listener.AcceptTcpClientAsync());
            }
            Check(WindowsTrafficSampler.TcpCount("127.77.88.1","127.77.88.0/24",new HashSet<int>()) == 3,"TCP count is not 3");
            Check(WindowsTrafficSampler.TcpCount("127.77.88.1","127.77.88.0/24",new HashSet<int>{Environment.ProcessId}) == 0,"Owned control process was counted");
            clients[0].Dispose(); accepted[0].Dispose();
            Check(WindowsTrafficSampler.TcpCount("127.77.88.1","127.77.88.0/24",new HashSet<int>()) == 2,"TCP count is not 2");
            foreach(var client in clients) client.Dispose(); foreach(var client in accepted) client.Dispose();
            Check(WindowsTrafficSampler.TcpCount("127.77.88.1","127.77.88.0/24",new HashSet<int>()) == 0,"Closing connections were counted");
            var nic = NetworkInterface.GetAllNetworkInterfaces().First(n=>n.OperationalStatus==OperationalStatus.Up && n.GetIPProperties().GetIPv4Properties() is not null);
            var sampler = new WindowsTrafficSampler();
            var first = sampler.Read(true,nic.Id,fixture,new HashSet<int>());
            Check(first.UploadBytesPerSecond is null && first.Error == "WAITING_FOR_SAMPLE", "GetIfEntry2 failed or first sample masked as zero: " + first.Error);
            await Task.Delay(1050);
            var second = sampler.Read(true,nic.Id,fixture,new HashSet<int>());
            Check(second.UploadBytesPerSecond is >= 0 && second.DownloadBytesPerSecond is >= 0 && second.Error is null,"Native interface counters unavailable: " + second.Error);
            Check(sampler.Read(false,nic.Id,fixture,new HashSet<int>()).UploadBytesPerSecond==0,"Disconnected rate was not zero");
            Console.WriteLine("Windows native TCP table 3→2→0, PID exclusion and GetIfEntry2 counters: passed (isolated loopback/NIC)");
        }
        finally { foreach(var client in clients) client.Dispose(); foreach(var client in accepted) client.Dispose(); }
    }
}
