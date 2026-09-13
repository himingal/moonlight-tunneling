using System.Text;
using System.Text.Json.Nodes;

namespace MingalTunnel.Core;

/// <summary>
/// Writes the local rule-set sing-box watches. sing-box reloads a local
/// rule-set on its own when the file changes, so ticking an app applies to new
/// connections within seconds with no restart and no dropped connections.
/// </summary>
public static class RuleSetWriter
{
    /// <summary>
    /// Never matches a real path. The set must always contain a process rule:
    /// sing-box decides at startup whether it needs process lookups at all, and
    /// an empty set would leave that off for rules added later.
    /// </summary>
    public const string Placeholder = "^MINGAL_TUNNEL_NO_APP_SELECTED$";

    public static string Build(IEnumerable<TunneledApp> apps)
    {
        var patterns = apps.Where(a => a.Enabled).Select(PathPattern.ForApp).Distinct().ToList();
        if (patterns.Count == 0) patterns.Add(Placeholder);
        var arr = new JsonArray();
        foreach (var p in patterns) arr.Add(p);
        var root = new JsonObject
        {
            ["version"] = 3,
            ["rules"] = new JsonArray { new JsonObject { ["process_path_regex"] = arr } },
        };
        return root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Returns true when the file content actually changed.</summary>
    public static bool Write(IEnumerable<TunneledApp> apps, string path)
    {
        var json = Build(apps);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path) && File.ReadAllText(path) == json) return false;
        // Overwrite in place (no temp+rename): sing-box watches this path and an
        // in-place write is what was verified to trigger its reload.
        File.WriteAllText(path, json, new UTF8Encoding(false));
        return true;
    }
}
