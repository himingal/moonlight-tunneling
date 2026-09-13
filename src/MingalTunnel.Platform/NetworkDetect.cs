using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MingalTunnel.Platform;

public static class NetworkDetect
{
    public const string TunInterfaceName = "MingalTunnel";

    private static bool IsTunnelAdapter(NetworkInterface ni) =>
        ni.Name.Equals(TunInterfaceName, StringComparison.OrdinalIgnoreCase) ||
        ni.Name.StartsWith("tun", StringComparison.OrdinalIgnoreCase) ||
        ni.Description.Contains("sing-tun", StringComparison.OrdinalIgnoreCase) ||
        ni.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase);

    private static bool HasIPv4Gateway(NetworkInterface ni)
    {
        try
        {
            return ni.GetIPProperties().GatewayAddresses.Any(g =>
                g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
        }
        catch { return false; }
    }

    private static IEnumerable<NetworkInterface> Candidates() =>
        NetworkInterface.GetAllNetworkInterfaces().Where(ni =>
            ni.OperationalStatus == OperationalStatus.Up &&
            ni.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) &&
            !IsTunnelAdapter(ni) &&
            HasIPv4Gateway(ni));

    /// <summary>
    /// The adapter that really carries internet traffic, never a TUN. Once
    /// auto_route is active the TUN *is* the default route, so asking Windows
    /// for "the best interface" would return the tunnel itself; sing-box's own
    /// auto_detect_interface has the same problem on Windows and binds the
    /// direct outbound to the tunnel ("loopback connection to TUN range").
    /// </summary>
    public static NetworkInterface? GetPhysicalInterface(string? preferName = null)
    {
        var candidates = Candidates().ToList();
        if (candidates.Count == 0) return null;

        if (preferName != null)
        {
            var same = candidates.FirstOrDefault(c => c.Name == preferName);
            if (same != null) return same;
        }

        // With no TUN up, Windows' own routing decision is authoritative.
        if (!IsOurTunUp())
        {
            var dest = BitConverter.ToUInt32(IPAddress.Parse("1.1.1.1").GetAddressBytes(), 0);
            if (Native.GetBestInterface(dest, out uint idx) == 0)
            {
                var best = candidates.FirstOrDefault(c =>
                {
                    try { return c.GetIPProperties().GetIPv4Properties()?.Index == idx; }
                    catch { return false; }
                });
                if (best != null) return best;
            }
        }

        return candidates
            .OrderBy(c => c.NetworkInterfaceType switch
            {
                NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet => 0,
                NetworkInterfaceType.Wireless80211 => 1,
                _ => 2,
            })
            .First();
    }

    public static bool IsOurTunUp() =>
        NetworkInterface.GetAllNetworkInterfaces().Any(ni =>
            ni.Name.Equals(TunInterfaceName, StringComparison.OrdinalIgnoreCase) &&
            ni.OperationalStatus == OperationalStatus.Up);

    /// <summary>
    /// True when the physical adapter has real IPv6 internet (a v6 gateway and a
    /// global address). Only then can tunneled apps leak over v6, so only then
    /// does the TUN need to capture ::/0.
    /// </summary>
    public static bool HasGlobalIPv6(NetworkInterface ni)
    {
        try
        {
            var props = ni.GetIPProperties();
            bool gw = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetworkV6);
            bool global = props.UnicastAddresses.Any(u =>
                u.Address.AddressFamily == AddressFamily.InterNetworkV6 &&
                !u.Address.IsIPv6LinkLocal && !u.Address.IsIPv6SiteLocal && !u.Address.IsIPv6UniqueLocal &&
                !IPAddress.IsLoopback(u.Address));
            return gw && global;
        }
        catch { return false; }
    }

    /// <summary>First IPv4 DNS server configured on the adapter (router/ISP), if any.</summary>
    public static IPAddress? GetIPv4Dns(NetworkInterface ni)
    {
        try
        {
            return ni.GetIPProperties().DnsAddresses.FirstOrDefault(a =>
                a.AddressFamily == AddressFamily.InterNetwork &&
                !a.ToString().StartsWith("172.19.", StringComparison.Ordinal));
        }
        catch { return null; }
    }

    public static async Task<bool> CanReachInternetAsync(TimeSpan timeout)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(timeout);
            await client.ConnectAsync("1.1.1.1", 443, cts.Token);
            return client.Connected;
        }
        catch { return false; }
    }

    public static async Task<bool> CanResolveAsync(TimeSpan timeout)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            var addrs = await Dns.GetHostAddressesAsync("cloudflare.com", cts.Token);
            return addrs.Length > 0;
        }
        catch { return false; }
    }

    public static bool IsLocalPortFree(int port)
    {
        try
        {
            var l = new TcpListener(IPAddress.Loopback, port);
            l.Start();
            l.Stop();
            return true;
        }
        catch { return false; }
    }

    public static int FindFreePort(int startingAt)
    {
        for (int p = startingAt; p < startingAt + 200 && p < 65535; p++)
            if (IsLocalPortFree(p)) return p;
        return GetEphemeralPort();
    }

    public static int GetEphemeralPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
