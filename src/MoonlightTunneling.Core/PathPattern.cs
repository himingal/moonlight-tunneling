using System.Text;
using System.Text.RegularExpressions;

namespace MoonlightTunneling.Core;

/// <summary>
/// Builds process_path_regex patterns. They have to mean the same thing to
/// sing-box (Go RE2) and to .NET (live status / kill), so only the common
/// subset is used: anchors, escaped punctuation, [^\\]+ and .*.
/// </summary>
public static partial class PathPattern
{
    private const string Meta = @"\.+*?()|[]{}^$";

    /// <summary>
    /// Escapes regex metacharacters only. Regex.Escape is not usable here: it
    /// also escapes spaces and '#', and RE2 rejects "\ " as an invalid escape,
    /// which would break every path under "Program Files".
    /// </summary>
    public static string Re2Escape(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (var c in s)
        {
            if (Meta.Contains(c)) sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    public static string ForApp(TunneledApp a) => a.Kind switch
    {
        AppMatchKind.ExactPath => "(?i)^" + Re2Escape(a.Target) + "$",
        AppMatchKind.Squirrel => "(?i)^" + Re2Escape(a.Target.TrimEnd('\\')) + @"\\app-[^\\]+\\" + Re2Escape(a.ExeName ?? "") + "$",
        AppMatchKind.Folder => "(?i)^" + Re2Escape(a.Target.TrimEnd('\\')) + @"\\",
        AppMatchKind.Regex => a.Target,
        _ => throw new ArgumentOutOfRangeException(nameof(a)),
    };

    public static Regex ToRegex(string pattern) =>
        new(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    [GeneratedRegex(@"^(?<root>.*\\WindowsApps)\\(?<name>[^\\_]+)_[^\\]+__(?<pub>[^\\]+)\\(?<rest>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex StorePath();

    /// <summary>
    /// Store apps live in WindowsApps\Name_Version_Arch__Publisher\…, so the
    /// folder changes on every update; match any version of the same package.
    /// </summary>
    public static string? TryStorePackagePattern(string exePath)
    {
        var m = StorePath().Match(exePath);
        if (!m.Success) return null;
        return "(?i)^" + Re2Escape(m.Groups["root"].Value) + @"\\" + Re2Escape(m.Groups["name"].Value) + @"_[^\\]+__" +
               Re2Escape(m.Groups["pub"].Value) + @"\\" + Re2Escape(m.Groups["rest"].Value) + "$";
    }
}
