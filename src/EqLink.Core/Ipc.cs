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
    ProtectionStatus? Protection = null);

public sealed record IpcRequest(string Command, string? Mode = null, string? BundleBase64 = null, string? Passphrase = null,
    PhysicalEgress.Settings? Egress = null, bool? ProtectionEnabled = null);
public sealed record IpcResponse(bool Ok, string? Error, ClientStatus Status, EgressConfiguration? Egress = null, object? Diagnostics = null);
