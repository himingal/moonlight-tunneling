using System.Text.RegularExpressions;
using MingalTunnel.Platform;

namespace MingalTunnel.Core;

public static partial class SingBoxBinary
{
    /// <summary>The generated config uses the 1.12+ schema (endpoints, rule actions, new DNS servers).</summary>
    public static readonly Version Minimum = new(1, 12, 0);

    public static string? Locate(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath)) return File.Exists(overridePath) ? overridePath : null;

        var bundled = Path.Combine(AppPaths.InstallDir, "engine", "sing-box.exe");
        if (File.Exists(bundled)) return bundled;

        // Dev runs from bin\: walk up to the repo's third_party\engine.
        var dir = new DirectoryInfo(AppPaths.InstallDir);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "third_party", "engine", "sing-box.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    [GeneratedRegex(@"(\d+)\.(\d+)\.(\d+)")]
    private static partial Regex VersionRx();

    public static async Task<Version?> GetVersionAsync(string exe)
    {
        try
        {
            var r = await ProcessRunner.RunAsync(exe, "version", timeout: TimeSpan.FromSeconds(10));
            var m = VersionRx().Match(r.StdOut);
            return m.Success ? new Version(m.Value) : null;
        }
        catch
        {
            return null;
        }
    }
}
