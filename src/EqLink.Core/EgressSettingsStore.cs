using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace EqLink.Core;

public sealed record EgressInterface(string Id, string Name, string Description, string[] Ipv4Addresses, bool IsUp);
public sealed record EgressConfiguration(PhysicalEgress.Settings? Settings, EgressInterface[] Interfaces, bool Locked, string? Error = null);

/// <summary>Local configuration only. Saving never resolves the server or opens an outer connection.</summary>
public static class EgressSettingsStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static PhysicalEgress.Settings Validate(PhysicalEgress.Settings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.InterfaceId) || settings.InterfaceId.Length > 256 ||
            settings.DnsServers is not { Length: > 0 and <= 4 } ||
            settings.DnsServers.Any(s => !IPAddress.TryParse(s, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork))
            throw new InvalidDataException("指定出口需要网卡 ID 和 1 至 4 个 IPv4 DNS 地址。");
        return new PhysicalEgress.Settings(settings.InterfaceId.Trim(), settings.DnsServers.Select(s => IPAddress.Parse(s).ToString()).Distinct().ToArray());
    }

    public static PhysicalEgress.Settings? Load(string path)
    {
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 16_384) throw new InvalidDataException("出口配置文件过大，请重新保存出口设置。");
        return Validate(JsonSerializer.Deserialize<PhysicalEgress.Settings>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("出口配置为空，请重新保存出口设置。"));
    }

    public static void Save(string path, PhysicalEgress.Settings? settings)
    {
        if (settings is null) { File.Delete(path); return; }
        var normalized = Validate(settings);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(normalized, Json));
            File.Move(temp, path, true);
        }
        finally { File.Delete(temp); }
    }

    public static EgressInterface[] Interfaces()
    {
        var result = new List<EgressInterface>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)) continue;
            try
            {
                var addresses = nic.GetIPProperties().UnicastAddresses
                    .Select(a => a.Address)
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) &&
                        !a.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                    .Select(a => a.ToString()).ToArray();
                result.Add(new EgressInterface(nic.Id, nic.Name, nic.Description, addresses, nic.OperationalStatus == OperationalStatus.Up));
            }
            catch (NetworkInformationException) { /* An adapter may disappear while the list is being refreshed. */ }
        }
        return result.OrderByDescending(n => n.IsUp).ThenBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
}
