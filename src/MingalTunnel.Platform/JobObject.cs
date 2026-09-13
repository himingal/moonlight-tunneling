using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MingalTunnel.Platform;

/// <summary>
/// Ties child processes to this process's lifetime: when Mingal Tunnel exits
/// or crashes, Windows kills sing-box too. An orphaned sing-box would keep a
/// TUN adapter and the default route with nobody supervising it, and a later
/// start would spawn a second instance on the same WireGuard key, which makes
/// the VPN server bounce the session between the two.
/// </summary>
public sealed class JobObject : IDisposable
{
    private IntPtr _handle;

    public JobObject()
    {
        _handle = Native.CreateJobObjectW(IntPtr.Zero, null);
        if (_handle == IntPtr.Zero) throw new Win32Exception();

        var info = new Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = Native.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!Native.SetInformationJobObject(_handle, Native.JobObjectExtendedLimitInformation, ref info,
                Marshal.SizeOf<Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
            throw new Win32Exception();
    }

    public void Add(Process process)
    {
        if (!Native.AssignProcessToJobObject(_handle, process.Handle)) throw new Win32Exception();
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            Native.CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
