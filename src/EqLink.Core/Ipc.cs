namespace EqLink.Core;

public sealed record ClientStatus(
    bool Imported,
    bool Connected,
    string DeviceName,
    string VirtualIp,
    TransportMode Mode,
    ActivePath Path,
    string Message,
    string Egress = "系统路由",
    string Phase = "Disconnected",
    string? ErrorCode = null,
    ProtectionStatus? Protection = null,
    TrafficSnapshot? Metrics = null,
    string Registration = "Pending",
    AddressPreference? Address = null,
    string? Server = null,
    string? HealthWarning = null,
    HostsOverrideStatus? Hosts = null);

public sealed record TrafficSnapshot(int? ActiveTcpConnections, double? UploadBytesPerSecond, double? DownloadBytesPerSecond,
    DateTimeOffset SampledAt, string? Error = null);

public sealed record IpcRequest(string Command, string? Mode = null, string? BundleBase64 = null,
    PhysicalEgress.Settings? Egress = null, bool? ProtectionEnabled = null, AddressPreference? Address = null, string? ProbePath = null,
    HostsOverrideSettings? Hosts = null);
public sealed record IpcResponse(bool Ok, string? Error, ClientStatus Status, EgressConfiguration? Egress = null, object? Diagnostics = null,
    AddressPreference? Address = null, HealthProbeResult? Probe = null, HostsOverrideSettings? Hosts = null);
public sealed record HealthProbeResult(bool Success, string? ErrorCode, DateTimeOffset SampledAt);
