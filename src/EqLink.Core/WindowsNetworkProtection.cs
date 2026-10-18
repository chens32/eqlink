using System.Buffers.Binary;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace EqLink.Core;

public sealed record ProtectionStatus(bool Enabled, bool Installed, bool Allowing, string? Prefix, string? Error = null);

/// <summary>Windows x64 WFP policy. Persistent/boot-time denial survives the service;
/// only the live service owns the dynamic, interface-bound exceptions. No firewall profile is changed.</summary>
public sealed class WindowsNetworkProtection : IDisposable
{
    public static readonly Guid Owner = new("7324d715-31c7-4ac6-9618-169738369a44");
    private readonly Guid _owner, _sublayer;
    private readonly object _gate = new();
    private IntPtr _engine, _allowEngine;
    private string? _prefix, _localIp;
    private ulong _allowedLuid;
    public bool Installed { get; private set; }
    public bool Allowing { get { lock (_gate) return _allowEngine != IntPtr.Zero; } }

    public WindowsNetworkProtection(Guid? owner = null)
    {
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 8) throw new PlatformNotSupportedException("WFP requires Windows x64.");
        _owner = owner ?? Owner;
        _sublayer = Key(_owner, "sublayer");
        Open(ref _engine, false);
    }

    public void EnsureBlocked(string prefix, string localIp)
    {
        ValidatePrefix(prefix, localIp);
        lock (_gate)
        {
            if (_engine == IntPtr.Zero) Open(ref _engine, false);
            try { EnsureBlockedCore(prefix,localIp); }
            catch (Win32Exception)
            {
                // BFE restarts invalidate RPC sessions. Reopen once; failure propagates and
                // prevents the coordinator from reporting a usable connection.
                if (_allowEngine != IntPtr.Zero) FwpmEngineClose0(_allowEngine);
                if (_engine != IntPtr.Zero) FwpmEngineClose0(_engine);
                _allowEngine = _engine = IntPtr.Zero; _allowedLuid = 0; Installed = false;
                Open(ref _engine, false);
                EnsureBlockedCore(prefix,localIp);
            }
        }
    }

    private void EnsureBlockedCore(string prefix,string localIp)
    {
            if (Installed && prefix == _prefix && localIp == _localIp && Enumerate(_engine).Count(r=>(r.Flags & 3)!=0)==8) return;
            Deny();
            Transaction(_engine, () =>
            {
                using var mem = new NativeMemory();
                var provider = new Provider { Key = _owner, Display = mem.Display("EqLink network protection"), Flags = 1 };
                CheckExists(FwpmProviderAdd0(_engine, ref provider, IntPtr.Zero));
                var sublayer = new Sublayer { Key = _sublayer, Display = mem.Display("EqLink virtual IPv4 policy"), Flags = 1, Provider = mem.Put(_owner), Weight = 0x7fff };
                CheckExists(FwpmSubLayerAdd0(_engine, ref sublayer, IntPtr.Zero));
                foreach (var rule in Enumerate(_engine).Where(r => (r.Flags & 3) != 0)) { var key = rule.Key; Check(FwpmFilterDeleteByKey0(_engine, ref key)); }
                foreach (var layer in new[] { Outbound, Inbound })
                foreach (var flags in new uint[] { 1, 2 })
                {
                    Add(_engine, layer, flags, false, prefix, localIp, null, false);
                    Add(_engine, layer, flags, true, prefix, localIp, null, true);
                }
            });
            _prefix = prefix; _localIp = localIp; Installed = true;
    }

    public void Allow(ulong interfaceLuid)
    {
        if (interfaceLuid == 0) throw new ArgumentException("Missing virtual interface LUID.");
        lock (_gate)
        {
            if (!Installed) throw new InvalidOperationException("Persistent network protection is not installed.");
            if (_allowEngine != IntPtr.Zero && _allowedLuid == interfaceLuid) return;
            Deny();
            Open(ref _allowEngine, true);
            try
            {
                Transaction(_allowEngine, () =>
                {
                    foreach (var layer in new[] { Outbound, Inbound }) Add(_allowEngine, layer, 0, true, _prefix!, _localIp!, interfaceLuid, false);
                });
                _allowedLuid = interfaceLuid;
            }
            catch { Deny(); throw; }
        }
    }

    public void Deny()
    {
        lock (_gate)
        {
            if (_allowEngine != IntPtr.Zero) { var handle = _allowEngine; _allowEngine = IntPtr.Zero; Check(FwpmEngineClose0(handle)); }
            _allowedLuid = 0;
        }
    }

    public void Remove()
    {
        lock (_gate)
        {
            Deny();
            Transaction(_engine, () =>
            {
                foreach (var rule in Enumerate(_engine)) { var key = rule.Key; Check(FwpmFilterDeleteByKey0(_engine, ref key)); }
                var sub = _sublayer; var provider = _owner;
                CheckMissing(FwpmSubLayerDeleteByKey0(_engine, ref sub));
                CheckMissing(FwpmProviderDeleteByKey0(_engine, ref provider));
            });
            Installed = false; _prefix = null; _localIp = null;
        }
    }

    public object Inspect()
    {
        lock (_gate) return new { provider = _owner, filterCount = Enumerate(_engine).Count, installed = Installed, allowing = Allowing, prefix = _prefix };
    }

    public void Dispose()
    {
        lock (_gate)
        {
            Deny();
            if (_engine != IntPtr.Zero) { Check(FwpmEngineClose0(_engine)); _engine = IntPtr.Zero; }
        }
    }

    public static void ValidatePrefix(string prefix, string localIp)
    {
        var settings = new ServerSettings { PublicHost = "validation.invalid", OverlayCidr = prefix };
        var network = settings.Overlay().Network;
        if (!IPAddress.TryParse(localIp, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            !ip.GetAddressBytes().AsSpan(0, 3).SequenceEqual(IPAddress.Parse(network).GetAddressBytes().AsSpan(0, 3)) || ip.GetAddressBytes()[3] is 0 or 255)
            throw new ArgumentException("Protection address must belong to its private /24.");
    }

    private void Add(IntPtr engine, Guid layer, uint flags, bool permit, string prefix, string localIp, ulong? luid, bool self)
    {
        using var mem = new NativeMemory();
        var conditions = new List<Condition>();
        var address = self ? localIp : prefix.Split('/')[0];
        conditions.Add(new Condition { Field = RemoteAddress, Value = new Value { Type = 0x100, Data = mem.Put(new V4Mask { Address = Ip(address), Mask = self ? uint.MaxValue : 0xffffff00 }) } });
        if (permit)
        {
            conditions.Add(new Condition { Field = LocalAddress, Value = new Value { Type = 3, Data = (IntPtr)(long)Ip(localIp) } });
            if (self) conditions.Add(new Condition { Field = FlagsCondition, Match = 6, Value = new Value { Type = 3, Data = (IntPtr)1 } });
            else conditions.Add(new Condition { Field = LocalInterface, Value = new Value { Type = 4, Data = mem.Put(luid!.Value) } });
        }
        var tag = $"{prefix}|{layer}|{flags}|{permit}|{self}|{luid}";
        var filter = new Filter
        {
            Key = Key(_owner, tag), Display = mem.Display("EqLink " + (permit ? "allow " : "block ") + prefix), Flags = flags,
            Provider = mem.Put(_owner), Layer = layer, Sublayer = _sublayer,
            Weight = new Value { Type = 4, Data = mem.Put(permit ? 100UL : 10UL) },
            Count = (uint)conditions.Count, Conditions = mem.Array(conditions), Action = new Action { Type = permit ? 0x1002u : 0x1001u }
        };
        Check(FwpmFilterAdd0(engine, ref filter, IntPtr.Zero, out _));
    }

    private List<Filter> Enumerate(IntPtr engine)
    {
        using var mem = new NativeMemory();
        var result = new List<Filter>();
        foreach (var layer in new[] { Outbound, Inbound })
        {
            var template = new EnumTemplate { Provider = mem.Put(_owner), Layer = layer, Flags = 0x18, ActionMask = uint.MaxValue };
            Check(FwpmFilterCreateEnumHandle0(engine, ref template, out var handle));
            try
            {
                while (true)
                {
                    Check(FwpmFilterEnum0(engine, handle, 128, out var entries, out var count));
                    try
                    {
                        for (var i = 0; i < count; i++)
                        {
                            var rule = Marshal.PtrToStructure<Filter>(Marshal.ReadIntPtr(entries, i * IntPtr.Size));
                            if (rule.Provider != IntPtr.Zero && Marshal.PtrToStructure<Guid>(rule.Provider) == _owner && rule.Sublayer == _sublayer) result.Add(rule);
                        }
                    }
                    finally { FwpmFreeMemory0(ref entries); }
                    if (count < 128) break;
                }
            }
            finally { Check(FwpmFilterDestroyEnumHandle0(engine, handle)); }
        }
        return result;
    }

    private static void Open(ref IntPtr handle, bool dynamic)
    {
        var session = new Session { Key = Guid.NewGuid(), Flags = dynamic ? 1u : 0u, Timeout = 5000 };
        Check(FwpmEngineOpen0(null, 10, IntPtr.Zero, ref session, out handle));
    }
    private static void Transaction(IntPtr engine, System.Action action)
    {
        Check(FwpmTransactionBegin0(engine, 0));
        try { action(); Check(FwpmTransactionCommit0(engine)); }
        catch { FwpmTransactionAbort0(engine); throw; }
    }
    private static uint Ip(string ip) => BinaryPrimitives.ReadUInt32BigEndian(IPAddress.Parse(ip).GetAddressBytes());
    private static Guid Key(Guid owner, string name) => new(SHA256.HashData(Encoding.UTF8.GetBytes(owner + ":" + name)).AsSpan(0, 16));
    private static void Check(uint code) { if (code != 0) throw new Win32Exception(unchecked((int)code), $"WFP policy operation failed (0x{code:X8})."); }
    private static void CheckExists(uint code) { if (code != 0x80320009) Check(code); }
    private static void CheckMissing(uint code) { if (code is not (0x80320005 or 0x80320007 or 0x80320008)) Check(code); }

    private static readonly Guid Outbound = new("1e5c9fae-8a84-4135-a331-950b54229ecd"), Inbound = new("c86fd1bf-21cd-497e-a0bb-17425c885c58");
    private static readonly Guid RemoteAddress = new("b235ae9a-1d64-49b8-a44c-5ff3d9095045"), LocalAddress = new("d9ee00de-c1ef-4617-bfe3-ffd8f5a08957");
    private static readonly Guid LocalInterface = new("4cd62a49-59c3-4969-b7f3-bda5d32890a4"), FlagsCondition = new("632ce23b-5167-435c-86d7-e903684aa80c");
    [StructLayout(LayoutKind.Sequential)] private struct Display { public IntPtr Name, Description; }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public uint Size; public IntPtr Data; }
    [StructLayout(LayoutKind.Sequential)] private struct Session { public Guid Key; public Display Display; public uint Flags, Timeout, Process; public IntPtr Sid, Username; public int Kernel; }
    [StructLayout(LayoutKind.Sequential)] private struct Provider { public Guid Key; public Display Display; public uint Flags; public Blob Data; public IntPtr Service; }
    [StructLayout(LayoutKind.Sequential)] private struct Sublayer { public Guid Key; public Display Display; public uint Flags; public IntPtr Provider; public Blob Data; public ushort Weight; }
    [StructLayout(LayoutKind.Sequential)] private struct Value { public uint Type; public IntPtr Data; }
    [StructLayout(LayoutKind.Sequential)] private struct V4Mask { public uint Address, Mask; }
    [StructLayout(LayoutKind.Sequential)] private struct Condition { public Guid Field; public uint Match; public Value Value; }
    [StructLayout(LayoutKind.Sequential)] private struct Action { public uint Type; public Guid Key; }
    [StructLayout(LayoutKind.Explicit, Size = 16)] private struct Context { [FieldOffset(0)] public ulong Raw; [FieldOffset(0)] public Guid Key; }
    [StructLayout(LayoutKind.Sequential)] private struct Filter { public Guid Key; public Display Display; public uint Flags; public IntPtr Provider; public Blob Data; public Guid Layer, Sublayer; public Value Weight; public uint Count; public IntPtr Conditions; public Action Action; public Context Context; public IntPtr Reserved; public ulong Id; public Value EffectiveWeight; }
    [StructLayout(LayoutKind.Sequential)] private struct EnumTemplate { public IntPtr Provider; public Guid Layer; public uint Type, Flags; public IntPtr Context; public uint Count; public IntPtr Conditions; public uint ActionMask; public IntPtr Callout; }
    private sealed class NativeMemory : IDisposable
    {
        private readonly List<IntPtr> _allocations = [];
        public IntPtr Put<T>(T value) where T : struct { var p = Marshal.AllocHGlobal(Marshal.SizeOf<T>()); _allocations.Add(p); Marshal.StructureToPtr(value, p, false); return p; }
        public Display Display(string name) { var p = Marshal.StringToHGlobalUni(name); _allocations.Add(p); return new Display { Name = p }; }
        public IntPtr Array(List<Condition> values) { var size = Marshal.SizeOf<Condition>(); var p = Marshal.AllocHGlobal(size * values.Count); _allocations.Add(p); for (var i = 0; i < values.Count; i++) Marshal.StructureToPtr(values[i], p + size * i, false); return p; }
        public void Dispose() { foreach (var p in _allocations) Marshal.FreeHGlobal(p); }
    }
    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)] private static extern uint FwpmEngineOpen0(string? server, uint auth, IntPtr identity, ref Session session, out IntPtr engine);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmEngineClose0(IntPtr engine);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmProviderAdd0(IntPtr engine, ref Provider provider, IntPtr security);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmProviderDeleteByKey0(IntPtr engine, ref Guid key);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmSubLayerAdd0(IntPtr engine, ref Sublayer sublayer, IntPtr security);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmSubLayerDeleteByKey0(IntPtr engine, ref Guid key);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmFilterAdd0(IntPtr engine, ref Filter filter, IntPtr security, out ulong id);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmFilterDeleteByKey0(IntPtr engine, ref Guid key);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmFilterCreateEnumHandle0(IntPtr engine, ref EnumTemplate template, out IntPtr handle);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmFilterEnum0(IntPtr engine, IntPtr handle, uint requested, out IntPtr entries, out uint count);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmFilterDestroyEnumHandle0(IntPtr engine, IntPtr handle);
    [DllImport("fwpuclnt.dll")] private static extern void FwpmFreeMemory0(ref IntPtr memory);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmTransactionBegin0(IntPtr engine, uint flags);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmTransactionCommit0(IntPtr engine);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmTransactionAbort0(IntPtr engine);
}
