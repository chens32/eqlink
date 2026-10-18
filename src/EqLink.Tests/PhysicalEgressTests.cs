using System.Net.NetworkInformation;
using EqLink.Core;

namespace EqLink.Tests;

internal static partial class Program
{
    private static async Task PhysicalEgressGuards(DeviceProfile profile)
    {
        var work = Path.Combine(Path.GetTempPath(), "eqlink-egress-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var settings = Path.Combine(work, "egress.json");
        try
        {
            Check(await PhysicalEgress.FromFileAsync(settings, profile) is null, "Missing optional egress settings changed default routing.");
            File.WriteAllText(settings, ConfigRenderer.Serialize(new PhysicalEgress.Settings("missing-interface", ["223.5.5.5"])));
            try
            {
                await using var unexpected = await PhysicalEgress.FromFileAsync(settings, profile);
                throw new Exception("Missing selected interface silently fell back to system routing.");
            }
            catch (InvalidOperationException) { }
            var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up &&
                n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211);
            if (nic is null) throw new Exception("No Ethernet/Wi-Fi interface is available for the Windows egress guard test.");
            File.WriteAllText(settings, ConfigRenderer.Serialize(new PhysicalEgress.Settings(nic.Id, ["223.5.5.5"])));
            try
            {
                await using var unexpected = await PhysicalEgress.FromFileAsync(settings, profile with { PublicHost = "198.18.7.1" });
                throw new Exception("Physical egress accepted a fake IP target.");
            }
            catch (IOException) { }
            await using var egress = await PhysicalEgress.FromFileAsync(settings, profile with { PublicHost = "203.0.113.25" })
                ?? throw new Exception("Valid explicit egress was ignored.");
            var runtime = egress.RuntimeProfile;
            Check(runtime.PublicHost == "127.0.0.1" && runtime.HysteriaServerName == profile.HysteriaServerName &&
                runtime.RealityServerName == profile.RealityServerName && runtime.OpenVpnPort == profile.OpenVpnPort,
                "Physical egress changed the TLS identity or inner OpenVPN target.");
            Console.WriteLine("Physical egress interface/fake-IP/identity guards: passed");
        }
        finally
        {
            File.Delete(settings);
            Directory.Delete(work);
        }
    }
}
