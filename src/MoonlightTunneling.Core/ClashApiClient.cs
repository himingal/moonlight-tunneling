using System.Net.Http.Headers;
using System.Text.Json;

namespace MoonlightTunneling.Core;

public sealed record TunnelConnection(string Id, string? ProcessPath, bool ViaVpn, string Destination);

/// <summary>Client for sing-box's local Clash-compatible API (connection list / close).</summary>
public sealed class ClashApiClient : IDisposable
{
    private readonly HttpClient _http;

    public ClashApiClient(int port, string secret)
    {
        _http = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
            Timeout = TimeSpan.FromSeconds(5),
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
    }

    public async Task<bool> IsUpAsync()
    {
        try { return (await _http.GetAsync("version")).IsSuccessStatusCode; }
        catch { return false; }
    }

    public async Task<List<TunnelConnection>?> GetConnectionsAsync()
    {
        try
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync("connections"));
            var list = new List<TunnelConnection>();
            if (!doc.RootElement.TryGetProperty("connections", out var conns) || conns.ValueKind != JsonValueKind.Array)
                return list;
            foreach (var c in conns.EnumerateArray())
            {
                string id = c.GetProperty("id").GetString() ?? "";
                string? path = null, dest = "";
                if (c.TryGetProperty("metadata", out var md))
                {
                    if (md.TryGetProperty("processPath", out var pp)) path = pp.GetString();
                    var host = md.TryGetProperty("host", out var h) ? h.GetString() : null;
                    var ip = md.TryGetProperty("destinationIP", out var d) ? d.GetString() : null;
                    dest = string.IsNullOrEmpty(host) ? ip ?? "" : host;
                }
                bool vpn = false;
                if (c.TryGetProperty("chains", out var chains) && chains.ValueKind == JsonValueKind.Array)
                    vpn = chains.EnumerateArray().Any(x => x.GetString() == "vpn");
                list.Add(new TunnelConnection(id, string.IsNullOrEmpty(path) ? null : path, vpn, dest));
            }
            return list;
        }
        catch
        {
            return null;
        }
    }

    public async Task CloseAsync(string id)
    {
        try { await _http.DeleteAsync("connections/" + Uri.EscapeDataString(id)); }
        catch { }
    }

    public void Dispose() => _http.Dispose();
}
