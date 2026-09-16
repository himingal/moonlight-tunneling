using System.Security;
using System.Security.Principal;
using System.Text;

namespace MoonlightTunneling.Platform;

/// <summary>
/// Autostart through Task Scheduler instead of the Startup folder: the app
/// needs Administrator rights for the TUN adapter, and only a task registered
/// with "highest privileges" can start elevated at logon without a UAC prompt.
/// Interactive logon type so the tray icon lives in the user's session.
/// </summary>
public static class ScheduledTaskAutostart
{
    public const string TaskName = "MoonlightTunneling";

    public static async Task<bool> IsEnabledAsync()
    {
        var r = await ProcessRunner.RunAsync("schtasks.exe", $"/Query /TN \"{TaskName}\"");
        return r.ExitCode == 0;
    }

    public static async Task<RunResult> EnableAsync(string exePath)
    {
        var user = WindowsIdentity.GetCurrent().Name;
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.3" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Moonlight Tunneling - per-app VPN split tunneling</Description></RegistrationInfo>
              <Triggers>
                <LogonTrigger><Enabled>true</Enabled><UserId>{SecurityElement.Escape(user)}</UserId><Delay>PT10S</Delay></LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <StartWhenAvailable>true</StartWhenAvailable>
                <AllowHardTerminate>true</AllowHardTerminate>
                <Priority>6</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec><Command>{SecurityElement.Escape(exePath)}</Command><Arguments>--autostart</Arguments></Exec>
              </Actions>
            </Task>
            """;
        var tmp = Path.Combine(Path.GetTempPath(), $"mingaltunnel-task-{Guid.NewGuid():N}.xml");
        await File.WriteAllTextAsync(tmp, xml, Encoding.Unicode);
        try
        {
            return await ProcessRunner.RunAsync("schtasks.exe", $"/Create /TN \"{TaskName}\" /XML \"{tmp}\" /F");
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    public static Task<RunResult> DisableAsync() =>
        ProcessRunner.RunAsync("schtasks.exe", $"/Delete /TN \"{TaskName}\" /F");
}
