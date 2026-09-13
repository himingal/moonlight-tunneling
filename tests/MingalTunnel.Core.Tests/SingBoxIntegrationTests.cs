using System.Text.Json.Nodes;
using MingalTunnel.Core;
using MingalTunnel.Platform;
using Xunit;

namespace MingalTunnel.Core.Tests;

/// <summary>Runs the real bundled sing-box (third_party\engine) against generated configs. Never opens a TUN.</summary>
public class SingBoxIntegrationTests
{
    static SingBoxIntegrationTests() => TestEnv.Init();

    private static async Task AssertAccepted(string json)
    {
        var sb = TestEnv.SingBox;
        Assert.True(sb != null, "run build\\fetch-deps.ps1 first");
        var r = await ProcessRunner.RunAsync(sb!, "check -c stdin", json, AppPaths.RuntimeDir);
        Assert.True(r.ExitCode == 0, r.Combined);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task GeneratedConfigPassesCheck(bool v6Profile, bool v6Capture)
    {
        RuleSetWriter.Write([
            new TunneledApp { Enabled = true, Kind = AppMatchKind.Squirrel, Target = @"C:\Users\x\AppData\Local\Discord", ExeName = "Discord.exe" },
            new TunneledApp { Enabled = true, Kind = AppMatchKind.ExactPath, Target = @"C:\Program Files (x86)\A B (c)\a+b.exe" },
            new TunneledApp { Enabled = true, Kind = AppMatchKind.Folder, Target = @"D:\Games\[Test] #1" },
        ], AppPaths.RuleSetFile);
        await AssertAccepted(SingBoxConfigBuilder.Build(TestEnv.Input(TestEnv.FakeProfile(v6Profile), v6Capture: v6Capture)));
    }

    [Fact]
    public async Task EmptyRuleSetIsAccepted()
    {
        RuleSetWriter.Write([], AppPaths.RuleSetFile);
        await AssertAccepted(SingBoxConfigBuilder.Build(TestEnv.Input(TestEnv.FakeProfile())));
    }

    [Fact]
    public void V6RejectOnlyWhenMachineHasV6AndVpnDoesNot()
    {
        var withReject = JsonNode.Parse(SingBoxConfigBuilder.Build(TestEnv.Input(TestEnv.FakeProfile(false), v6Capture: true)))!;
        Assert.Contains(withReject["route"]!["rules"]!.AsArray(), r => (string?)r!["action"] == "reject");
        var noReject = JsonNode.Parse(SingBoxConfigBuilder.Build(TestEnv.Input(TestEnv.FakeProfile(true), v6Capture: true)))!;
        Assert.DoesNotContain(noReject["route"]!["rules"]!.AsArray(), r => (string?)r!["action"] == "reject");
        var tun = noReject["inbounds"]!.AsArray().First(i => (string?)i!["type"] == "tun")!;
        Assert.Contains(tun["route_address"]!.AsArray(), a => (string?)a == "::/0");
    }

    /// <summary>Supervisor end to end in proxy-only mode against a VPN that never answers.</summary>
    [Fact]
    public async Task SupervisorStartsProbesAndStops()
    {
        var sb = TestEnv.SingBox;
        Assert.NotNull(sb);
        RuleSetWriter.Write([], AppPaths.RuleSetFile);
        using var sup = new TunnelSupervisor();
        var states = new List<TunnelState>();
        sup.StateChanged += (s, _) => { lock (states) states.Add(s); };
        int port = NetworkDetect.FindFreePort(18200);
        await sup.StartAsync(new TunnelStartOptions
        {
            Profile = TestEnv.FakeProfile(),
            SingBoxExe = sb!,
            ProxyPort = port,
            EnableTun = false,
            HandshakeProbes = 1,
        });
        Assert.Equal(TunnelState.Degraded, sup.State);
        Assert.NotNull(sup.Clash);
        Assert.True(await sup.Clash!.IsUpAsync());
        Assert.NotNull(await sup.Clash.GetConnectionsAsync());
        Assert.False(NetworkDetect.IsLocalPortFree(port));

        await sup.StopAsync();
        Assert.Equal(TunnelState.Stopped, sup.State);
        await Task.Delay(500);
        Assert.True(NetworkDetect.IsLocalPortFree(port));
        Assert.Equal([TunnelState.Starting, TunnelState.Degraded, TunnelState.Stopped], states);
    }

    [Fact]
    public async Task PortInUseIsReportedNotCrashed()
    {
        var sb = TestEnv.SingBox;
        Assert.NotNull(sb);
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        try
        {
            using var sup = new TunnelSupervisor();
            var ex = await Assert.ThrowsAsync<PortInUseException>(() => sup.StartAsync(new TunnelStartOptions
            {
                Profile = TestEnv.FakeProfile(), SingBoxExe = sb!, ProxyPort = port, EnableTun = false,
            }));
            Assert.Equal(port, ex.Port);
            Assert.Equal(TunnelState.Failed, sup.State);
        }
        finally
        {
            l.Stop();
        }
    }

    [Fact]
    public async Task MissingBinaryIsReported()
    {
        using var sup = new TunnelSupervisor();
        var ex = await Assert.ThrowsAsync<TunnelException>(() => sup.StartAsync(new TunnelStartOptions
        {
            Profile = TestEnv.FakeProfile(), SingBoxExe = @"C:\nope\sing-box.exe", ProxyPort = 1, EnableTun = false,
        }));
        Assert.Contains("não encontrado", ex.Message);
    }
}
