namespace MoonlightTunneling.Core;

public static class AppPaths
{
    /// <summary>Overridable (MOONLIGHTTUNNELING_DATA) so tests never touch the real profile.</summary>
    public static string DataDir { get; set; } =
        Environment.GetEnvironmentVariable("MOONLIGHTTUNNELING_DATA") is { Length: > 0 } d
            ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MoonlightTunneling");

    public static string RuntimeDir => Path.Combine(DataDir, "runtime");
    public static string LogsDir => Path.Combine(DataDir, "logs");
    public static string ProfilesDir => Path.Combine(DataDir, "profiles");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string RuleSetFile => Path.Combine(RuntimeDir, "tunneled-apps.json");
    public static string SingBoxLog => Path.Combine(LogsDir, "sing-box.log");
    public static string AppLogFile => Path.Combine(LogsDir, "app.log");

    public static string InstallDir => AppContext.BaseDirectory;

    public static void EnsureCreated()
    {
        foreach (var d in new[] { DataDir, RuntimeDir, LogsDir, ProfilesDir })
            Directory.CreateDirectory(d);
    }
}
