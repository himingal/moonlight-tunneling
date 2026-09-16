namespace MoonlightTunneling.Platform;

/// <summary>
/// Per-app kill-switch built on Windows Firewall. All rules share one name so
/// they can be listed and removed together (and by the uninstaller). Rules are
/// persistent: if Moonlight Tunneling isn't running at all, apps under kill-switch
/// stay offline instead of silently using the normal connection.
/// </summary>
public static class FirewallRules
{
    public const string RuleName = "MoonlightTunneling Kill-Switch";

    public static async Task RemoveAllAsync()
    {
        // netsh exits non-zero when nothing matched; that's fine.
        await ProcessRunner.RunAsync("netsh.exe", $"advfirewall firewall delete rule name=\"{RuleName}\"");
    }

    public static async Task<bool> BlockProgramAsync(string exePath)
    {
        var r = await ProcessRunner.RunAsync("netsh.exe",
            $"advfirewall firewall add rule name=\"{RuleName}\" dir=out action=block enable=yes profile=any program=\"{exePath}\"");
        return r.ExitCode == 0;
    }
}
