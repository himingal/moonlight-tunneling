using System.Diagnostics;

namespace MingalTunnel.Core;

public static class AppCatalog
{
    private static string Env(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
    private static string LocalAppData => Env(Environment.SpecialFolder.LocalApplicationData);
    private static string RoamingAppData => Env(Environment.SpecialFolder.ApplicationData);
    private static string ProgramFiles => Env(Environment.SpecialFolder.ProgramFiles);
    private static string ProgramFilesX86 => Env(Environment.SpecialFolder.ProgramFilesX86);

    private sealed record Curated(string Key, string Name, Func<TunneledApp?> Detect);

    private static TunneledApp? Squirrel(string key, string name, string root, string exe) =>
        File.Exists(Path.Combine(root, "Update.exe")) && Directory.GetDirectories(root, "app-*").Any(d => File.Exists(Path.Combine(d, exe)))
            ? new TunneledApp
            {
                CuratedKey = key, Name = name, Kind = AppMatchKind.Squirrel, Target = root, ExeName = exe,
                LaunchPath = Path.Combine(root, "Update.exe"), LaunchArgs = $"--processStart \"{exe}\"",
            }
            : null;

    private static TunneledApp? FirstExisting(string key, string name, params string[] paths)
    {
        var p = paths.FirstOrDefault(File.Exists);
        return p == null ? null : new TunneledApp { CuratedKey = key, Name = name, Kind = AppMatchKind.ExactPath, Target = p };
    }

    private static TunneledApp? FolderIf(string key, string name, string folder, string launcher)
    {
        var exe = Path.Combine(folder, launcher);
        return File.Exists(exe)
            ? new TunneledApp { CuratedKey = key, Name = name, Kind = AppMatchKind.Folder, Target = folder, LaunchPath = exe }
            : null;
    }

    private static readonly Curated[] Known =
    [
        new("discord", "Discord", () => Squirrel("discord", "Discord", Path.Combine(LocalAppData, "Discord"), "Discord.exe")),
        new("discord-ptb", "Discord PTB", () => Squirrel("discord-ptb", "Discord PTB", Path.Combine(LocalAppData, "DiscordPTB"), "DiscordPTB.exe")),
        new("discord-canary", "Discord Canary", () => Squirrel("discord-canary", "Discord Canary", Path.Combine(LocalAppData, "DiscordCanary"), "DiscordCanary.exe")),
        new("slack", "Slack", () => Squirrel("slack", "Slack", Path.Combine(LocalAppData, "slack"), "slack.exe")),
        new("brave", "Brave", () => FirstExisting("brave", "Brave",
            Path.Combine(ProgramFiles, @"BraveSoftware\Brave-Browser\Application\brave.exe"),
            Path.Combine(ProgramFilesX86, @"BraveSoftware\Brave-Browser\Application\brave.exe"),
            Path.Combine(LocalAppData, @"BraveSoftware\Brave-Browser\Application\brave.exe"))),
        new("chrome", "Google Chrome", () => FirstExisting("chrome", "Google Chrome",
            Path.Combine(ProgramFiles, @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(ProgramFilesX86, @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(LocalAppData, @"Google\Chrome\Application\chrome.exe"))),
        new("edge", "Microsoft Edge", () => FirstExisting("edge", "Microsoft Edge",
            Path.Combine(ProgramFilesX86, @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(ProgramFiles, @"Microsoft\Edge\Application\msedge.exe"))),
        new("vivaldi", "Vivaldi", () => FirstExisting("vivaldi", "Vivaldi",
            Path.Combine(LocalAppData, @"Vivaldi\Application\vivaldi.exe"),
            Path.Combine(ProgramFiles, @"Vivaldi\Application\vivaldi.exe"))),
        // Opera keeps the real browser in a versioned subfolder behind a launcher.
        new("opera", "Opera", () => FolderIf("opera", "Opera", Path.Combine(LocalAppData, @"Programs\Opera"), "opera.exe")),
        new("opera-gx", "Opera GX", () => FolderIf("opera-gx", "Opera GX", Path.Combine(LocalAppData, @"Programs\Opera GX"), "opera.exe")),
        new("firefox", "Firefox", () => FirstExisting("firefox", "Firefox",
            Path.Combine(ProgramFiles, @"Mozilla Firefox\firefox.exe"),
            Path.Combine(ProgramFilesX86, @"Mozilla Firefox\firefox.exe"))),
        new("spotify", "Spotify", () => FirstExisting("spotify", "Spotify", Path.Combine(RoamingAppData, @"Spotify\Spotify.exe"))),
        new("telegram", "Telegram", () => FirstExisting("telegram", "Telegram", Path.Combine(RoamingAppData, @"Telegram Desktop\Telegram.exe"))),
        new("vscode", "VS Code", () => FirstExisting("vscode", "VS Code",
            Path.Combine(LocalAppData, @"Programs\Microsoft VS Code\Code.exe"),
            Path.Combine(ProgramFiles, @"Microsoft VS Code\Code.exe"))),
    ];

    public static List<TunneledApp> DetectInstalledCurated()
    {
        var list = new List<TunneledApp>();
        foreach (var c in Known)
        {
            try
            {
                var app = c.Detect();
                if (app != null) list.Add(app);
            }
            catch { }
        }
        return list;
    }

    public static bool IsSharedWebView(string path) =>
        Path.GetFileName(path).Equals("msedgewebview2.exe", StringComparison.OrdinalIgnoreCase);

    public static string FriendlyName(string exePath)
    {
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(exePath);
            var n = !string.IsNullOrWhiteSpace(vi.FileDescription) ? vi.FileDescription : vi.ProductName;
            if (!string.IsNullOrWhiteSpace(n) && n.Length <= 60) return n.Trim();
        }
        catch { }
        return Path.GetFileNameWithoutExtension(exePath);
    }

    /// <summary>
    /// Builds the right entry for an executable: Squirrel apps and Store
    /// packages get version-independent patterns so auto-updates don't silently
    /// drop them out of the tunnel.
    /// </summary>
    public static TunneledApp FromExecutable(string exePath, string? displayName = null)
    {
        exePath = Path.GetFullPath(exePath);
        var name = displayName ?? FriendlyName(exePath);
        var dir = Path.GetDirectoryName(exePath)!;
        var exe = Path.GetFileName(exePath);
        var dirName = Path.GetFileName(dir);

        // root\app-1.0.9257\Discord.exe
        if (dirName.StartsWith("app-", StringComparison.OrdinalIgnoreCase))
        {
            var root = Path.GetDirectoryName(dir)!;
            if (File.Exists(Path.Combine(root, "Update.exe")))
                return SquirrelEntry(name, root, exe);
        }
        // root\Discord.exe stub or root\Update.exe picked directly
        if (File.Exists(Path.Combine(dir, "Update.exe")))
        {
            // Picking Update.exe itself: the app's exe is normally named after its
            // root folder (Discord\app-*\Discord.exe), next to helpers we must skip.
            var target = exe.Equals("Update.exe", StringComparison.OrdinalIgnoreCase)
                ? Directory.GetDirectories(dir, "app-*").SelectMany(d => Directory.GetFiles(d, "*.exe"))
                    .Select(f => Path.GetFileName(f)!)
                    .Where(f => !f.Equals("Update.exe", StringComparison.OrdinalIgnoreCase) && !f.Contains("squirrel", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => Path.GetFileNameWithoutExtension(f).Equals(Path.GetFileName(dir), StringComparison.OrdinalIgnoreCase))
                    .ThenBy(f => f.Length)
                    .FirstOrDefault()
                : exe;
            if (target != null && Directory.GetDirectories(dir, "app-*").Any(d => File.Exists(Path.Combine(d, target))))
                return SquirrelEntry(name, dir, target);
        }

        var store = PathPattern.TryStorePackagePattern(exePath);
        if (store != null)
            return new TunneledApp { Name = name, Kind = AppMatchKind.Regex, Target = store, SeenPaths = [exePath] };

        return new TunneledApp { Name = name, Kind = AppMatchKind.ExactPath, Target = exePath };
    }

    private static TunneledApp SquirrelEntry(string name, string root, string exe) => new()
    {
        Name = name, Kind = AppMatchKind.Squirrel, Target = root, ExeName = exe,
        LaunchPath = Path.Combine(root, "Update.exe"), LaunchArgs = $"--processStart \"{exe}\"",
    };

    public static TunneledApp FromFolder(string folder) => new()
    {
        Name = Path.GetFileName(folder.TrimEnd('\\')) is { Length: > 0 } n ? n : folder,
        Kind = AppMatchKind.Folder,
        Target = Path.GetFullPath(folder).TrimEnd('\\'),
    };

    /// <summary>How to (re)open the app, or null when it can't be started directly (Store apps, bare folders).</summary>
    public static (string Exe, string Args)? GetLaunchCommand(TunneledApp a)
    {
        if (!string.IsNullOrEmpty(a.LaunchPath) && File.Exists(a.LaunchPath)) return (a.LaunchPath, a.LaunchArgs ?? "");
        if (a.Kind == AppMatchKind.ExactPath && File.Exists(a.Target)) return (a.Target, a.LaunchArgs ?? "");
        return null;
    }

    public static string DisplayPath(TunneledApp a) => a.Kind switch
    {
        AppMatchKind.Squirrel => Path.Combine(a.Target, "app-*", a.ExeName ?? ""),
        AppMatchKind.Folder => a.Target + @"\*",
        AppMatchKind.Regex => a.SeenPaths.FirstOrDefault() ?? a.Target,
        _ => a.Target,
    };

    public static bool Exists(TunneledApp a) => a.Kind switch
    {
        AppMatchKind.ExactPath => File.Exists(a.Target),
        AppMatchKind.Squirrel or AppMatchKind.Folder => Directory.Exists(a.Target),
        _ => true,
    };

    /// <summary>Best file to pull an icon from.</summary>
    public static string? IconSource(TunneledApp a)
    {
        try
        {
            switch (a.Kind)
            {
                case AppMatchKind.ExactPath: return File.Exists(a.Target) ? a.Target : null;
                case AppMatchKind.Squirrel:
                    var latest = Directory.GetDirectories(a.Target, "app-*").OrderByDescending(d => d).Select(d => Path.Combine(d, a.ExeName ?? "")).FirstOrDefault(File.Exists);
                    return latest ?? a.LaunchPath;
                case AppMatchKind.Folder:
                    return a.LaunchPath ?? Directory.EnumerateFiles(a.Target, "*.exe").FirstOrDefault();
                default:
                    return a.SeenPaths.FirstOrDefault(File.Exists);
            }
        }
        catch { return null; }
    }
}
