using System.Diagnostics;
using System.Net;
using System.Text;

namespace EqLink.Core;

/// <summary>Preserves all bytes outside an explicitly owned hosts block, including encoding and line endings.</summary>
public sealed class WindowsHostsFile(string? path = null, Guid? owner = null)
{
    public static readonly Guid Owner = new("720e9b36-40af-48e6-91a8-ed63dd39cf61");
    private readonly string _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");
    private readonly string _begin = "# BEGIN EQLINK HOSTS " + (owner ?? Owner).ToString("D");
    private readonly string _end = "# END EQLINK HOSTS " + (owner ?? Owner).ToString("D");
    public int ExistingMatches { get; private set; }

    public async Task ApplyAsync(IEnumerable<HostsOverrideEntry> entries)
    {
        var mappings = entries.ToArray();
        bool changed;
        for (var attempt = 0; ; attempt++)
        {
            try { changed = UpdateFile(mappings); break; }
            catch (IOException ex) when (ex.Message == "HOSTS_FILE_BUSY" && attempt < 40)
            { await Task.Delay(250); }
        }
        if (!changed || path is not null) return;
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ipconfig.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("/flushdns");
        using var process = Process.Start(start) ?? throw new IOException("HOSTS_CACHE_FLUSH_FAILED");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (TimeoutException) { process.Kill(true); await process.WaitForExitAsync(); throw new IOException("HOSTS_CACHE_FLUSH_FAILED"); }
        await Task.WhenAll(stdout, stderr);
        if (process.ExitCode != 0) throw new IOException("HOSTS_CACHE_FLUSH_FAILED");
    }

    private bool UpdateFile(HostsOverrideEntry[] mappings)
    {
        try
        {
            if (new FileInfo(_path).Length > 1_048_576) throw new IOException("HOSTS_FILE_TOO_LARGE");
            var initial = File.ReadAllBytes(_path);
            var preview = Render(initial, mappings, out var previewMatches);
            ExistingMatches = previewMatches;
            if (initial.AsSpan().SequenceEqual(preview)) return false;
            // An exclusive handle prevents a concurrent writer from being silently overwritten.
            using var file = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            if (file.Length > 1_048_576) throw new IOException("HOSTS_FILE_TOO_LARGE");
            var original = new byte[(int)file.Length]; file.ReadExactly(original);
            var updated = Render(original, mappings, out var existing);
            ExistingMatches = existing;
            if (original.AsSpan().SequenceEqual(updated)) return false;
            try { file.Position = 0; file.Write(updated); file.SetLength(updated.Length); file.Flush(true); }
            catch
            {
                try { file.Position = 0; file.Write(original); file.SetLength(original.Length); file.Flush(true); }
                catch { throw new IOException("HOSTS_FILE_ROLLBACK_FAILED"); }
                throw;
            }
            return true;
        }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33) { throw new IOException("HOSTS_FILE_BUSY"); }
        catch (IOException ex) when (ex.Message.StartsWith("HOSTS_", StringComparison.Ordinal)) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new IOException("HOSTS_FILE_WRITE_FAILED"); }
    }

    public byte[] Render(byte[] original, HostsOverrideEntry[] mappings, out int existingMatches)
    {
        if (original.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }) || original.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }) || original.AsSpan().StartsWith(new byte[] { 0, 0, 0xfe, 0xff }))
            throw new IOException("HOSTS_FILE_ENCODING_UNSUPPORTED");
        var bom = original.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
        var starts = new List<int>(); var ends = new List<int>();
        var cursor = bom;
        while (cursor < original.Length)
        {
            var next = Array.IndexOf(original, (byte)10, cursor); if (next < 0) next = original.Length; else next++;
            var line = Encoding.Latin1.GetString(original, cursor, next - cursor).TrimEnd('\r', '\n');
            if (line.StartsWith(_begin, StringComparison.Ordinal)) { if (line != _begin) throw new IOException("HOSTS_FILE_MARKERS_INVALID"); starts.Add(cursor); }
            if (line.StartsWith(_end, StringComparison.Ordinal)) { if (line != _end) throw new IOException("HOSTS_FILE_MARKERS_INVALID"); ends.Add(next); }
            cursor = next;
        }
        if (starts.Count != ends.Count || starts.Count > 1 || starts.Count == 1 && starts[0] >= ends[0]) throw new IOException("HOSTS_FILE_MARKERS_INVALID");
        var outside = starts.Count == 0 ? original : original[..starts[0]].Concat(original[ends[0]..]).ToArray();
        var desired = mappings.ToDictionary(e => e.Hostname, e => e.Address, StringComparer.OrdinalIgnoreCase);
        existingMatches = 0;
        foreach (var rawLine in Encoding.Latin1.GetString(outside[bom..]).Split('\n'))
        {
            var fields = rawLine.Split('#', 2)[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2 || !IPAddress.TryParse(fields[0], out var ip)) continue;
            foreach (var name in fields.Skip(1))
            {
                string normalized; try { normalized = HostsOverrideSettings.NormalizeHostname(name); } catch (ArgumentException) { continue; }
                if (!desired.TryGetValue(normalized, out var requested)) continue;
                if (ip.ToString() != requested) throw new IOException("HOSTS_FILE_CONFLICT");
                existingMatches++;
            }
        }
        if (mappings.Length == 0) return outside;
        var lf = Array.IndexOf(outside, (byte)10); var newline = lf > 0 && outside[lf - 1] == 13 || lf < 0 ? "\r\n" : "\n";
        var block = Encoding.ASCII.GetBytes(_begin + newline + string.Join(newline, mappings.Select(e => e.Address + " " + e.Hostname)) + newline + _end + newline);
        if (outside.Length + block.Length > 1_048_576) throw new IOException("HOSTS_FILE_TOO_LARGE");
        return outside[..bom].Concat(block).Concat(outside[bom..]).ToArray();
    }
}
