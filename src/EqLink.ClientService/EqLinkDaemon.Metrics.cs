using EqLink.Core;

namespace EqLink.ClientService;

internal sealed partial class EqLinkDaemon
{
    private TrafficSnapshot _metrics = new(0, 0, 0, DateTimeOffset.UtcNow);
    private Task? _metricsLoop;
    private async Task MetricsLoopAsync(CancellationToken stop)
    {
        var sampler = new WindowsTrafficSampler();
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var excluded = new HashSet<int> { Environment.ProcessId };
                foreach (var process in new[] { _hysteria, _xray, _openVpn })
                    try { if (process is not null) excluded.Add(process.Id); } catch (InvalidOperationException) { }
                var snapshot = sampler.Read(_vpnConnected, _virtualInterfaceId, _profile, excluded);
                lock (_gate) _metrics = snapshot;
                await Task.Delay(1000, stop);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
}
