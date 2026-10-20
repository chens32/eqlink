using System.Text;
using EqLink.Core;

namespace EqLink.Tests;

internal static partial class Program
{
    private static async Task HostsOverrides()
    {
        const string cidr = "10.88.0.0/24";
        var settings = HostsOverrideSettings.Parse(true, "# local only\n10.88.0.2 RANCHER.Example.test. alias.example.test # aliases\n10.88.0.1 服务.example.test", cidr);
        Check(settings.Entries.Length == 3 && settings.Entries.Any(e => e.Hostname == "rancher.example.test"), "Hosts aliases/case/trailing dot parsing");
        Check(settings.Entries.Any(e => e.Hostname.StartsWith("xn--", StringComparison.Ordinal)), "Hosts IDN normalization");
        foreach (var bad in new[] { "10.88.0.2 a.example.test\n10.88.0.3 A.example.test", "192.168.1.2 a.example.test", "10.88.0.0 a.example.test", "10.88.0.255 a.example.test", "10.88.0.02 a.example.test", "::1 a.example.test", "10.88.0.2 *.example.test", "10.88.0.2 https://a.example.test", "10.88.0.2 a.local", "10.88.0.2 no-dot", "10.88.0.2" })
        {
            try { HostsOverrideSettings.Parse(true, bad, cidr); throw new Exception("Invalid hosts mapping accepted: " + bad); } catch (ArgumentException) { }
        }
        var root = Path.Combine(Path.GetTempPath(), "eqlink-hosts-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var network = Guid.NewGuid().ToString(); HostsOverrideStore.Save(root, network, settings);
            Check(HostsOverrideStore.Load(root, network, cidr).Entries.Length == 3, "Hosts persistence");
            Check(!HostsOverrideStore.Load(root, Guid.NewGuid().ToString(), cidr).Enabled, "Hosts network isolation");
            var path = Path.Combine(root, "system-hosts-fixture");
            foreach (var original in new[] { Encoding.ASCII.GetBytes("# Original\r\n127.0.0.1 localhost\r\n"), Encoding.ASCII.GetBytes("# Original\n127.0.0.1 localhost"), new byte[] { 0xef, 0xbb, 0xbf }.Concat(Encoding.UTF8.GetBytes("# 中文注释\n127.0.0.1 localhost\n")).ToArray(), new byte[] { 35, 32, 0xd6, 0xd0, 0xce, 0xc4, 13, 10 }, Array.Empty<byte>() })
            {
                File.WriteAllBytes(path, original);
                var policy = new WindowsHostsFile(path, Guid.NewGuid());
                await policy.ApplyAsync(settings.Entries); var enabled = File.ReadAllBytes(path);
                await policy.ApplyAsync(settings.Entries);
                Check(File.ReadAllBytes(path).SequenceEqual(enabled), "Hosts apply is idempotent");
                await policy.ApplyAsync([]);
                Check(File.ReadAllBytes(path).SequenceEqual(original), "Hosts cleanup preserves original bytes and encoding");
            }
            var collision = Encoding.ASCII.GetBytes("192.168.1.2 rancher.example.test # foreign\r\n");
            File.WriteAllBytes(path, collision); var managed = new WindowsHostsFile(path, Guid.NewGuid());
            try { await managed.ApplyAsync(settings.Entries); throw new Exception("Foreign hosts conflict accepted"); } catch (IOException ex) when (ex.Message == "HOSTS_FILE_CONFLICT") { }
            Check(File.ReadAllBytes(path).SequenceEqual(collision), "Conflicting foreign entry is preserved without modification");
            var same = Encoding.ASCII.GetBytes("10.88.0.2 rancher.example.test # foreign\r\n"); File.WriteAllBytes(path, same);
            await managed.ApplyAsync(settings.Entries);
            Check(managed.ExistingMatches == 1, "Matching foreign entry is reported");
            await managed.ApplyAsync([]); Check(File.ReadAllBytes(path).SequenceEqual(same), "Matching foreign entry survives disable");
            var owner = Guid.NewGuid(); var malformed = Encoding.ASCII.GetBytes("# BEGIN EQLINK HOSTS " + owner + "\r\n10.88.0.2 orphan.example.test\r\n");
            File.WriteAllBytes(path, malformed);
            try { await new WindowsHostsFile(path, owner).ApplyAsync([]); throw new Exception("Malformed owned block accepted"); } catch (IOException ex) when (ex.Message == "HOSTS_FILE_MARKERS_INVALID") { }
            Check(File.ReadAllBytes(path).SequenceEqual(malformed), "Malformed block never truncates foreign content");
            if (OperatingSystem.IsWindows())
            {
                File.WriteAllText(path, "# Temporarily held by a reader\r\n");
                var sharing = new WindowsHostsFile(path, Guid.NewGuid());
                Task applying;
                using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    applying = sharing.ApplyAsync(settings.Entries);
                    Check(!applying.IsCompleted, "Concurrent reader delays writing without overwriting it");
                    await Task.Delay(300);
                }
                await applying.WaitAsync(TimeSpan.FromSeconds(2));
                await sharing.ApplyAsync([]);
                Check(File.ReadAllText(path) == "# Temporarily held by a reader\r\n", "Transient hosts lock is retried and original content remains intact");
            }
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine("Hosts parsing, validation, network storage, byte preservation, conflict and cleanup: passed");
    }
}
