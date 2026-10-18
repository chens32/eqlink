using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace EqLink.Core;

public sealed record OverlayRoute(string Prefix, int PrefixLength, uint InterfaceIndex, string NextHop, uint Metric, bool Local);
public sealed record NetworkSnapshot(bool EgressReady, string EgressSignature, string? SourceIpv4, uint? EgressIndex,
    bool VirtualReady, uint? VirtualIndex, ulong? VirtualLuid, string? Conflict, OverlayRoute[] Routes);

/// <summary>Only reads IPv4 routes, except for an explicitly requested repair of a previously observed EqLink route.</summary>
public static class WindowsNetworkEnvironment
{
    public static NetworkSnapshot Read(DeviceProfile profile, PhysicalEgress.Settings? egress, bool repairOwnedRoute = false, string? virtualInterfaceId = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var all = NetworkInterface.GetAllNetworkInterfaces();
        var nic = egress is null ? null : all.FirstOrDefault(n => n.Id.Equals(egress.InterfaceId, StringComparison.OrdinalIgnoreCase));
        var source = nic?.OperationalStatus == OperationalStatus.Up ? UsableAddresses(nic).FirstOrDefault() : null;
        var index = source is null ? (uint?)null : (uint)nic!.GetIPProperties().GetIPv4Properties().Index;
        var ready = egress is null || source is not null;
        var signature = egress is null ? "system" : $"{egress.InterfaceId}|{index}|{source}";
        var tap = all.FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up && n.Description.Contains("TAP-Windows",StringComparison.OrdinalIgnoreCase) &&
            (virtualInterfaceId is null || n.Id.Equals(virtualInterfaceId,StringComparison.OrdinalIgnoreCase)) &&
            n.GetIPProperties().UnicastAddresses.Any(a => a.Address.ToString() == profile.VirtualIp && a.PrefixLength == 24));
        uint? tapIndex = tap is null ? null : (uint)tap.GetIPProperties().GetIPv4Properties().Index;
        ulong? luid = null;
        if (tap is not null) { var guid = Guid.Parse(tap.Id); Check(ConvertInterfaceGuidToLuid(ref guid, out var value)); luid = value; }
        var network = IPAddress.Parse(profile.OverlayCidr.Split('/')[0]);
        var rows = ReadRows();
        var relevant = rows.Where(r => r.Destination.Length >= 24 && InSubnet(Address(r.Destination.Address), network)).ToArray();
        var conflict = relevant.FirstOrDefault(r => r.Index != tapIndex && r.Loopback == 0);
        var conflictText = conflict.Index == 0 ? null : $"{Address(conflict.Destination.Address)}/{conflict.Destination.Length} 经接口 {conflict.Index}";
        var hasRoute = tapIndex is not null && relevant.Any(r => r.Index == tapIndex && r.Destination.Length == 24);
        if (repairOwnedRoute && !hasRoute && conflictText is null && tapIndex is not null && luid is not null)
        {
            var row = new RouteRow
            {
                Luid = luid.Value, Index = tapIndex.Value, Destination = new Prefix { Address = Sockaddr(network), Length = 24 },
                NextHop = Sockaddr(IPAddress.Parse(profile.ServerIp)), SiteLength = 24, ValidLifetime = uint.MaxValue,
                PreferredLifetime = uint.MaxValue, Metric = 0, Protocol = 3
            };
            var result = CreateIpForwardEntry2(ref row);
            if (result is not (0 or 183 or 5010)) Check(result);
            rows = ReadRows();
            relevant = rows.Where(r => r.Destination.Length >= 24 && InSubnet(Address(r.Destination.Address), network)).ToArray();
            hasRoute = relevant.Any(r => r.Index == tapIndex && r.Destination.Length == 24);
        }
        return new NetworkSnapshot(ready, signature, source?.ToString(), index, tap is not null && hasRoute && conflictText is null,
            tapIndex, luid, conflictText, relevant.Select(r => new OverlayRoute(Address(r.Destination.Address).ToString(), r.Destination.Length, r.Index, Address(r.NextHop).ToString(), r.Metric, r.Loopback != 0)).ToArray());
    }

    public static string? EgressSignature(PhysicalEgress.Settings? egress)
    {
        if (egress is null) return "system";
        var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Id.Equals(egress.InterfaceId, StringComparison.OrdinalIgnoreCase));
        var source = nic?.OperationalStatus == OperationalStatus.Up ? UsableAddresses(nic).FirstOrDefault() : null;
        return source is null ? null : $"{egress.InterfaceId}|{nic!.GetIPProperties().GetIPv4Properties().Index}|{source}";
    }

    private static IEnumerable<IPAddress> UsableAddresses(NetworkInterface nic) => nic.GetIPProperties().UnicastAddresses
        .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address) && !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
        .Select(a => a.Address).OrderBy(a => a.ToString(), StringComparer.Ordinal);
    private static bool InSubnet(IPAddress address, IPAddress network) => address.GetAddressBytes().AsSpan(0, 3).SequenceEqual(network.GetAddressBytes().AsSpan(0, 3));
    private static SockAddr Sockaddr(IPAddress address) { var b = address.GetAddressBytes(); return new SockAddr { Family = 2, Address = BitConverter.ToUInt32(b) }; }
    private static IPAddress Address(SockAddr value) => new(BitConverter.GetBytes(value.Address));
    private static RouteRow[] ReadRows()
    {
        Check(GetIpForwardTable2(2, out var table));
        try
        {
            var count = Marshal.ReadInt32(table);
            if (count is < 0 or > 100000) throw new IOException("Invalid Windows IPv4 route table.");
            var rows = new RouteRow[count]; var size = Marshal.SizeOf<RouteRow>();
            for (var i = 0; i < count; i++) rows[i] = Marshal.PtrToStructure<RouteRow>(table + 8 + i * size);
            return rows;
        }
        finally { FreeMibTable(table); }
    }
    private static void Check(uint code) { if (code != 0) throw new Win32Exception(unchecked((int)code)); }
    [StructLayout(LayoutKind.Explicit, Size = 28)] private struct SockAddr { [FieldOffset(0)] public ushort Family; [FieldOffset(4)] public uint Address; }
    [StructLayout(LayoutKind.Sequential)] private struct Prefix { public SockAddr Address; public byte Length; }
    [StructLayout(LayoutKind.Sequential)] private struct RouteRow
    {
        public ulong Luid; public uint Index; public Prefix Destination; public SockAddr NextHop; public byte SiteLength;
        public uint ValidLifetime, PreferredLifetime, Metric, Protocol; public byte Loopback, Autoconfigure, Publish, Immortal; public uint Age, Origin;
    }
    [DllImport("iphlpapi.dll")] private static extern uint ConvertInterfaceGuidToLuid(ref Guid guid, out ulong luid);
    [DllImport("iphlpapi.dll")] private static extern uint GetIpForwardTable2(ushort family, out IntPtr table);
    [DllImport("iphlpapi.dll")] private static extern void FreeMibTable(IntPtr table);
    [DllImport("iphlpapi.dll")] private static extern uint CreateIpForwardEntry2(ref RouteRow row);
}

public sealed class WindowsNetworkChanges : IDisposable
{
    private readonly Action _changed;
    private readonly RouteCallback _callback;
    private IntPtr _handle;
    public WindowsNetworkChanges(Action changed)
    {
        _changed = changed;
        _callback = (_, _, _) => Signal();
        NetworkChange.NetworkAddressChanged += AddressChanged;
        NetworkChange.NetworkAvailabilityChanged += AvailabilityChanged;
        if (OperatingSystem.IsWindows()) NotifyRouteChange2(2, _callback, IntPtr.Zero, false, out _handle);
    }
    private void Signal() { try { _changed(); } catch (ObjectDisposedException) { } }
    private void AddressChanged(object? sender, EventArgs e) => Signal();
    private void AvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => Signal();
    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= AddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= AvailabilityChanged;
        if (_handle != IntPtr.Zero) { CancelMibChangeNotify2(_handle); _handle = IntPtr.Zero; }
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void RouteCallback(IntPtr context, IntPtr row, uint kind);
    [DllImport("iphlpapi.dll")] private static extern uint NotifyRouteChange2(ushort family, RouteCallback callback, IntPtr context, [MarshalAs(UnmanagedType.U1)] bool initial, out IntPtr handle);
    [DllImport("iphlpapi.dll")] private static extern uint CancelMibChangeNotify2(IntPtr handle);
}
