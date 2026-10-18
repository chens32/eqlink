using System.Text.Json;
using EqLink.Core;

namespace EqLink.Tests;

internal static partial class Program
{
    private static void EgressSettings()
    {
        var work = Path.Combine(Path.GetTempPath(), "eqlink-egress-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var path = Path.Combine(work, "egress.json");
        try
        {
            Check(EgressSettingsStore.Load(path) is null, "A new client did not default to system routing.");
            EgressSettingsStore.Save(path, new PhysicalEgress.Settings(" test-interface ", ["223.5.5.5", "223.5.5.5", "192.168.1.1"]));
            var restored = EgressSettingsStore.Load(path)!;
            Check(restored.InterfaceId == "test-interface" && restored.DnsServers.SequenceEqual(new[] { "223.5.5.5", "192.168.1.1" }),
                "Egress settings did not preserve the adapter and normalize DNS addresses.");
            var previous = File.ReadAllBytes(path);
            foreach (var invalid in new PhysicalEgress.Settings[]
            {
                new("", ["223.5.5.5"]), new("test-interface", []), new("test-interface", ["::1"]),
                new("test-interface", ["invalid"]), new("test-interface", ["1.1.1.1", "2.2.2.2", "3.3.3.3", "4.4.4.4", "5.5.5.5"])
            })
            {
                try { EgressSettingsStore.Save(path, invalid); throw new Exception("Invalid egress settings were saved."); }
                catch (InvalidDataException) { }
                Check(File.ReadAllBytes(path).SequenceEqual(previous), "Rejected settings replaced the existing configuration.");
            }
            Check(Directory.GetFiles(work).Length == 1, "Saving settings left temporary files behind.");
            File.WriteAllText(path, "{broken");
            try { EgressSettingsStore.Load(path); throw new Exception("Corrupt settings silently reverted to system routing."); }
            catch (JsonException) { }
            File.WriteAllText(path, "null");
            try { EgressSettingsStore.Load(path); throw new Exception("Null settings silently reverted to system routing."); }
            catch (InvalidDataException) { }
            EgressSettingsStore.Save(path, null);
            Check(!File.Exists(path) && EgressSettingsStore.Load(path) is null, "Explicit system routing did not clear the binding.");
            var legacy = JsonSerializer.Deserialize<IpcRequest>("{\"command\":\"status\"}", new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Check(legacy is { Command: "status", Egress: null }, "Existing IPC requests lost compatibility.");
            Console.WriteLine("Egress settings validation, persistence, recovery and IPC compatibility: passed");
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(work);
        }
    }
}
