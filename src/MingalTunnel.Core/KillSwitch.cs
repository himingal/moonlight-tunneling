using MingalTunnel.Platform;

namespace MingalTunnel.Core;

/// <summary>
/// Opt-in per-app kill-switch. Rules exist whenever the TUN is not verified
/// up, and are only lifted once it is. They persist across reboots and app
/// exits on purpose: that's the window where traffic would otherwise leak.
/// Inside a running tunnel no firewall is needed: a tunneled app whose VPN is
/// down is still routed to the (dead) VPN, never to the normal connection.
/// </summary>
public sealed class KillSwitch
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HashSet<string>? _applied;
    private bool _warnedNoAdmin;

    public int EngagedCount => _applied?.Count ?? 0;

    public async Task SyncAsync(bool tunnelUp, IReadOnlyList<TunneledApp> apps)
    {
        var desired = tunnelUp
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : apps.Where(a => a.Enabled && a.KillSwitch).SelectMany(ResolveExePaths).ToHashSet(StringComparer.OrdinalIgnoreCase);

        await _gate.WaitAsync();
        try
        {
            if (_applied != null && _applied.SetEquals(desired)) return;
            if (!Elevation.IsAdministrator())
            {
                if (desired.Count > 0 && !_warnedNoAdmin)
                {
                    _warnedNoAdmin = true;
                    AppLog.Warn("Kill-switch precisa de Administrador; regras não aplicadas.");
                }
                return;
            }
            await FirewallRules.RemoveAllAsync();
            int ok = 0;
            foreach (var path in desired)
                if (await FirewallRules.BlockProgramAsync(path)) ok++;
            if (desired.Count > 0)
                AppLog.Warn($"Kill-switch ativo: {ok} executável(is) sem rede até o túnel voltar.");
            else if (_applied is { Count: > 0 })
                AppLog.Info("Kill-switch liberado (túnel ativo).");
            _applied = desired;
        }
        catch (Exception ex)
        {
            AppLog.Error("Falha ao aplicar kill-switch: " + ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Concrete executables behind an app entry (firewall rules need real paths, not patterns).</summary>
    public static IEnumerable<string> ResolveExePaths(TunneledApp a)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            switch (a.Kind)
            {
                case AppMatchKind.ExactPath:
                    set.Add(a.Target);
                    break;
                case AppMatchKind.Squirrel when Directory.Exists(a.Target) && a.ExeName != null:
                    foreach (var d in Directory.GetDirectories(a.Target, "app-*"))
                    {
                        var exe = Path.Combine(d, a.ExeName);
                        if (File.Exists(exe)) set.Add(exe);
                    }
                    break;
                case AppMatchKind.Folder when Directory.Exists(a.Target):
                    foreach (var exe in Directory.EnumerateFiles(a.Target, "*.exe", SearchOption.AllDirectories).Take(300))
                        set.Add(exe);
                    break;
            }
        }
        catch { }

        foreach (var s in a.SeenPaths) set.Add(s);
        if (a.Kind == AppMatchKind.Regex)
        {
            var rx = PathPattern.ToRegex(PathPattern.ForApp(a));
            foreach (var p in ProcessPaths.Snapshot())
                if (rx.IsMatch(p.Path)) set.Add(p.Path);
        }
        return set;
    }
}
