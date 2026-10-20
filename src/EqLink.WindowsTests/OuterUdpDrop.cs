using System.Buffers.Binary;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;

/// <summary>Acceptance-only dynamic WFP session, bounded to one UDP destination and physical source IPv4.</summary>
internal static class OuterUdpDrop
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 6 || !OperatingSystem.IsWindows()) throw new ArgumentException("--outer-udp-drop SERVER_IPV4 PORT SOURCE_IPV4 STOP_FILE REPORT_JSON");
        var target = IPAddress.Parse(args[1]); var port = ushort.Parse(args[2]); var source = IPAddress.Parse(args[3]);
        if (target.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || source.AddressFamily != target.AddressFamily || IPAddress.IsLoopback(target) || port < 1024) throw new ArgumentException("Invalid bounded fault target");
        IntPtr engine = IntPtr.Zero;
        var key = Guid.NewGuid();
        var report = new Dictionary<string, object?> { ["startedAt"] = DateTimeOffset.UtcNow, ["scope"] = "Dynamic WFP UDP drop for exact public destination, port, and physical source only", ["target"] = target.ToString(), ["port"] = port, ["source"] = source.ToString(), ["filterKey"] = key };
        void Save() => File.WriteAllText(args[5], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        try
        {
            using var mem = new Memory();
            var session = new Session { Key = Guid.NewGuid(), Flags = 1, Timeout = 5000 };
            Check(FwpmEngineOpen0(null, 10, IntPtr.Zero, ref session, out engine));
            var providerKey = Guid.NewGuid();
            var provider = new Provider { Key = providerKey, Display = mem.Display("EqLink bounded acceptance fault") };
            Check(FwpmProviderAdd0(engine, ref provider, IntPtr.Zero));
            var sub = new Sublayer { Key = Guid.NewGuid(), Display = mem.Display("EqLink bounded acceptance fault"), Provider = mem.Put(providerKey), Weight = 0x7fff };
            Check(FwpmSubLayerAdd0(engine, ref sub, IntPtr.Zero));
            var conditions = new List<Condition> {
                new() { Field = new("b235ae9a-1d64-49b8-a44c-5ff3d9095045"), Value = new() { Type=0x100, Data=mem.Put(new V4Mask { Address=Ip(target),Mask=uint.MaxValue }) } },
                new() { Field = new("d9ee00de-c1ef-4617-bfe3-ffd8f5a08957"), Value = new() { Type=3, Data=(IntPtr)(long)Ip(source) } },
                new() { Field = new("3971ef2b-623e-4f9a-8cb1-6e79b806b9a7"), Value = new() { Type=1, Data=(IntPtr)17 } },
                new() { Field = new("c35a604d-d22b-4e1a-91b4-68f674ee674b"), Value = new() { Type=2, Data=(IntPtr)port } },
            };
            var filter = new Filter { Key=key, Display=mem.Display("EqLink bounded UDP loss test"), Provider=mem.Put(providerKey),
                Layer=new("09e61aea-d214-46e2-9b21-b26b0b2f28c8"), Sublayer=sub.Key, Weight=new() {Type=4,Data=mem.Put(ulong.MaxValue)},
                Count=(uint)conditions.Count,Conditions=mem.Array(conditions),Action=new() {Type=0x1001} };
            Check(FwpmFilterAdd0(engine,ref filter,IntPtr.Zero,out _));
            report["ready"]=true;Save();
            var until=DateTimeOffset.UtcNow.AddSeconds(180);
            while(!File.Exists(args[4]) && DateTimeOffset.UtcNow<until) await Task.Delay(100);
            report["stopRequested"]=File.Exists(args[4]);
        }
        catch(Exception ex) { report["errorType"]=ex.GetType().Name; if(ex is Win32Exception native) report["nativeCode"]=native.NativeErrorCode; }
        finally
        {
            if(engine!=IntPtr.Zero){report["sessionClosed"]=FwpmEngineClose0(engine)==0;engine=IntPtr.Zero;}
            var verify=new Session {Key=Guid.NewGuid(),Timeout=5000};
            if(FwpmEngineOpen0(null,10,IntPtr.Zero,ref verify,out var reader)==0)
            {
                try { var result=FwpmFilterGetByKey0(reader,ref key,out var found);report["filterAbsentAfterClose"]=result==0x80320003; if(found!=IntPtr.Zero) FwpmFreeMemory0(ref found); }
                finally {FwpmEngineClose0(reader);}
            }
            report["completedAt"]=DateTimeOffset.UtcNow;Save();
        }
        return report.ContainsKey("errorType") || !Equals(report.GetValueOrDefault("filterAbsentAfterClose"),true) ? 1 : 0;
    }
    private static uint Ip(IPAddress ip)=>BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes());
    private static void Check(uint code){if(code!=0)throw new Win32Exception(unchecked((int)code));}
    [StructLayout(LayoutKind.Sequential)] private struct Display{public IntPtr Name,Description;}
    [StructLayout(LayoutKind.Sequential)] private struct Blob{public uint Size;public IntPtr Data;}
    [StructLayout(LayoutKind.Sequential)] private struct Session{public Guid Key;public Display Display;public uint Flags,Timeout,Process;public IntPtr Sid,Username;public int Kernel;}
    [StructLayout(LayoutKind.Sequential)] private struct Provider{public Guid Key;public Display Display;public uint Flags;public Blob Data;public IntPtr Service;}
    [StructLayout(LayoutKind.Sequential)] private struct Sublayer{public Guid Key;public Display Display;public uint Flags;public IntPtr Provider;public Blob Data;public ushort Weight;}
    [StructLayout(LayoutKind.Sequential)] private struct Value{public uint Type;public IntPtr Data;}
    [StructLayout(LayoutKind.Sequential)] private struct V4Mask{public uint Address,Mask;}
    [StructLayout(LayoutKind.Sequential)] private struct Condition{public Guid Field;public uint Match;public Value Value;}
    [StructLayout(LayoutKind.Sequential)] private struct Action{public uint Type;public Guid Key;}
    [StructLayout(LayoutKind.Explicit,Size=16)] private struct Context{[FieldOffset(0)]public ulong Raw;[FieldOffset(0)]public Guid Key;}
    [StructLayout(LayoutKind.Sequential)] private struct Filter{public Guid Key;public Display Display;public uint Flags;public IntPtr Provider;public Blob Data;public Guid Layer,Sublayer;public Value Weight;public uint Count;public IntPtr Conditions;public Action Action;public Context Context;public IntPtr Reserved;public ulong Id;public Value EffectiveWeight;}
    private sealed class Memory:IDisposable
    {
        private readonly List<IntPtr> _items=[];
        public IntPtr Put<T>(T value)where T:struct{var p=Marshal.AllocHGlobal(Marshal.SizeOf<T>());_items.Add(p);Marshal.StructureToPtr(value,p,false);return p;}
        public Display Display(string text){var p=Marshal.StringToHGlobalUni(text);_items.Add(p);return new(){Name=p};}
        public IntPtr Array(List<Condition> values){var size=Marshal.SizeOf<Condition>();var p=Marshal.AllocHGlobal(size*values.Count);_items.Add(p);for(var i=0;i<values.Count;i++)Marshal.StructureToPtr(values[i],p+i*size,false);return p;}
        public void Dispose(){foreach(var p in _items)Marshal.FreeHGlobal(p);}
    }
    [DllImport("fwpuclnt.dll",CharSet=CharSet.Unicode)]private static extern uint FwpmEngineOpen0(string? server,uint auth,IntPtr identity,ref Session session,out IntPtr engine);
    [DllImport("fwpuclnt.dll")]private static extern uint FwpmEngineClose0(IntPtr engine);
    [DllImport("fwpuclnt.dll")]private static extern uint FwpmProviderAdd0(IntPtr engine,ref Provider provider,IntPtr security);
    [DllImport("fwpuclnt.dll")]private static extern uint FwpmSubLayerAdd0(IntPtr engine,ref Sublayer sublayer,IntPtr security);
    [DllImport("fwpuclnt.dll")]private static extern uint FwpmFilterAdd0(IntPtr engine,ref Filter filter,IntPtr security,out ulong id);
    [DllImport("fwpuclnt.dll")]private static extern uint FwpmFilterGetByKey0(IntPtr engine,ref Guid key,out IntPtr filter);
    [DllImport("fwpuclnt.dll")]private static extern void FwpmFreeMemory0(ref IntPtr memory);
}
