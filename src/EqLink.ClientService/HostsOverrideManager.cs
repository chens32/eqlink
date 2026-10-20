using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using EqLink.Core;

namespace EqLink.ClientService;

internal sealed class HostsOverrideManager(string root, WindowsHostsFile? policy = null) : IAsyncDisposable
{
    private readonly WindowsHostsFile _policy = policy ?? new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _networkId, _cidr, _error;
    private bool _applied, _policyMayExist = true;
    public HostsOverrideSettings Settings { get; private set; } = HostsOverrideSettings.Empty;
    public HostsOverrideStatus Status => new(Settings.Enabled, _applied, Settings.Entries.Length, _error, _policy.ExistingMatches);

    public async Task SetNetworkAsync(string networkId, string cidr)
    {
        await _gate.WaitAsync();
        try {
        if (_networkId == networkId && _cidr == cidr) return;
        try
        {
            await DeactivateCoreAsync();
            _networkId = networkId; _cidr = cidr; _error = null; Settings = HostsOverrideSettings.Empty;
            Settings = HostsOverrideStore.Load(root, networkId, cidr);
            if (File.Exists(HostsOverrideStore.FilePath(root, networkId))) { _policyMayExist = true; await ApplyAsync(Settings); }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or JsonException or SocketException)
        { _error = Code(ex); }
        } finally { _gate.Release(); }
    }
    public async Task SaveAsync(HostsOverrideSettings requested)
    {
        await _gate.WaitAsync();
        try {
        if (_networkId is null || _cidr is null) throw new InvalidOperationException("请先导入 EqLink 通用包。");
        var next = requested.Validate(_cidr);
        if (next.Revision != Settings.Revision) throw new InvalidOperationException("域名覆写已被其他窗口修改，请重新读取后再保存。");
        next = next with { Revision = checked(Settings.Revision + 1) };
        var previous = Settings;
        try
        {
            await ApplyAsync(next);
            HostsOverrideStore.Save(root, _networkId, next);
            Settings = next; _error = null;
        }
        catch (Exception error)
        {
            try { await ApplyAsync(previous); }
            catch { _error = "HOSTS_FILE_ROLLBACK_FAILED"; }
            throw new IOException(_error ?? Code(error));
        }
        } finally { _gate.Release(); }
    }
    private async Task ApplyAsync(HostsOverrideSettings settings)
    {
        if (!settings.Enabled || settings.Entries.Length == 0) { await DeactivateCoreAsync(); return; }
        _policyMayExist = true;
        await _policy.ApplyAsync(settings.Entries);
        _applied = true;
    }
    public async Task DeactivateAsync()
    {
        await _gate.WaitAsync();
        try { await DeactivateCoreAsync(); } finally { _gate.Release(); }
    }
    private async Task DeactivateCoreAsync()
    {
        if (_policyMayExist) { await _policy.ApplyAsync([]); _applied = false; _policyMayExist = false; }
    }
    private static string Code(Exception error) => error is SocketException ? "HOSTS_FILE_WRITE_FAILED" :
        error.Message.StartsWith("HOSTS_", StringComparison.Ordinal) ? error.Message : "HOSTS_CONFIG_INVALID";
    public async ValueTask DisposeAsync() => await DeactivateAsync();
}
