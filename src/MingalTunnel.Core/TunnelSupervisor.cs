using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MingalTunnel.Platform;

namespace MingalTunnel.Core;

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

public sealed class PortInUseException(int port) : TunnelException($"A porta local {port} já está em uso por outro programa.")
{
    public int Port { get; } = port;
}

public sealed class ForeignSingBoxException(IReadOnlyList<ProcessEntry> processes)
    : TunnelException("Já existe outro sing-box rodando: " + string.Join(", ", processes.Select(p => p.Path)))
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
    public string Detail { get; private set; } = "Desligado";
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
            SetState(TunnelState.Stopped, "Desligado");
            AppLog.Info("Túnel desligado.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StartCoreAsync(bool reconnecting)
    {
        var o = _opts!;
        if (!reconnecting) SetState(TunnelState.Starting, "Iniciando…");
        try
        {
            if (!File.Exists(o.SingBoxExe))
                throw new TunnelException($"sing-box.exe não encontrado em {o.SingBoxExe}. Reinstale o Mingal Tunnel ou aponte outro caminho em Configurações.");
            var version = await SingBoxBinary.GetVersionAsync(o.SingBoxExe)
                          ?? throw new TunnelException($"{o.SingBoxExe} não executou. Antivírus pode ter bloqueado o arquivo.");
            if (version < SingBoxBinary.Minimum)
                throw new TunnelException($"sing-box {version} é antigo demais (mínimo {SingBoxBinary.Minimum}).");
            if (o.EnableTun && !Elevation.IsAdministrator())
                throw new TunnelException("O Mingal Tunnel precisa rodar como Administrador pra criar o adaptador de rede virtual (TUN).");

            var foreign = ProcessPaths.Snapshot()
                .Where(p => Path.GetFileName(p.Path).Equals("sing-box.exe", StringComparison.OrdinalIgnoreCase) && p.Pid != _proc?.Id)
                .ToList();
            if (foreign.Count > 0 && o.EnableTun) throw new ForeignSingBoxException(foreign);
            if (!NetworkDetect.IsLocalPortFree(o.ProxyPort)) throw new PortInUseException(o.ProxyPort);

            var phys = NetworkDetect.GetPhysicalInterface(BoundInterface)
                       ?? throw new TunnelException("Nenhuma conexão de rede ativa encontrada (cabo/Wi-Fi).");
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
                throw new TunnelException("O sing-box recusou a configuração: " + Clean(check.Combined));

            ResetOversizedLog();
            _logOffset = File.Exists(AppPaths.SingBoxLog) ? new FileInfo(AppPaths.SingBoxLog).Length : 0;
            Launch(o.SingBoxExe, json);
            AppLog.Info($"sing-box {version} iniciado (saída normal por \"{phys.Name}\"{(input.CaptureIPv6 ? ", IPv6 capturado" : "")}).");

            if (o.EnableTun)
            {
                if (!await WaitForTunAsync(TimeSpan.FromSeconds(40)))
                    throw new TunnelException(FriendlyExit("O adaptador TUN não subiu."));
                if (!await VerifyConnectivityAsync())
                {
                    KillProcess();
                    throw new TunnelException("O túnel subiu mas o PC perdeu acesso à internet, então ele foi desligado automaticamente e sua conexão normal foi restaurada.");
                }
            }

            Clash?.Dispose();
            Clash = new ClashApiClient(input.ClashPort, input.ClashSecret);
            ProxyPort = o.ProxyPort;

            int? ms = null;
            for (int i = 0; i < o.HandshakeProbes && ms == null; i++)
            {
                if (_proc == null || _proc.HasExited) throw new TunnelException(FriendlyExit("O sing-box parou logo após iniciar."));
                ms = await IpCheck.MeasureLatencyAsync(ProxyPort, TimeSpan.FromSeconds(5));
                if (ms == null) await Task.Delay(1500);
            }
            LatencyChanged?.Invoke(ms);
            _probeFailures = 0;
            _connectedSince = DateTime.UtcNow;
            if (ms != null)
            {
                SetState(TunnelState.Connected, $"Conectado · {p.Name}");
                AppLog.Success($"Túnel conectado ({p.Name}, {ms} ms).");
            }
            else
            {
                SetState(TunnelState.Degraded, "Túnel ativo, mas o servidor VPN ainda não respondeu");
                AppLog.Warn("Túnel ativo, mas o servidor VPN não respondeu ao teste. Confira se o .conf ainda é válido.");
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
            WorkingDirectory = AppPaths.RuntimeDir,
        };
        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.ErrorDataReceived += (_, e) => CaptureOutput(e.Data);
        proc.OutputDataReceived += (_, e) => CaptureOutput(e.Data);
        proc.Exited += OnProcessExited;
        proc.Start();
        try { _job.Add(proc); } catch (Exception ex) { AppLog.Warn("Não consegui vincular o sing-box ao ciclo de vida do app: " + ex.Message); }
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
                var reason = FriendlyExit($"sing-box parou inesperadamente (código {code}).");
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
                    ? $"Túnel caiu e não voltou após {_reconnectAttempts} tentativas. {reason}"
                    : $"Túnel caiu. {reason}";
                SetState(TunnelState.Failed, msg);
                AppLog.Error(msg);
                return;
            }
            _reconnectAttempts++;
            int delay = BackoffSeconds[Math.Min(_reconnectAttempts - 1, BackoffSeconds.Length - 1)];
            SetState(TunnelState.Reconnecting, $"Reconectando ({_reconnectAttempts}/{o.MaxReconnectAttempts}) em {delay}s…");
            AppLog.Warn($"Reconectando em {delay}s (tentativa {_reconnectAttempts}/{o.MaxReconnectAttempts})…");
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
                AppLog.Warn("Tentativa de reconexão falhou: " + ex.Message);
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
                        SetState(TunnelState.Connected, $"Conectado · {_opts?.Profile.Name}");
                        AppLog.Success("Servidor VPN respondendo de novo.");
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
                        SetState(TunnelState.Degraded, "Servidor VPN sem resposta");
                        AppLog.Warn("Servidor VPN não respondeu ao teste de saúde.");
                    }
                    if (_probeFailures >= 3)
                    {
                        _ = RestartAsync("VPN sem resposta há cerca de 1 minuto; reiniciando o túnel.", countsAsAttempt: true);
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
                SetState(TunnelState.Degraded, "Sem conexão de rede física");
                return;
            }
            if (!string.Equals(phys.Name, BoundInterface, StringComparison.Ordinal))
            {
                // The direct outbound is pinned to the old adapter; rebuild on the new one.
                BoundInterface = phys.Name;
                _ = RestartAsync($"Rede mudou para \"{phys.Name}\"; reiniciando o túnel nela.", countsAsAttempt: false);
            }
        }, null, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
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
            t.Contains("wintun") || t.Contains("configure tun") || t.Contains("open interface")
                ? "Falha ao criar o adaptador TUN (driver wintun). Confira se o wintun.dll está junto do sing-box.exe e se o antivírus não bloqueou."
            : t.Contains("address already in use") || t.Contains("only one usage of each socket")
                ? "Uma porta local já está em uso; troque a porta em Configurações."
            : t.Contains("missing default interface") || t.Contains("no such network interface")
                ? "A rede ainda não estava pronta."
            : t.Contains("access is denied") || t.Contains("acesso negado")
                ? "Acesso negado; o Mingal Tunnel precisa rodar como Administrador."
            : "";
        var last = tail.Split('\n').LastOrDefault(l => l.Contains("FATAL") || l.Contains("ERROR")) ?? tail.Split('\n').LastOrDefault() ?? "";
        return string.Join(" ", new[] { prefix, hint, string.IsNullOrWhiteSpace(last) ? "" : $"Detalhe: {last}" }.Where(s => s.Length > 0));
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
        KillProcess();
        Clash?.Dispose();
        _job.Dispose();
    }
}
