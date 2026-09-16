using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;

namespace MoonlightTunneling.Core;

/// <summary>
/// The only third-party requests the app makes: the public-IP lookup the user
/// asks for, and a tiny 204 probe through the VPN that doubles as health check.
/// </summary>
public static class IpCheck
{
    private static readonly string[] IpServices = ["https://api.ipify.org", "https://ipv4.icanhazip.com"];
    private const string ProbeUrl = "http://cp.cloudflare.com/generate_204";

    private static readonly HttpClient Direct = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly ConcurrentDictionary<int, HttpClient> Proxied = new();

    private static HttpClient ViaProxy(int port) => Proxied.GetOrAdd(port, p => new HttpClient(new SocketsHttpHandler
    {
        Proxy = new WebProxy($"http://127.0.0.1:{p}"),
        UseProxy = true,
        // Pooled connections through a dead tunnel would stall probes.
        PooledConnectionLifetime = TimeSpan.FromSeconds(30),
        ConnectTimeout = TimeSpan.FromSeconds(6),
    })
    { Timeout = TimeSpan.FromSeconds(10) });

    /// <summary>Public IP; through the local proxy (= VPN exit) when a port is given, otherwise the normal route.</summary>
    public static async Task<string?> GetPublicIpAsync(int? proxyPort)
    {
        var client = proxyPort is int p ? ViaProxy(p) : Direct;
        foreach (var url in IpServices)
        {
            try
            {
                var s = (await client.GetStringAsync(url)).Trim();
                if (IPAddress.TryParse(s, out _)) return s;
            }
            catch { }
        }
        return null;
    }

    /// <summary>Round trip of a 204 request through the VPN, in ms; null when the VPN doesn't answer.</summary>
    public static async Task<int?> MeasureLatencyAsync(int proxyPort, TimeSpan? timeout = null)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(8));
            var sw = Stopwatch.StartNew();
            using var resp = await ViaProxy(proxyPort).GetAsync(ProbeUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            sw.Stop();
            return resp.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.OK ? (int)sw.ElapsedMilliseconds : null;
        }
        catch
        {
            return null;
        }
    }
}
