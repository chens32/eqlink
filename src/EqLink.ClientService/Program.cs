using System.Runtime.InteropServices;
using System.Security.Principal;
using EqLink.Core;

namespace EqLink.ClientService;

internal static class Program
{
    private const string ServiceName = "EqLink";
    private static readonly ManualResetEventSlim StopEvent = new(false);
    private static ServiceMainDelegate? _serviceMain;
    private static ServiceControlDelegate? _serviceControl;
    private static IntPtr _statusHandle;
    private static EqLinkDaemon? _daemon;

    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] is "--network-protection-remove" or "--network-protection-reset")
        {
            try
            {
                if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) return 5;
                if (System.Diagnostics.Process.GetProcessesByName("EqLink.ClientService").Any(p => p.Id != Environment.ProcessId)) return 1618;
                using var guard = new WindowsNetworkProtection();
                guard.Remove();
                if (args[0] == "--network-protection-reset")
                {
                    var settings = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "EqLink", "protection.json");
                    if (System.IO.Directory.Exists(System.IO.Path.GetDirectoryName(settings))) System.IO.File.WriteAllText(settings, "{\"enabled\":false}");
                }
                Console.WriteLine("EqLink network protection removed.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine("EqLink network protection cleanup failed: " + ex.Message); return 1603; }
        }
        if (args.SequenceEqual(["--console"]))
        {
            _daemon = new EqLinkDaemon();
            _daemon.Start();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; StopEvent.Set(); };
            StopEvent.Wait();
            _daemon.StopAsync().GetAwaiter().GetResult();
            return 0;
        }
        _serviceMain = ServiceMain;
        var table = new[] { new ServiceTableEntry(ServiceName, _serviceMain), new ServiceTableEntry(null, null) };
        if (!StartServiceCtrlDispatcher(table)) return Marshal.GetLastWin32Error();
        return 0;
    }

    private static void ServiceMain(int argc, IntPtr argv)
    {
        _serviceControl = ServiceControl;
        _statusHandle = RegisterServiceCtrlHandlerEx(ServiceName, _serviceControl, IntPtr.Zero);
        if (_statusHandle == IntPtr.Zero) return;
        SetStatus(2, 0);
        try
        {
            _daemon = new EqLinkDaemon();
            _daemon.Start();
            SetStatus(4, 1);
            StopEvent.Wait();
            SetStatus(3, 0);
            _daemon.StopAsync().GetAwaiter().GetResult();
            SetStatus(1, 0);
        }
        catch
        {
            SetStatus(1, 0);
            throw;
        }
    }

    private static uint ServiceControl(uint control, uint eventType, IntPtr eventData, IntPtr context)
    {
        if (control is 1 or 5) StopEvent.Set();
        return 0;
    }

    private static void SetStatus(uint state, uint accepted)
    {
        var status = new ServiceStatus { ServiceType = 0x10, CurrentState = state, ControlsAccepted = accepted, WaitHint = 30_000 };
        SetServiceStatus(_statusHandle, ref status);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServiceTableEntry(string? name, ServiceMainDelegate? callback)
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? Name = name;
        public ServiceMainDelegate? Callback = callback;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void ServiceMainDelegate(int argc, IntPtr argv);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint ServiceControlDelegate(uint control, uint eventType, IntPtr eventData, IntPtr context);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool StartServiceCtrlDispatcher([In] ServiceTableEntry[] table);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr RegisterServiceCtrlHandlerEx(string name, ServiceControlDelegate callback, IntPtr context);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetServiceStatus(IntPtr handle, ref ServiceStatus status);
}
