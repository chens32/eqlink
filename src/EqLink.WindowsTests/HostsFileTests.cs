using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using EqLink.Core;

internal static class HostsFileTests
{
    public static async Task<int> RunAsync(string output)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");
        var original = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        var report = new Dictionary<string, object?> { ["timestamp"] = DateTimeOffset.UtcNow, ["scope"] = "Owned hosts fixture; Windows system resolver; original file restored", ["originalSha256"] = original };
        var name = "eqlink-" + Guid.NewGuid().ToString("N") + ".example.test";
        var policy = new WindowsHostsFile(owner: Guid.NewGuid());
        var passed = false;
        try
        {
            await policy.ApplyAsync([new(name, "10.88.0.2")]);
            var first = (await Dns.GetHostAddressesAsync(name).WaitAsync(TimeSpan.FromSeconds(5))).Select(a => a.ToString()).ToArray();
            report["firstResolution"] = first;
            if (first.Length != 1 || first[0] != "10.88.0.2") throw new IOException("HOSTS_RESOLUTION_MISMATCH");
            await policy.ApplyAsync([new(name, "10.88.0.3")]);
            var second = (await Dns.GetHostAddressesAsync(name).WaitAsync(TimeSpan.FromSeconds(5))).Select(a => a.ToString()).ToArray();
            report["updatedResolution"] = second;
            if (second.Length != 1 || second[0] != "10.88.0.3") throw new IOException("HOSTS_UPDATE_MISMATCH");
            passed = true;
        }
        catch (Exception error) { report["errorType"] = error.GetType().Name; report["error"] = error.Message.StartsWith("HOSTS_", StringComparison.Ordinal) ? error.Message : "HOSTS_TEST_FAILED"; }
        finally
        {
            try { await policy.ApplyAsync([]); }
            catch (Exception error) { report["cleanupErrorType"] = error.GetType().Name; passed = false; }
            var restored = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            report["restoredSha256"] = restored;
            report["originalBytesRestored"] = restored == original;
            passed &= restored == original;
            report["result"] = passed ? "passed" : "failed";
            File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        return passed ? 0 : 1;
    }
}
