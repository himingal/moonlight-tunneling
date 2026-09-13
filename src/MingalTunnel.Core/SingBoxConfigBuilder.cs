using System.Text.Json;
using System.Text.Json.Nodes;
using MingalTunnel.Platform;

namespace MingalTunnel.Core;

public sealed record SingBoxConfigInput
{
    public required VpnProfile Profile { get; init; }
    public required string PrivateKey { get; init; }
    public string? PresharedKey { get; init; }
    public bool EnableTun { get; init; } = true;
    public string? PhysicalInterface { get; init; }
    public bool CaptureIPv6 { get; init; }
    public IReadOnlyList<string> RouteExclude { get; init; } = [];
    public string DirectDns { get; init; } = "1.1.1.1";
    public string VpnDns { get; init; } = "1.1.1.1";
    public required int ProxyPort { get; init; }
    public required int ClashPort { get; init; }
    public required string ClashSecret { get; init; }
    public required string RuleSetPath { get; init; }
    public required string LogPath { get; init; }
}

/// <summary>
/// Generates the sing-box config. The shape carries over what discord-tunneling
/// v6 proved on real machines; each non-obvious choice is commented where it's made.
/// </summary>
public static class SingBoxConfigBuilder
{
    public const string RuleSetTag = "tunneled";

    public static string Build(SingBoxConfigInput i)
    {
        var p = i.Profile;
        bool v6InTunnel = p.HasIPv6;

        var root = new JsonObject
        {
            // "warn", never "info": at info sing-box logs one line per connection
            // and the TUN sees every connection on the machine (3 GB in hours).
            ["log"] = new JsonObject { ["level"] = "warn", ["output"] = i.LogPath, ["timestamp"] = true },
            ["dns"] = BuildDns(i, v6InTunnel),
            ["endpoints"] = new JsonArray { BuildWireGuard(i) },
            ["inbounds"] = BuildInbounds(i),
            ["outbounds"] = new JsonArray { BuildDirect(i) },
            ["route"] = BuildRoute(i, v6InTunnel),
            ["experimental"] = new JsonObject
            {
                // Local-only, secret-protected: feeds the per-app "in the tunnel
                // now" indicator and closes stale connections on "Reaplicar".
                ["clash_api"] = new JsonObject
                {
                    ["external_controller"] = $"127.0.0.1:{i.ClashPort}",
                    ["secret"] = i.ClashSecret,
                },
            },
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject BuildDns(SingBoxConfigInput i, bool v6InTunnel)
    {
        var vpnRule = new JsonObject
        {
            ["rule_set"] = new JsonArray { RuleSetTag },
            ["action"] = "route",
            ["server"] = "dns-vpn",
        };
        // AAAA answers the tunnel can't reach just make apps hang on dead connections.
        if (!v6InTunnel) vpnRule["strategy"] = "ipv4_only";

        return new JsonObject
        {
            ["servers"] = new JsonArray
            {
                // Explicit resolvers, never "local": with auto_route a "local"
                // server hands the query to the OS resolver, whose packet the TUN
                // captures again and re-resolves forever (SagerNet/sing-box#3637).
                // dns-direct uses the adapter's own DNS (router/ISP) so local
                // names keep resolving like they do without the tunnel.
                new JsonObject { ["type"] = "udp", ["tag"] = "dns-direct", ["server"] = i.DirectDns, ["server_port"] = 53, ["detour"] = "direct" },
                new JsonObject { ["type"] = "udp", ["tag"] = "dns-vpn", ["server"] = i.VpnDns, ["server_port"] = 53, ["detour"] = "vpn" },
            },
            ["rules"] = new JsonArray { vpnRule },
            ["final"] = "dns-direct",
        };
    }

    private static JsonObject BuildWireGuard(SingBoxConfigInput i)
    {
        var p = i.Profile;
        var allowed = new JsonArray();
        // Advertise exactly the families the VPN handed us. Claiming ::/0 on a
        // v4-only tunnel made Discord hang on "missing IPv6 local address".
        if (p.HasIPv4) allowed.Add("0.0.0.0/0");
        if (p.HasIPv6) allowed.Add("::/0");

        var peer = new JsonObject
        {
            ["address"] = p.EndpointHost,
            ["port"] = p.EndpointPort,
            ["public_key"] = p.PeerPublicKey,
            ["allowed_ips"] = allowed,
            ["persistent_keepalive_interval"] = 25,
        };
        if (i.PresharedKey != null) peer["pre_shared_key"] = i.PresharedKey;

        var addr = new JsonArray();
        foreach (var a in p.Addresses) addr.Add(a);

        return new JsonObject
        {
            ["type"] = "wireguard",
            ["tag"] = "vpn",
            ["system"] = false,
            ["address"] = addr,
            ["private_key"] = i.PrivateKey,
            ["mtu"] = p.Mtu ?? 1408,
            // Detour through "direct" instead of dialing on its own: an endpoint
            // bound to an interface fails on Windows when that adapter has IPv6
            // disabled (SagerNet/sing-box#2900).
            ["detour"] = "direct",
            ["peers"] = new JsonArray { peer },
        };
    }

    private static JsonArray BuildInbounds(SingBoxConfigInput i)
    {
        var inbounds = new JsonArray();
        if (i.EnableTun)
        {
            var addresses = new JsonArray { "172.19.0.1/30" };
            var routes = new JsonArray { "0.0.0.0/0" };
            if (i.CaptureIPv6)
            {
                addresses.Add("fdfe:dcba:9876::1/126");
                routes.Add("::/0");
            }
            var tun = new JsonObject
            {
                ["type"] = "tun",
                ["tag"] = "tun-in",
                ["interface_name"] = NetworkDetect.TunInterfaceName,
                ["address"] = addresses,
                ["mtu"] = 1400,
                ["auto_route"] = true,
                ["route_address"] = routes,
                // strict_route forces *everything* into the tunnel - the opposite
                // of a split tunnel - and on Windows breaks multihomed DNS.
                ["strict_route"] = false,
                ["stack"] = "system",
            };
            if (i.RouteExclude.Count > 0)
            {
                var ex = new JsonArray();
                foreach (var r in i.RouteExclude) ex.Add(r);
                tun["route_exclude_address"] = ex;
            }
            inbounds.Add(tun);
        }
        inbounds.Add(new JsonObject
        {
            ["type"] = "mixed",
            ["tag"] = "mixed-in",
            ["listen"] = "127.0.0.1",
            ["listen_port"] = i.ProxyPort,
        });
        return inbounds;
    }

    private static JsonObject BuildDirect(SingBoxConfigInput i)
    {
        // With auto_route the TUN is the default route, so the direct outbound
        // (everything not tunneled) must be pinned to the physical adapter or
        // it loops back into the TUN and the whole PC goes offline.
        // domain_resolver also keeps sing-box from rejecting a detour into an
        // outbound with no dial fields when no interface is known.
        var direct = new JsonObject { ["type"] = "direct", ["tag"] = "direct", ["domain_resolver"] = "dns-direct" };
        if (i.PhysicalInterface != null) direct["bind_interface"] = i.PhysicalInterface;
        return direct;
    }

    private static JsonObject BuildRoute(SingBoxConfigInput i, bool v6InTunnel)
    {
        var rules = new JsonArray
        {
            // The TUN's DNS address lives inside the TUN subnet; without the
            // hijack every lookup is rejected as a loopback into the TUN range.
            // Matched by port rather than sniffing, so ordinary traffic (games,
            // streams) never waits on a sniffer.
            new JsonObject { ["port"] = 53, ["action"] = "hijack-dns" },
            // The local proxy always exits through the VPN: that's what "verify
            // exit IP" and the latency probe measure.
            new JsonObject
            {
                ["inbound"] = new JsonArray { "mixed-in" },
                ["action"] = "resolve",
                ["server"] = "dns-vpn",
                ["strategy"] = v6InTunnel ? "prefer_ipv4" : "ipv4_only",
            },
            new JsonObject { ["inbound"] = new JsonArray { "mixed-in" }, ["action"] = "route", ["outbound"] = "vpn" },
        };
        if (i.CaptureIPv6 && !v6InTunnel)
        {
            // IPv6 policy: the machine has v6 but the VPN doesn't. Tunneled apps'
            // v6 is refused outright (apps fall back to v4 at once) instead of
            // either leaking around the tunnel or hanging on a dead route.
            rules.Add(new JsonObject
            {
                ["rule_set"] = new JsonArray { RuleSetTag },
                ["ip_version"] = 6,
                ["action"] = "reject",
            });
        }
        rules.Add(new JsonObject
        {
            ["rule_set"] = new JsonArray { RuleSetTag },
            ["action"] = "route",
            ["outbound"] = "vpn",
        });

        return new JsonObject
        {
            ["rule_set"] = new JsonArray
            {
                new JsonObject { ["type"] = "local", ["tag"] = RuleSetTag, ["format"] = "source", ["path"] = i.RuleSetPath },
            },
            ["rules"] = rules,
            // Process lookups for every connection: required for rules that only
            // live inside a hot-reloaded rule-set, and for the live indicator.
            ["find_process"] = true,
            ["default_domain_resolver"] = "dns-direct",
            ["final"] = "direct",
        };
    }
}
