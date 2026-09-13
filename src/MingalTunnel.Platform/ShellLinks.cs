using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace MingalTunnel.Platform;

public sealed record ShortcutInfo(string Name, string LinkPath, string TargetPath, string Arguments);

public static class ShellLinks
{
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr findData, uint flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int cch, out int icon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int icon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink;

    public static ShortcutInfo? Read(string lnkPath)
    {
        try
        {
            var link = (IShellLinkW)new CShellLink();
            try
            {
                ((IPersistFile)link).Load(lnkPath, 0);
                var target = new StringBuilder(1024);
                link.GetPath(target, target.Capacity, IntPtr.Zero, 0);
                var args = new StringBuilder(2048);
                link.GetArguments(args, args.Capacity);
                var t = Environment.ExpandEnvironmentVariables(target.ToString());
                if (string.IsNullOrWhiteSpace(t)) return null;
                return new ShortcutInfo(Path.GetFileNameWithoutExtension(lnkPath), lnkPath, t, args.ToString());
            }
            finally
            {
                Marshal.FinalReleaseComObject(link);
            }
        }
        catch
        {
            return null;
        }
    }

    public static void Create(string lnkPath, string target, string arguments, string workingDir, string? icon)
    {
        var link = (IShellLinkW)new CShellLink();
        try
        {
            link.SetPath(target);
            link.SetArguments(arguments);
            link.SetWorkingDirectory(workingDir);
            if (icon != null) link.SetIconLocation(icon, 0);
            ((IPersistFile)link).Save(lnkPath, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    /// <summary>Start Menu shortcuts (user + all users) that point at an existing .exe.</summary>
    public static List<ShortcutInfo> ScanStartMenu()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
        };
        var result = new Dictionary<string, ShortcutInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots.Where(Directory.Exists))
        {
            IEnumerable<string> files;
            // Localized Windows keeps legacy junctions (e.g. "Programas") that deny
            // listing; skipping them instead of failing is what keeps the scan non-empty.
            var opts = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
            };
            try { files = Directory.EnumerateFiles(root, "*.lnk", opts).ToList(); }
            catch { continue; }
            foreach (var f in files)
            {
                var info = Read(f);
                if (info == null) continue;
                if (!info.TargetPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(info.TargetPath)) continue;
                var n = info.Name.ToLowerInvariant();
                var exe = Path.GetFileName(info.TargetPath).ToLowerInvariant();
                if (n.Contains("uninstall") || n.Contains("desinstalar") || exe.StartsWith("unins") || exe.Contains("uninstall")) continue;
                result.TryAdd(info.TargetPath + "|" + info.Arguments, info);
            }
        }
        return result.Values.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
