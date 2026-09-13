using MingalTunnel.Core;
using Xunit;

namespace MingalTunnel.Core.Tests;

public class PathPatternTests
{
    private static bool Match(TunneledApp a, string path) => PathPattern.ToRegex(PathPattern.ForApp(a)).IsMatch(path);

    [Fact]
    public void SquirrelSurvivesAutoUpdate()
    {
        var a = new TunneledApp { Kind = AppMatchKind.Squirrel, Target = @"C:\Users\migue\AppData\Local\Discord", ExeName = "Discord.exe" };
        Assert.True(Match(a, @"C:\Users\migue\AppData\Local\Discord\app-1.0.9257\Discord.exe"));
        Assert.True(Match(a, @"C:\Users\migue\AppData\Local\Discord\app-1.0.9999\Discord.exe"));
        Assert.True(Match(a, @"c:\users\MIGUE\appdata\local\discord\app-1.0.9999\discord.exe"));
        // Squirrel's generic updater and other Squirrel apps stay out.
        Assert.False(Match(a, @"C:\Users\migue\AppData\Local\Discord\Update.exe"));
        Assert.False(Match(a, @"C:\Users\migue\AppData\Local\slack\app-4.41.0\slack.exe"));
        Assert.False(Match(a, @"C:\Users\migue\AppData\Local\Discord\app-1.0.9257\sub\Discord.exe"));
    }

    [Fact]
    public void ExactPathWithSpacesAndParens()
    {
        var a = new TunneledApp { Kind = AppMatchKind.ExactPath, Target = @"C:\Program Files (x86)\Some Game+ [v2]\game.exe" };
        Assert.True(Match(a, @"C:\Program Files (x86)\Some Game+ [v2]\game.exe"));
        Assert.False(Match(a, @"C:\Program Files (x86)\Some Game+ [v2]\gameXexe"));
        Assert.DoesNotContain(@"\ ", PathPattern.ForApp(a));
    }

    [Fact]
    public void FolderMatchesEverythingInside()
    {
        var a = new TunneledApp { Kind = AppMatchKind.Folder, Target = @"D:\Games\Valorant\" };
        Assert.True(Match(a, @"D:\Games\Valorant\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe"));
        Assert.False(Match(a, @"D:\Games\ValorantOther\x.exe"));
    }

    [Fact]
    public void StorePackageIgnoresVersion()
    {
        var p = PathPattern.TryStorePackagePattern(@"C:\Program Files\WindowsApps\5319275A.WhatsAppDesktop_2.2450.6.0_x64__cv1g1gvanyjgm\WhatsApp.exe");
        Assert.NotNull(p);
        var rx = PathPattern.ToRegex(p!);
        Assert.True(rx.IsMatch(@"C:\Program Files\WindowsApps\5319275A.WhatsAppDesktop_2.2511.1.0_x64__cv1g1gvanyjgm\WhatsApp.exe"));
        Assert.False(rx.IsMatch(@"C:\Program Files\WindowsApps\Other_1.0_x64__cv1g1gvanyjgm\WhatsApp.exe"));
    }

    [Fact]
    public void RuleSetAlwaysHasAProcessRule()
    {
        Assert.Contains(RuleSetWriter.Placeholder.Replace("\\", "\\\\"), RuleSetWriter.Build([]));
        var json = RuleSetWriter.Build([new TunneledApp { Enabled = false, Kind = AppMatchKind.ExactPath, Target = @"C:\x.exe" }]);
        Assert.DoesNotContain("x.exe", json);
    }
}
