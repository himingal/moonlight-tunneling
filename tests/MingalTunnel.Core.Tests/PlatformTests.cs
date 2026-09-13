using MingalTunnel.Core;
using MingalTunnel.Platform;
using Xunit;

namespace MingalTunnel.Core.Tests;

public class PlatformTests
{
    [Fact]
    public void SnapshotSeesThisProcess()
    {
        var self = ProcessPaths.Snapshot().FirstOrDefault(p => p.Pid == Environment.ProcessId);
        Assert.False(string.IsNullOrEmpty(self.Path));
    }

    [Fact]
    public void ConnectionTablesReturnPids() => Assert.NotEmpty(ConnectionTables.PidsWithSockets());

    [Fact]
    public void PhysicalInterfaceIsNeverATun()
    {
        var ni = NetworkDetect.GetPhysicalInterface();
        Assert.NotNull(ni);
        Assert.NotEqual(NetworkDetect.TunInterfaceName, ni!.Name);
        Assert.DoesNotContain("sing-tun", ni.Description);
    }

    [Fact]
    public void DetectsInstalledDiscordAsSquirrel()
    {
        var discord = AppCatalog.DetectInstalledCurated().FirstOrDefault(a => a.CuratedKey == "discord");
        if (discord == null) return; // not installed on this machine
        Assert.Equal(AppMatchKind.Squirrel, discord.Kind);
        var running = AppCatalog.IconSource(discord);
        Assert.NotNull(running);
        Assert.Matches(PathPattern.ToRegex(PathPattern.ForApp(discord)), running!);
        var fromExe = AppCatalog.FromExecutable(running!);
        Assert.Equal(AppMatchKind.Squirrel, fromExe.Kind);
        Assert.Equal(discord.Target, fromExe.Target, ignoreCase: true);
    }

    [Fact]
    public void StartMenuScanFindsSomething() => Assert.NotEmpty(ShellLinks.ScanStartMenu());

    /// <summary>Only meaningful on a machine with discord-tunneling installed; reads its config, never prints the key.</summary>
    [Fact]
    public async Task LegacyImportWorksIfPresent()
    {
        TestEnv.Init();
        var legacy = await LegacyDiscordTunneling.DetectAsync();
        if (legacy?.ConfigPath == null) return;
        var p = LegacyDiscordTunneling.ImportProfile(legacy.ConfigPath);
        Assert.NotNull(p);
        Assert.NotEmpty(p!.Addresses);
        Assert.True(p.EndpointPort > 0);
        Assert.Equal(32, Convert.FromBase64String(Dpapi.Unprotect(p.PrivateKeyProtected)).Length);
        // And the imported profile yields a config sing-box accepts.
        RuleSetWriter.Write([], AppPaths.RuleSetFile);
        var json = SingBoxConfigBuilder.Build(TestEnv.Input(p));
        var r = await ProcessRunner.RunAsync(TestEnv.SingBox!, "check -c stdin", json, AppPaths.RuntimeDir);
        Assert.True(r.ExitCode == 0, r.Combined);
    }
}
