using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace EqLink.Core;

public sealed class RateWindow
{
    private readonly Queue<(double Time, ulong Sent, ulong Received)> _samples = new();
    private string? _identity;
    public (double? Up, double? Down) Add(string identity, double seconds, ulong sent, ulong received)
    {
        if (_identity != identity || _samples.Count > 0 && (sent < _samples.Last().Sent || received < _samples.Last().Received || seconds <= _samples.Last().Time)) Reset();
        _identity = identity; _samples.Enqueue((seconds, sent, received));
        while (_samples.Count > 2 && seconds - _samples.ElementAt(1).Time >= 3) _samples.Dequeue();
        var first = _samples.Peek(); var delta = seconds - first.Time;
        return delta <= 0 ? (null, null) : ((sent - first.Sent) / delta, (received - first.Received) / delta);
    }
    public void Reset() { _identity = null; _samples.Clear(); }
}

public sealed class WindowsTrafficSampler
{
    private readonly RateWindow _rates = new();
    public TrafficSnapshot Read(bool connected, string? interfaceId, DeviceProfile? profile, ISet<int> excludedPids)
    {
        if (!connected) { _rates.Reset(); return new(0, 0, 0, DateTimeOffset.UtcNow); }
        if (interfaceId is null || profile is null) return new(null, null, null, DateTimeOffset.UtcNow, "VIRTUAL_ADAPTER_UNAVAILABLE");
        int? count = null; double? up = null, down = null; string? error = null;
        try { count = TcpCount(profile.VirtualIp, profile.OverlayCidr, excludedPids); }
        catch { error = "TCP_TABLE_UNAVAILABLE"; }
        try
        {
            var guid = Guid.Parse(interfaceId); Check(ConvertInterfaceGuidToLuid(ref guid, out var luid));
            // MIB_IF_ROW2 is fixed-size in the public Windows SDK, including its two UTF-16 name buffers.
            var row = new IfRow { Luid = luid }; Check(GetIfEntry2(ref row));
            (up, down) = _rates.Add($"{guid}|{luid}|{row.Index}|{profile.VirtualIp}", (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency, row.OutOctets, row.InOctets);
            if (up is null) error ??= "WAITING_FOR_SAMPLE";
        }
        catch { _rates.Reset(); error = error is null ? "INTERFACE_COUNTER_UNAVAILABLE" : error + ";INTERFACE_COUNTER_UNAVAILABLE"; }
        return new(count, up, down, DateTimeOffset.UtcNow, error);
    }

    public static int TcpCount(string localIp, string cidr, ISet<int> excludedPids)
    {
        uint size = 0; var initial = GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 5, 0);
        if (initial != 122) Check(initial);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (size is < 4 or > 64_000_000) throw new IOException("TCP_TABLE_SIZE_INVALID");
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                var result = GetExtendedTcpTable(buffer, ref size, false, 2, 5, 0);
                if (result == 122) continue; Check(result);
                var rows = Marshal.ReadInt32(buffer); if (rows < 0 || 4L + rows * 24L > size) throw new IOException("TCP_TABLE_INVALID");
                var local = BitConverter.ToUInt32(IPAddress.Parse(localIp).GetAddressBytes());
                var subnet = IPAddress.Parse(cidr.Split('/')[0]).GetAddressBytes(); var count = 0;
                for (var i = 0; i < rows; i++)
                {
                    var row = buffer + 4 + i * 24;
                    if (Marshal.ReadInt32(row) != 5 || unchecked((uint)Marshal.ReadInt32(row, 4)) != local || excludedPids.Contains(Marshal.ReadInt32(row, 20))) continue;
                    var remote = BitConverter.GetBytes(Marshal.ReadInt32(row, 12));
                    if (remote.AsSpan(0, 3).SequenceEqual(subnet.AsSpan(0, 3))) count++;
                }
                return count;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new IOException("TCP_TABLE_CHANGED");
    }
    private static void Check(uint error) { if (error != 0) throw new Win32Exception(unchecked((int)error)); }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct IfRow
    {
        public ulong Luid; public uint Index; public Guid Guid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string Alias;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string Description;
        public uint PhysicalAddressLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] PhysicalAddress;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] PermanentPhysicalAddress;
        public uint Mtu, Type, TunnelType, MediaType, PhysicalMediumType, AccessType, DirectionType;
        public byte InterfaceFlags;
        public uint OperStatus, AdminStatus, MediaConnectState; public Guid NetworkGuid; public uint ConnectionType;
        public ulong TransmitLinkSpeed, ReceiveLinkSpeed, InOctets, InUcastPkts, InNUcastPkts, InDiscards, InErrors, InUnknownProtos,
            InUcastOctets, InMulticastOctets, InBroadcastOctets, OutOctets, OutUcastPkts, OutNUcastPkts, OutDiscards, OutErrors,
            OutUcastOctets, OutMulticastOctets, OutBroadcastOctets, OutQLen;
    }
    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(IntPtr table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order, uint family, uint tableClass, uint reserved);
    [DllImport("iphlpapi.dll")] private static extern uint ConvertInterfaceGuidToLuid(ref Guid guid, out ulong luid);
    [DllImport("iphlpapi.dll", CharSet = CharSet.Unicode)] private static extern uint GetIfEntry2(ref IfRow row);
}
