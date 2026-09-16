using System.Security.Cryptography;
using MoonlightTunneling.Core;
using MoonlightTunneling.Platform;

namespace MoonlightTunneling.Core.Tests;

internal static class TestEnv
{
    static TestEnv()
    {
        AppPaths.DataDir = Path.Combine(Path.GetTempPath(), "MoonlightTunnelingTests-" + Environment.ProcessId);
        AppPaths.EnsureCreated();
    }

    public static void Init() { }

    public static string? SingBox => SingBoxBinary.Locate(null);

    public static string Key() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>A profile pointing at TEST-NET (never answers), so tests never touch a real VPN.</summary>
    public static VpnProfile FakeProfile(bool v6 = false) => new()
    {
        Name = "fake",
        Addresses = v6 ? ["10.2.0.2/32", "2a07:b944::2:2/128"] : ["10.2.0.2/32"],
        PrivateKeyProtected = Dpapi.Protect(Key()),
        PeerPublicKey = Key(),
        EndpointHost = "192.0.2.1",
        EndpointPort = 51820,
        Dns = ["10.2.0.1"],
    };

    public static SingBoxConfigInput Input(VpnProfile p, bool tun = true, bool v6Capture = false) => new()
    {
        Profile = p,
        PrivateKey = Dpapi.Unprotect(p.PrivateKeyProtected),
        EnableTun = tun,
        PhysicalInterface = tun ? "Ethernet" : null,
        CaptureIPv6 = v6Capture,
        RouteExclude = ["192.0.2.1/32"],
        ProxyPort = 18080,
        ClashPort = 18081,
        ClashSecret = "secret",
        RuleSetPath = AppPaths.RuleSetFile,
        LogPath = AppPaths.SingBoxLog,
    };
}
