using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace MoonlightTunneling.Platform;

/// <summary>
/// Starts programs as the normal (non-elevated) user even though Moonlight Tunneling
/// itself runs as Administrator. Launching Discord or a browser elevated would
/// break drag and drop, notifications and file dialogs, and hand them admin
/// rights they have no business having. The token is borrowed from the desktop
/// shell (Explorer), which always runs non-elevated.
/// </summary>
public static class DeElevatedLauncher
{
    private const uint TOKEN_DUPLICATE = 0x0002;
    private const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint TOKEN_ADJUST_DEFAULT = 0x0080;
    private const uint TOKEN_ADJUST_SESSIONID = 0x0100;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const int CREATE_UNICODE_ENVIRONMENT = 0x00000400;

    public static bool ShellAvailable => Native.GetShellWindow() != IntPtr.Zero;

    public static void Launch(string exePath, string arguments, string? workingDir = null)
    {
        workingDir ??= Path.GetDirectoryName(exePath);

        if (!Elevation.IsAdministrator())
        {
            Process.Start(new ProcessStartInfo(exePath, arguments) { UseShellExecute = true, WorkingDirectory = workingDir ?? "" });
            return;
        }

        try
        {
            LaunchWithShellToken(exePath, arguments, workingDir);
        }
        catch (Exception)
        {
            // Explorer re-launches whatever it is asked to open in the user's own
            // (non-elevated) context. It can't forward arguments, so it's only a
            // fallback for plain executables.
            if (string.IsNullOrWhiteSpace(arguments))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{exePath}\"") { UseShellExecute = false });
            else
                throw;
        }
    }

    private static void LaunchWithShellToken(string exePath, string arguments, string? workingDir)
    {
        IntPtr shell = Native.GetShellWindow();
        if (shell == IntPtr.Zero) throw new InvalidOperationException("Desktop shell (Explorer) is not running.");
        Native.GetWindowThreadProcessId(shell, out uint shellPid);

        IntPtr hProc = IntPtr.Zero, hToken = IntPtr.Zero, hPrimary = IntPtr.Zero;
        try
        {
            hProc = Native.OpenProcess(Native.PROCESS_QUERY_INFORMATION, false, shellPid);
            if (hProc == IntPtr.Zero) throw new Win32Exception();
            if (!Native.OpenProcessToken(hProc, TOKEN_DUPLICATE, out hToken)) throw new Win32Exception();
            const uint access = TOKEN_QUERY | TOKEN_ASSIGN_PRIMARY | TOKEN_DUPLICATE | TOKEN_ADJUST_DEFAULT | TOKEN_ADJUST_SESSIONID;
            if (!Native.DuplicateTokenEx(hToken, access, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out hPrimary))
                throw new Win32Exception();

            var si = new Native.STARTUPINFO { cb = System.Runtime.InteropServices.Marshal.SizeOf<Native.STARTUPINFO>() };
            var cmd = new StringBuilder($"\"{exePath}\" {arguments}".TrimEnd());
            if (!Native.CreateProcessWithTokenW(hPrimary, 0, exePath, cmd, CREATE_UNICODE_ENVIRONMENT, IntPtr.Zero,
                    workingDir, ref si, out var pi))
                throw new Win32Exception();
            Native.CloseHandle(pi.hThread);
            Native.CloseHandle(pi.hProcess);
        }
        finally
        {
            if (hPrimary != IntPtr.Zero) Native.CloseHandle(hPrimary);
            if (hToken != IntPtr.Zero) Native.CloseHandle(hToken);
            if (hProc != IntPtr.Zero) Native.CloseHandle(hProc);
        }
    }
}
