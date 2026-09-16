using MoonlightTunneling.Platform;

namespace MoonlightTunneling.Core;

/// <summary>
/// One-time move of what versions released as "Mingal Tunnel" left behind:
/// the data folder (settings, profiles, logs), the autostart task and the
/// kill-switch firewall rules. The old names only appear here.
/// </summary>
public static class RenameMigration
{
    private const string OldName = "MingalTunnel";
    private const string OldTaskName = "MingalTunnel";
    private const string OldFirewallRule = "MingalTunnel Kill-Switch";

    /// <summary>Runs before anything reads or writes the data folder. Returns a line for the log, or null.</summary>
    public static string? MoveDataFolder()
    {
        if (Environment.GetEnvironmentVariable("MOONLIGHTTUNNELING_DATA") is { Length: > 0 }) return null;
        return MoveDataFolder(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), OldName),
            AppPaths.DataDir);
    }

    internal static string? MoveDataFolder(string oldDir, string newDir)
    {
        if (!Directory.Exists(oldDir) || Directory.Exists(newDir)) return null;
        try
        {
            Directory.Move(oldDir, newDir);
            return $"Settings and VPN profiles moved from {oldDir}.";
        }
        catch
        {
            // Something still holds a file (an old copy still running): copy
            // what can be read and leave the old folder alone.
            CopyTree(oldDir, newDir);
            return $"Settings and VPN profiles copied from {oldDir} (some files were in use, so the old folder was left in place).";
        }
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
        {
            try { File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: false); }
            catch { }
        }
        foreach (var dir in Directory.GetDirectories(from))
            CopyTree(dir, Path.Combine(to, Path.GetFileName(dir)));
    }

    /// <summary>Needs Administrator. Recreates autostart under the new name and drops old-named firewall rules.</summary>
    public static async Task MigrateSystemAsync(string exePath)
    {
        var query = await ProcessRunner.RunAsync("schtasks.exe", $"/Query /TN \"{OldTaskName}\"");
        if (query.ExitCode == 0)
        {
            await ProcessRunner.RunAsync("schtasks.exe", $"/Delete /TN \"{OldTaskName}\" /F");
            var r = await ScheduledTaskAutostart.EnableAsync(exePath);
            if (r.ExitCode == 0) AppLog.Info("Start with Windows moved to the new app name.");
            else AppLog.Warn("Couldn't recreate Start with Windows after the rename; turn it on again in Settings. " + r.Combined);
        }
        // Rules under the old name would keep blocking the old exe path; the
        // kill-switch re-creates whatever is needed under the new name.
        await ProcessRunner.RunAsync("netsh.exe", $"advfirewall firewall delete rule name=\"{OldFirewallRule}\"");
    }
}
