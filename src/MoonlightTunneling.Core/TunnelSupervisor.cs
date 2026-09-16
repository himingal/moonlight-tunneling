using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MoonlightTunneling.Platform;

namespace MoonlightTunneling.Core;

public enum TunnelState { Stopped, Starting, Connected, Degraded, Reconnecting, Failed }

public sealed class TunnelStartOptions
{
    public required VpnProfile Profile { get; init; }
    public required string SingBoxExe { get; init; }
    public required int ProxyPort { get; init; }
    public bool EnableTun { get; init; } = true;
    public bool AutoReconnect { get; init; } = true;
    public int MaxReconnectAttempts { get; init; } = 5;
    /// <summary>How many times to probe the VPN right after start before settling for "degraded".</summary>
    public int HandshakeProbes { get; init; } = 8;
}

public class TunnelException(string message) : Exception(message);

public sealed class PortInUseException(int port) : TunnelException($"Local port {port} is already in use by another program.")
{
    public int Port { get; } = port;
}

public sealed class ForeignSingBoxException(IReadOnlyList<ProcessEntry> processes)
    : TunnelException("Another sing-box is already running: " + string.Join(", ", processes.Select(p => p.Path)))
{
    public IReadOnlyList<ProcessEntry> Processes { get; } = processes;
}

/// <summary>
/// Owns the sing-box child process: validated start, connectivity rollback,
/// health probing, crash detection and bounded auto-reconnect.
/// </summary>
public sealed partial class TunnelSupervisor : IDisposable
{
    private static readonly int[] BackoffSeconds = [2, 5, 10, 20, 30];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JobObject _job = new();
    private readonly List<string> _stderrTail = [];
    private TunnelStartOptions? _opts;
    private Process? _proc;
    private CancellationTokenSource? _healthCts;
    private CancellationTokenSource _reconnectCts = new();
    private volatile bool _stopping;
    private int _reconnectAttempts;
    private int _probeFailures;
    private DateTime _connectedSince;
    private long _logOffset;
    private Timer? _netDebounce;
    private string? _lastLogLine;

    public event Action<TunnelState, string>? StateChanged;
    public event Action<int?>? LatencyChanged;

    public TunnelState State { get; private set; } = TunnelState.Stopped;
    public string Detail { get; private set; } = "Off";
    public ClashApiClient? Clash { get; private set; }
    public int ProxyPort { get; private set; }
    public string? BoundInterface { get; private set; }
    public bool IsTunUp => State is TunnelState.Connected or TunnelState.Degraded;
    public bool IsActive => State is TunnelState.Starting or TunnelState.Connected or TunnelState.Degraded or TunnelState.Reconnecting;

    public TunnelSupervisor()
    {
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
    }

    private void SetState(TunnelState s, string detail)
    {
        State = s;
        Detail = detail;
        StateChanged?.Invoke(s, detail);
    }

    public async Task StartAsync(TunnelStartOptions opts)
    {
        _stopping = false;
        _reconnectCts = new CancellationTokenSource();
        await _gate.WaitAsync();
        try
        {
            _opts = opts;
            _reconnectAttempts = 0;
            await StartCoreAsync(reconnecting: false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        _stopping = true;
        _reconnectCts.Cancel();
        _healthCts?.Cancel();
        await _gate.WaitAsync();
        try
        {
            KillProcess();
            SetState(TunnelState.Stopped, "Off");
            AppLog.Info("Tunnel turned off.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StartCoreAsync(bool reconnecting)
    {
        var o = _opts!;
        if (!reconnecting) SetState(TunnelState.Starting, "Starting…");
        try
        {
            if (!File.Exists(o.SingBoxExe))
                throw new TunnelException($"sing-box.exe not found at {o.SingBoxExe}. Reinstall Moonlight Tunneling or point to another copy in Settings.");
            var version = await SingBoxBinary.GetVersionAsync(o.SingBoxExe)
                          ?? throw new TunnelException($"{o.SingBoxExe} failed to run. An antivirus may have blocked it.");
            if (version < SingBoxBinary.Minimum)
                throw new TunnelException($"sing-box {version} is too old (minimum {SingBoxBinary.Minimum}).");
            if (o.EnableTun && !Elevation.IsAdministrator())
                throw new TunnelException("Moonlight Tunneling must run as Administrator to create the virtual network adapter (TUN).");

            var foreign = ProcessPaths.Snapshot()
                .Where(p => Path.GetFileName(p.Path).Equals("sing-box.exe", StringComparison.OrdinalIgnoreCase) && p.Pid != _proc?.Id)
                .ToList();
            if (foreign.Count > 0 && o.EnableTun) throw new ForeignSingBoxException(foreign);
            if (!NetworkDetect.IsLocalPortFree(o.ProxyPort)) throw new PortInUseException(o.ProxyPort);

            var phys = NetworkDetect.GetPhysicalInterface(BoundInterface)
                       ?? throw new TunnelException("No active network connection found (Ethernet/Wi-Fi).");
            BoundInterface = phys.Name;

            var p = o.Profile;
            var input = new SingBoxConfigInput
            {
                Profile = p,
                PrivateKey = Dpapi.Unprotect(p.PrivateKeyProtected),
                PresharedKey = p.PresharedKeyProtected != null ? Dpapi.Unprotect(p.PresharedKeyProtected) : null,
                EnableTun = o.EnableTun,
                PhysicalInterface = o.EnableTun ? phys.Name : null,
                CaptureIPv6 = o.EnableTun && NetworkDetect.HasGlobalIPv6(phys),
                RouteExclude = await ResolveEndpointExcludesAsync(p.EndpointHost),
                DirectDns = NetworkDetect.GetIPv4Dns(phys)?.ToString() ?? "1.1.1.1",
                VpnDns = p.Dns.FirstOrDefault(d => !d.Contains(':')) ?? "1.1.1.1",
                ProxyPort = o.ProxyPort,
                ClashPort = NetworkDetect.GetEphemeralPort(),
                ClashSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
                RuleSetPath = AppPaths.RuleSetFile,
                LogPath = AppPaths.SingBoxLog,
            };
            if (!File.Exists(AppPaths.RuleSetFile)) RuleSetWriter.Write([], AppPaths.RuleSetFile);
            var json = SingBoxConfigBuilder.Build(input);

            // Validate first: a config error then reads as a clear message
            // instead of a process that died for unknown reasons.
            var check = await ProcessRunner.RunAsync(o.SingBoxExe, "check -c stdin", json, AppPaths.RuntimeDir);
            if (check.ExitCode != 0)
                throw new TunnelException("sing-box rejected the configuration: " + Clean(check.Combined));

            if (o.EnableTun) await WaitForStaleAdapterAsync();
            ResetOversizedLog();
            _logOffset = File.Exists(AppPaths.SingBoxLog) ? new FileInfo(AppPaths.SingBoxLog).Length : 0;
            Launch(o.SingBoxExe, json);
            AppLog.Info($"sing-box {version} started (normal traffic via \"{phys.Name}\"{(input.CaptureIPv6 ? ", IPv6 captured" : "")}).");

            if (o.EnableTun)
            {
                if (!await WaitForTunAsync(TimeSpan.FromSeconds(40)))
                    throw new TunnelException(FriendlyExit("The TUN adapter did not come up."));
                if (!await VerifyConnectivityAsync())
                {
                    KillProcess();
                    throw new TunnelException("The tunnel came up but this PC lost internet access, so it was turned off automatically and your normal connection was restored.");
                }
            }

            Clash?.Dispose();
            Clash = new ClashApiClient(input.ClashPort, input.ClashSecret);
            ProxyPort = o.ProxyPort;

            int? ms = null;
            for (int i = 0; i < o.HandshakeProbes && ms == null; i++)
            {
                if (_proc == null || _proc.HasExited) throw new TunnelException(FriendlyExit("sing-box stopped right after starting."));
                ms = await IpCheck.MeasureLatencyAsync(ProxyPort, TimeSpan.FromSeconds(5));
                if (ms == null) await Task.Delay(1500);
            }
            LatencyChanged?.Invoke(ms);
            _probeFailures = 0;
            _connectedSince = DateTime.UtcNow;
            if (ms != null)
            {
                SetState(TunnelState.Connected, $"Connected · {p.Name}");
                AppLog.Success($"Tunnel connected ({p.Name}, {ms} ms).");
            }
            else
            {
                SetState(TunnelState.Degraded, "Tunnel is up, but the VPN server hasn't answered yet");
                AppLog.Warn("Tunnel is up, but the VPN server didn't answer the probe. Check that the .conf is still valid.");
            }
            StartHealthLoop();
        }
        catch (Exception ex)
        {
            KillProcess();
            if (!reconnecting)
            {
                SetState(TunnelState.Failed, ex.Message);
                AppLog.Error(ex.Message);
            }
            throw;
        }
    }

    private void Launch(string exe, string json)
    {
        lock (_stderrTail) _stderrTail.Clear();
        var psi = new ProcessStartInfo(exe, "run -c stdin")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            // Go writes UTF-8; the default (OEM code page) mangles localized Windows errors.
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = AppPaths.RuntimeDir,
        };
        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.ErrorDataReceived += (_, e) => CaptureOutput(e.Data);
        proc.OutputDataReceived += (_, e) => CaptureOutput(e.Data);
        proc.Exited += OnProcessExited;
        proc.Start();
        try { _job.Add(proc); } catch (Exception ex) { AppLog.Warn("Couldn't tie sing-box to the app's lifetime: " + ex.Message); }
        proc.BeginErrorReadLine();
        proc.BeginOutputReadLine();
        // The config (with the private key) goes over stdin and never touches disk.
        _proc = proc;
        try
        {
            var bytes = new UTF8Encoding(false).GetBytes(json);
            proc.StandardInput.BaseStream.Write(bytes);
            proc.StandardInput.Close();
        }
        catch (IOException)
        {
            // Died before reading its config; the exit path reports why.
        }
    }

    private void CaptureOutput(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_stderrTail)
        {
            _stderrTail.Add(Clean(line));
            if (_stderrTail.Count > 30) _stderrTail.RemoveAt(0);
        }
    }

    private void KillProcess()
    {
        _healthCts?.Cancel();
        var p = _proc;
        _proc = null; // makes OnProcessExited ignore this exit
        if (p == null) return;
        try
        {
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
            }
        }
        catch { }
        p.Dispose();
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (_stopping || !ReferenceEquals(sender, _proc)) return;
        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync();
            try
            {
                if (_stopping || !ReferenceEquals(sender, _proc)) return;
                int code = -1;
                try { code = ((Process)sender!).ExitCode; } catch { }
                var reason = FriendlyExit($"sing-box stopped unexpectedly (exit code {code}).");
                if (code == 1 && reason.EndsWith(").", StringComparison.Ordinal))
                    reason += " No error was reported, so it was most likely closed from outside (Task Manager or another tool).";
                _proc = null;
                AppLog.Error(reason);
                await ReconnectLoopAsync(reason);
            }
            finally
            {
                _gate.Release();
            }
        });
    }

    /// <summary>Caller holds _gate.</summary>
    private async Task ReconnectLoopAsync(string reason)
    {
        var o = _opts;
        while (o != null && !_stopping)
        {
            if (!o.AutoReconnect || _reconnectAttempts >= o.MaxReconnectAttempts)
            {
                var msg = o.AutoReconnect
                    ? $"The tunnel dropped and didn't come back after {_reconnectAttempts} attempts. {reason}"
                    : $"The tunnel dropped. {reason}";
                SetState(TunnelState.Failed, msg);
                AppLog.Error(msg);
                return;
            }
            _reconnectAttempts++;
            int delay = BackoffSeconds[Math.Min(_reconnectAttempts - 1, BackoffSeconds.Length - 1)];
            SetState(TunnelState.Reconnecting, $"Reconnecting ({_reconnectAttempts}/{o.MaxReconnectAttempts}) in {delay}s…");
            AppLog.Warn($"Reconnecting in {delay}s (attempt {_reconnectAttempts}/{o.MaxReconnectAttempts})…");
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delay), _reconnectCts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            try
            {
                await StartCoreAsync(reconnecting: true);
                return;
            }
            catch (Exception ex) when (ex is PortInUseException or ForeignSingBoxException)
            {
                SetState(TunnelState.Failed, ex.Message);
                AppLog.Error(ex.Message);
                return;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                AppLog.Warn("Reconnect attempt failed: " + ex.Message);
            }
        }
    }

    private async Task RestartAsync(string reason, bool countsAsAttempt)
    {
        await _gate.WaitAsync();
        try
        {
            if (_stopping || _opts == null) return;
            AppLog.Warn(reason);
            KillProcess();
            if (!countsAsAttempt) _reconnectAttempts = 0;
            await ReconnectLoopAsync(reason);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void StartHealthLoop()
    {
        _healthCts?.Cancel();
        var cts = new CancellationTokenSource();
        _healthCts = cts;
        _ = Task.Run(async () =>
        {
            var ct = cts.Token;
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(20), ct); }
                catch (OperationCanceledException) { return; }
                if (_proc == null || _proc.HasExited) continue;

                PumpSingBoxLog();
                var ms = await IpCheck.MeasureLatencyAsync(ProxyPort);
                if (ct.IsCancellationRequested) return;
                LatencyChanged?.Invoke(ms);
                if (ms != null)
                {
                    _probeFailures = 0;
                    if (State != TunnelState.Connected)
                    {
                        SetState(TunnelState.Connected, $"Connected · {_opts?.Profile.Name}");
                        AppLog.Success("The VPN server is answering again.");
                    }
                    // Only a tunnel that stayed up a while earns back its retries;
                    // otherwise a crash loop right after each connect would never end.
                    if (DateTime.UtcNow - _connectedSince > TimeSpan.FromMinutes(2)) _reconnectAttempts = 0;
                }
                else
                {
                    _probeFailures++;
                    if (State == TunnelState.Connected)
                    {
                        SetState(TunnelState.Degraded, "VPN server not responding");
                        AppLog.Warn("The VPN server didn't answer the health probe.");
                    }
                    if (_probeFailures >= 3)
                    {
                        _ = RestartAsync("The VPN has been unresponsive for about a minute; restarting the tunnel.", countsAsAttempt: true);
                        return;
                    }
                }
            }
        }, cts.Token);
    }

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        if (!IsTunUp) return;
        _netDebounce?.Dispose();
        _netDebounce = new Timer(_ =>
        {
            if (!IsTunUp || _stopping) return;
            var phys = NetworkDetect.GetPhysicalInterface(BoundInterface);
            if (phys == null)
            {
                SetState(TunnelState.Degraded, "No physical network connection");
                return;
            }
            if (!string.Equals(phys.Name, BoundInterface, StringComparison.Ordinal))
            {
                // The direct outbound is pinned to the old adapter; rebuild on the new one.
                BoundInterface = phys.Name;
                _ = RestartAsync($"Network changed to \"{phys.Name}\"; restarting the tunnel on it.", countsAsAttempt: false);
            }
        }, null, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// After a hard kill the Wintun adapter can linger for a few seconds; a new
    /// sing-box started in that window fails with "create adapter: file already
    /// exists | open existing adapter: element not found".
    /// </summary>
    private static async Task WaitForStaleAdapterAsync()
    {
        for (int i = 0; i < 16; i++)
        {
            bool present = NetworkInterface.GetAllNetworkInterfaces()
                .Any(n => n.Name.Equals(NetworkDetect.TunInterfaceName, StringComparison.OrdinalIgnoreCase));
            if (!present) return;
            await Task.Delay(500);
        }
    }

    private async Task<bool> WaitForTunAsync(TimeSpan timeout)
    {
        // Opening the adapter can take a long while on Windows; probing before
        // it settles reads as a dead tunnel and rolls back a slow-but-fine start.
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_proc == null || _proc.HasExited) return false;
            if (NetworkDetect.IsOurTunUp())
            {
                await Task.Delay(2500); // routing table catches up
                return true;
            }
            await Task.Delay(500);
        }
        return false;
    }

    private async Task<bool> VerifyConnectivityAsync()
    {
        // Both halves fail differently: TCP proves non-tunneled traffic still
        // escapes to the physical adapter; DNS proves lookups don't loop into the TUN.
        for (int i = 0; i < 6; i++)
        {
            if (_proc == null || _proc.HasExited) return false;
            if (await NetworkDetect.CanReachInternetAsync(TimeSpan.FromSeconds(4)) &&
                await NetworkDetect.CanResolveAsync(TimeSpan.FromSeconds(4)))
                return true;
            await Task.Delay(3000);
        }
        return false;
    }

    private static async Task<IReadOnlyList<string>> ResolveEndpointExcludesAsync(string host)
    {
        // Keep the VPN server's own address out of the TUN, as a second line
        // of defence next to the bound direct outbound.
        if (IPAddress.TryParse(host, out var ip))
            return [ip.AddressFamily == AddressFamily.InterNetworkV6 ? $"{ip}/128" : $"{ip}/32"];
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var addrs = await Dns.GetHostAddressesAsync(host, cts.Token);
            return addrs.Select(a => a.AddressFamily == AddressFamily.InterNetworkV6 ? $"{a}/128" : $"{a}/32").ToList();
        }
        catch
        {
            return [];
        }
    }

    private static void ResetOversizedLog()
    {
        try
        {
            var fi = new FileInfo(AppPaths.SingBoxLog);
            if (fi.Exists && fi.Length > 10 * 1024 * 1024) fi.Delete();
        }
        catch { }
    }

    /// <summary>
    /// Per-connection close errors ("connection upload closed … forcibly
    /// closed by the remote host") are sing-box noting ordinary resets; they
    /// are not tunnel problems and would bury the lines that are.
    /// </summary>
    [GeneratedRegex(@"connection: connection (upload|download) closed|open interface take too much time")]
    private static partial Regex NoiseRx();

    private void PumpSingBoxLog()
    {
        try
        {
            if (!File.Exists(AppPaths.SingBoxLog)) return;
            using var fs = new FileStream(AppPaths.SingBoxLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length <= _logOffset) { _logOffset = Math.Min(_logOffset, fs.Length); return; }
            fs.Seek(_logOffset, SeekOrigin.Begin);
            var buf = new byte[Math.Min(fs.Length - _logOffset, 256 * 1024)];
            int n = fs.Read(buf, 0, buf.Length);
            _logOffset += n;
            int shown = 0;
            foreach (var raw in Encoding.UTF8.GetString(buf, 0, n).Split('\n'))
            {
                var line = Clean(raw);
                if (line.Length == 0 || !(line.Contains("ERROR") || line.Contains("FATAL") || line.Contains("WARN"))) continue;
                if (NoiseRx().IsMatch(line)) continue;
                var body = TimestampRx().Replace(line, "");
                if (body == _lastLogLine) continue;
                _lastLogLine = body;
                if (++shown > 8) break;
                AppLog.Warn("sing-box: " + body);
            }
        }
        catch { }
    }

    private string FriendlyExit(string prefix)
    {
        string tail;
        lock (_stderrTail) tail = string.Join("\n", _stderrTail);
        PumpSingBoxLog();
        var t = tail.ToLowerInvariant();
        string hint =
            t.Contains("create adapter") || t.Contains("open existing adapter")
                ? "The previous virtual adapter was still being removed by Windows; retrying usually fixes it."
            : t.Contains("wintun") || t.Contains("configure tun") || t.Contains("open interface")
                ? "Couldn't create the TUN adapter (Wintun driver). Check that wintun.dll sits next to sing-box.exe and that no antivirus blocked it."
            : t.Contains("address already in use") || t.Contains("only one usage of each socket")
                ? "A local port is already in use; change the port in Settings."
            : t.Contains("missing default interface") || t.Contains("no such network interface")
                ? "The network wasn't ready yet."
            : t.Contains("access is denied") || t.Contains("acesso negado")
                ? "Access denied; Moonlight Tunneling must run as Administrator."
            : "";
        var last = tail.Split('\n').LastOrDefault(l => l.Contains("FATAL") || l.Contains("ERROR")) ?? tail.Split('\n').LastOrDefault() ?? "";
        return string.Join(" ", new[] { prefix, hint, string.IsNullOrWhiteSpace(last) ? "" : $"Details: {last}" }.Where(s => s.Length > 0));
    }

    [GeneratedRegex(@"\x1B\[[0-9;]*m")]
    private static partial Regex AnsiRx();

    [GeneratedRegex(@"^[+-]\d{4} \d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\s*")]
    private static partial Regex TimestampRx();

    private static string Clean(string s) => AnsiRx().Replace(s, "").Trim();

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        _stopping = true;
        _netDebounce?.Dispose();
        KillProcess();
        Clash?.Dispose();
        _job.Dispose();
    }
}
