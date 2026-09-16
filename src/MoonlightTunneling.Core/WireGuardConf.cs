using System.Net;
using MoonlightTunneling.Platform;

namespace MoonlightTunneling.Core;

public sealed record WireGuardConfig(
    string PrivateKey,
    IReadOnlyList<string> Addresses,
    IReadOnlyList<string> Dns,
    int? Mtu,
    string PeerPublicKey,
    string? PresharedKey,
    string EndpointHost,
    int EndpointPort);

/// <summary>Parser for standard WireGuard .conf exports (Proton, Mullvad, wg-quick…).</summary>
public static class WireGuardConf
{
    public static WireGuardConfig Parse(string text)
    {
        string? section = null;
        var iface = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var peer = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool sawPeer = false;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            int hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash].Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim().ToLowerInvariant();
                if (section == "peer")
                {
                    // Only the first peer is used; a split tunnel has one exit.
                    if (sawPeer) section = "ignored-peer";
                    sawPeer = true;
                }
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            var target = section switch { "interface" => iface, "peer" => peer, _ => null };
            if (target == null) continue;
            // Repeated keys (e.g. two Address lines) accumulate.
            target[key] = target.TryGetValue(key, out var prev) ? prev + "," + value : value;
        }

        var missing = new List<string>();
        if (!iface.ContainsKey("PrivateKey")) missing.Add("PrivateKey");
        if (!iface.ContainsKey("Address")) missing.Add("Address");
        if (!peer.ContainsKey("PublicKey")) missing.Add("PublicKey");
        if (!peer.ContainsKey("Endpoint")) missing.Add("Endpoint");
        if (missing.Count > 0)
            throw new FormatException("Incomplete .conf file, missing: " + string.Join(", ", missing));

        var privateKey = iface["PrivateKey"];
        var publicKey = peer["PublicKey"];
        ValidateKey(privateKey, "PrivateKey");
        ValidateKey(publicKey, "PublicKey");
        string? psk = peer.TryGetValue("PresharedKey", out var p) ? p : null;
        if (psk != null) ValidateKey(psk, "PresharedKey");

        var addresses = SplitList(iface["Address"]).Select(NormalizeCidr).ToList();
        var dns = iface.TryGetValue("DNS", out var d) ? SplitList(d).Where(x => IPAddress.TryParse(x, out _)).ToList() : [];
        int? mtu = iface.TryGetValue("MTU", out var m) && int.TryParse(m, out var mv) && mv is >= 1280 and <= 1500 ? mv : null;
        var (host, port) = ParseEndpoint(peer["Endpoint"]);

        return new WireGuardConfig(privateKey, addresses, dns, mtu, publicKey, psk, host, port);
    }

    public static VpnProfile ToProfile(WireGuardConfig c, string name, string? source) => new()
    {
        Name = name,
        Addresses = c.Addresses.ToList(),
        PrivateKeyProtected = Dpapi.Protect(c.PrivateKey),
        PeerPublicKey = c.PeerPublicKey,
        PresharedKeyProtected = c.PresharedKey != null ? Dpapi.Protect(c.PresharedKey) : null,
        EndpointHost = c.EndpointHost,
        EndpointPort = c.EndpointPort,
        Dns = c.Dns.ToList(),
        Mtu = c.Mtu,
        Source = source,
    };

    internal static (string host, int port) ParseEndpoint(string endpoint)
    {
        endpoint = endpoint.Trim();
        string host, portText;
        if (endpoint.StartsWith('['))
        {
            int close = endpoint.IndexOf(']');
            if (close < 0 || close + 2 > endpoint.Length || endpoint[close + 1] != ':')
                throw new FormatException($"Invalid Endpoint: {endpoint}");
            host = endpoint[1..close];
            portText = endpoint[(close + 2)..];
        }
        else
        {
            int colon = endpoint.LastIndexOf(':');
            if (colon <= 0) throw new FormatException($"Endpoint has no port: {endpoint}");
            host = endpoint[..colon];
            portText = endpoint[(colon + 1)..];
        }
        if (!int.TryParse(portText, out int port) || port is < 1 or > 65535)
            throw new FormatException($"Invalid port in Endpoint: {endpoint}");
        return (host, port);
    }

    private static string NormalizeCidr(string a)
    {
        if (a.Contains('/')) return a;
        return a.Contains(':') ? a + "/128" : a + "/32";
    }

    private static IEnumerable<string> SplitList(string v) =>
        v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void ValidateKey(string key, string field)
    {
        try
        {
            if (Convert.FromBase64String(key).Length == 32) return;
        }
        catch (FormatException) { }
        throw new FormatException($"{field} is not a valid WireGuard key.");
    }
}
