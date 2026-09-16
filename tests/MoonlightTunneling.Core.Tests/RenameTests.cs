using MoonlightTunneling.Core;
using Xunit;

namespace MoonlightTunneling.Core.Tests;

public class RenameTests
{
    [Fact]
    public void DataFolderMovesOnceAndKeepsFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "mt-rename-" + Guid.NewGuid().ToString("N"));
        var oldDir = Path.Combine(root, "MingalTunnel");
        var newDir = Path.Combine(root, "MoonlightTunneling");
        Directory.CreateDirectory(Path.Combine(oldDir, "profiles"));
        File.WriteAllText(Path.Combine(oldDir, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(oldDir, "profiles", "a.json"), "{}");
        try
        {
            Assert.NotNull(RenameMigration.MoveDataFolder(oldDir, newDir));
            Assert.True(File.Exists(Path.Combine(newDir, "settings.json")));
            Assert.True(File.Exists(Path.Combine(newDir, "profiles", "a.json")));
            Assert.False(Directory.Exists(oldDir));
            // Second run is a no-op.
            Assert.Null(RenameMigration.MoveDataFolder(oldDir, newDir));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ExistingNewFolderIsNeverOverwritten()
    {
        var root = Path.Combine(Path.GetTempPath(), "mt-rename-" + Guid.NewGuid().ToString("N"));
        var oldDir = Path.Combine(root, "old");
        var newDir = Path.Combine(root, "new");
        Directory.CreateDirectory(oldDir);
        Directory.CreateDirectory(newDir);
        try
        {
            Assert.Null(RenameMigration.MoveDataFolder(oldDir, newDir));
            Assert.True(Directory.Exists(oldDir));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void FriendlyPathHidesTheUserName()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.Equal(@"%LOCALAPPDATA%\Discord\app-*\Discord.exe", AppCatalog.FriendlyPath(local + @"\Discord\app-*\Discord.exe"));
        Assert.Equal(@"C:\Program Files\x\y.exe", AppCatalog.FriendlyPath(@"C:\Program Files\x\y.exe"));
        Assert.DoesNotContain(Environment.UserName, AppCatalog.FriendlyPath(local + @"\a.exe"), StringComparison.OrdinalIgnoreCase);
    }
}
