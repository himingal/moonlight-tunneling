using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;

namespace MoonlightTunneling.Core;

/// <summary>
/// The only request the app makes on its own: a tiny 204 probe sent through the
/// VPN, used as the health check and the latency figure.
/// </summary>
public static class IpCheck
{
    private const string ProbeUrl = "http://cp.cloudflare.com/generate_204";

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
