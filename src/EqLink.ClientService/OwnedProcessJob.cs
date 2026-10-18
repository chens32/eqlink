using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace EqLink.ClientService;

/// <summary>Closing the service process also terminates only its own child transports.</summary>
internal sealed class OwnedProcessJob : IDisposable
{
    private IntPtr _handle;
    public OwnedProcessJob()
    {
        _handle = CreateJobObject(IntPtr.Zero, null);
        if (_handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        var buffer = Marshal.AllocHGlobal(144);
        try
        {
            Marshal.Copy(new byte[144], 0, buffer, 144);
            Marshal.WriteInt32(buffer, 16, 0x2000); // JOBOBJECT_EXTENDED_LIMIT_INFORMATION, KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(_handle, 9, buffer, 144)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch { Dispose(); throw; }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    public void Add(Process process)
    {
        if (!AssignProcessToJobObject(_handle, process.Handle))
        {
            var error = Marshal.GetLastWin32Error();
            if (!process.HasExited) process.Kill(true);
            throw new Win32Exception(error, "Cannot supervise EqLink child process.");
        }
    }
    public void Dispose() { if (_handle != IntPtr.Zero) { CloseHandle(_handle); _handle = IntPtr.Zero; } }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(IntPtr job, uint infoClass, IntPtr info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
