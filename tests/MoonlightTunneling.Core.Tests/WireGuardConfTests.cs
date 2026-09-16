using MoonlightTunneling.Core;
using MoonlightTunneling.Platform;
using Xunit;

namespace MoonlightTunneling.Core.Tests;

public class WireGuardConfTests
{
    static WireGuardConfTests() => TestEnv.Init();

    private static readonly string Priv = TestEnv.Key(), Pub = TestEnv.Key();

    [Fact]
    public void ParsesProtonStyleExport()
    {
        var conf = $"""
            [Interface]
            # Key for test
            # Bouncing = 3
            PrivateKey = {Priv}
            Address = 10.2.0.2/32
            DNS = 10.2.0.1

            [Peer]
            # BR#12
            PublicKey = {Pub}
            AllowedIPs = 0.0.0.0/0, ::/0
            Endpoint = 146.70.98.2:51820
            """;
        var c = WireGuardConf.Parse(conf);
        Assert.Equal(Priv, c.PrivateKey);
        Assert.Equal(["10.2.0.2/32"], c.Addresses);
        Assert.Equal(["10.2.0.1"], c.Dns);
        Assert.Equal("146.70.98.2", c.EndpointHost);
        Assert.Equal(51820, c.EndpointPort);
    }

    [Fact]
    public void HandlesDualStackIPv6EndpointAndPsk()
    {
        var psk = TestEnv.Key();
        var conf = $"[Interface]\r\nPrivateKey={Priv}\r\nAddress = 10.64.1.2, fc00:bbbb::2\r\nMTU = 1380\r\n[Peer]\r\nPublicKey={Pub}\r\nPresharedKey = {psk}\r\nEndpoint = [2a03:1b20::1]:51820\r\n";
        var c = WireGuardConf.Parse(conf);
        Assert.Equal(["10.64.1.2/32", "fc00:bbbb::2/128"], c.Addresses);
        Assert.Equal("2a03:1b20::1", c.EndpointHost);
        Assert.Equal(1380, c.Mtu);
        Assert.Equal(psk, c.PresharedKey);
        var p = WireGuardConf.ToProfile(c, "x", null);
        Assert.True(p.HasIPv4 && p.HasIPv6);
        Assert.Equal(Priv, Dpapi.Unprotect(p.PrivateKeyProtected));
    }

    [Fact]
    public void RejectsIncompleteConf()
    {
        var ex = Assert.Throws<FormatException>(() => WireGuardConf.Parse("[Interface]\nAddress=10.0.0.2/32\n"));
        Assert.Contains("PrivateKey", ex.Message);
        Assert.Contains("Endpoint", ex.Message);
    }

    [Fact]
    public void RejectsBadKey()
    {
        Assert.Throws<FormatException>(() =>
            WireGuardConf.Parse($"[Interface]\nPrivateKey=abc\nAddress=10.0.0.2/32\n[Peer]\nPublicKey={Pub}\nEndpoint=1.2.3.4:51820\n"));
    }

    [Fact]
    public void HostnameEndpoint()
    {
        Assert.Equal(("nl.vpn.example.com", 443), WireGuardConf.ParseEndpoint("nl.vpn.example.com:443"));
    }
}
