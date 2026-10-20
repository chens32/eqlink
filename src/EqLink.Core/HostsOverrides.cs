using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace EqLink.Core;

public sealed record HostsOverrideEntry(string Hostname, string Address);
public sealed record HostsOverrideSettings(bool Enabled, HostsOverrideEntry[] Entries, long Revision = 0)
{
    public static HostsOverrideSettings Empty => new(false, []);
    public HostsOverrideSettings Validate(string cidr)
    {
        var network = new ServerSettings { PublicHost = "validation.invalid", OverlayCidr = cidr }.Overlay().Network;
        if (Entries is null || Entries.Length > 128 || Revision < 0) throw new ArgumentException("域名覆写最多支持 128 个域名，配置版本必须有效。");
        var entries = new SortedDictionary<string, HostsOverrideEntry>(StringComparer.Ordinal);
        foreach (var entry in Entries)
        {
            if (entry is null) throw new ArgumentException("域名覆写条目无效。");
            var name = NormalizeHostname(entry.Hostname);
            if (!IPAddress.TryParse(entry.Address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork || ip.ToString() != entry.Address ||
                !ip.GetAddressBytes().AsSpan(0, 3).SequenceEqual(IPAddress.Parse(network).GetAddressBytes().AsSpan(0, 3)) || ip.GetAddressBytes()[3] is 0 or 255)
                throw new ArgumentException("覆写目标必须是当前 EqLink 网段内的 IPv4 主机地址，不能使用网络地址或广播地址。");
            if (!entries.TryAdd(name, new(name, ip.ToString()))) throw new ArgumentException("同一域名只能配置一次：" + name);
        }
        return this with { Entries = entries.Values.ToArray() };
    }

    public static string NormalizeHostname(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 1024) throw new ArgumentException("请输入完整域名。");
        var name = new IdnMapping { UseStd3AsciiRules = true }.GetAscii(value.Trim().TrimEnd('.')).ToLowerInvariant();
        if (name.Length > 253 || !name.Contains('.') || IPAddress.TryParse(name, out _) || name.EndsWith(".localhost", StringComparison.Ordinal) ||
            name.Split('.').Any(label => label.Length is < 1 or > 63 || label[0] == '-' || label[^1] == '-' || label.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))))
            throw new ArgumentException("请使用完整域名；不支持通配符、URL、端口或 IP 作为域名。");
        if (name.EndsWith(".local", StringComparison.Ordinal)) throw new ArgumentException(".local 通常由 mDNS 解析，请为覆写使用完整的 .home.arpa 等域名。");
        return name;
    }

    public static HostsOverrideSettings Parse(bool enabled, string text, string cidr, long revision = 0)
    {
        if (text.Length > 65536) throw new ArgumentException("域名覆写文本过大。");
        var entries = new List<HostsOverrideEntry>();
        foreach (var line in text.Split('\n'))
        {
            var fields = line.Split('#', 2)[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 0) continue;
            if (fields.Length < 2) throw new ArgumentException("每行格式为：虚拟 IPv4 域名；可用 # 添加注释。");
            foreach (var name in fields.Skip(1)) { entries.Add(new(name, fields[0])); if (entries.Count > 128) throw new ArgumentException("域名覆写最多支持 128 个域名。"); }
        }
        return new HostsOverrideSettings(enabled, entries.ToArray(), revision).Validate(cidr);
    }
    public string ToHostsText() => string.Join(Environment.NewLine, Entries.Select(e => e.Address + " " + e.Hostname));
}

public sealed record HostsOverrideStatus(bool Enabled, bool Applied, int EntryCount, string? Error = null, int ExistingMatches = 0);

public static class HostsOverrideStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static string FilePath(string root, string networkId) => Path.Combine(root, "hosts", Guid.Parse(networkId).ToString("N") + ".json");
    public static HostsOverrideSettings Load(string root, string networkId, string cidr)
    {
        var path = FilePath(root, networkId);
        if (!File.Exists(path)) return HostsOverrideSettings.Empty;
        if (new FileInfo(path).Length > 65536) throw new InvalidDataException("域名覆写配置过大。");
        return (JsonSerializer.Deserialize<HostsOverrideSettings>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("域名覆写配置为空。")).Validate(cidr);
    }
    public static void Save(string root, string networkId, HostsOverrideSettings settings)
    {
        var path = FilePath(root, networkId); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Json)); File.Move(temporary, path, true); }
        finally { File.Delete(temporary); }
    }
}
