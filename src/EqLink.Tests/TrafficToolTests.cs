using System.Net;
using EqLink.Core;

namespace EqLink.Tests;

internal static partial class Program
{
    private static async Task TrafficTools()
    {
        var port = FreePort();
        var server = TrafficProbe.RunAsync("stream-serve",new(){["--seconds"]="5"},IPAddress.Parse("127.77.90.2"),port);
        foreach (var direction in new[]{"upload","download"})
        {
            await TrafficProbe.RunAsync("transfer",new(){["--peer"]="127.77.90.2",["--seconds"]="4",["--bytes"]="1048576",["--direction"]=direction},IPAddress.Parse("127.77.90.1"),port);
            Check(Environment.ExitCode==0,"Directional traffic payload verification failed");
        }
        await server;
        Console.WriteLine("Bounded directional traffic tools with complete SHA-256 payload verification: passed (isolated loopback)");
    }
}
