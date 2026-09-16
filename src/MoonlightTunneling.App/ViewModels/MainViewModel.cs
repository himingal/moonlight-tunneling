using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using MoonlightTunneling.Core;
using MoonlightTunneling.Platform;

namespace MoonlightTunneling.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly Dispatcher _ui;
    private readonly TunnelSupervisor _tunnel = new();
    private readonly KillSwitch _killSwitch = new();
    private readonly DispatcherTimer _liveTimer;
    private readonly DispatcherTimer _saveTimer;
    private bool _windowVisible, _liveBusy, _hideAdminBanner;

    private string _statusTitle = "Off", _statusDetail = "The tunnel is off. No app goes through the VPN.";
    private Brush _statusBrush = Palette.Gray;
    private string _latencyText = "—";
    private string? _killSwitchBanner;
    private int _selectedTab;
    private ProfileItem? _activeProfile;
    private bool _autostart;
    private string _singBoxInfo = "";
    private CancellationTokenSource? _boostCts;
    private string? _boostStatus;
    private string _portHint = "";

    public MainViewModel(Dispatcher ui)
    {
        _ui = ui;
        Settings = JsonStore.LoadOrDefault<AppSettings>(AppPaths.SettingsFile);
        IsAdmin = Elevation.IsAdministrator();
        if (Settings.SchemaVersion < 2)
        {
            // 1.1: the boost hold default went from 60s to 20s; carry it over
            // unless it had been customised.
            if (Settings.BoostHoldSeconds == 60) Settings.BoostHoldSeconds = 20;
            Settings.SchemaVersion = 2;
        }

        _liveTimer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (_, _) => _ = RefreshLiveAsync(), ui);
        _liveTimer.Stop();
        // Normal priority: a busy UI must never starve settings saves.
        _saveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Normal, (_, _) => { _saveTimer!.Stop(); SaveNow(); }, ui);
        _saveTimer.Stop();

        _tunnel.StateChanged += (s, d) => _ui.BeginInvoke(() => OnTunnelState(s, d));
        _tunnel.LatencyChanged += ms => _ui.BeginInvoke(() => LatencyText = ms is int v ? $"{v} ms" : "no response");

        ToggleTunnelCommand = new AsyncCommand(ToggleTunnelAsync, () => _tunnel.State != TunnelState.Starting);
        AddAppCommand = new RelayCommand(AddApps);
        ReapplyCommand = new AsyncCommand(ReapplyAsync, () => _tunnel.IsTunUp);
        ImportProfileCommand = new RelayCommand(ImportProfiles);
        UseProfileCommand = new AsyncCommand(p => UseProfileAsync((ProfileItem)p!));
        DeleteProfileCommand = new RelayCommand(p => DeleteProfile((ProfileItem)p!));
        BrowseSingBoxCommand = new RelayCommand(BrowseSingBox);
        ResetSingBoxCommand = new RelayCommand(() => SingBoxPath = "");
        OpenDataFolderCommand = new RelayCommand(() => OpenFolder(AppPaths.DataDir));
        OpenLogsCommand = new RelayCommand(() => OpenFolder(AppPaths.LogsDir));
        CopyLogCommand = new RelayCommand(() => { try { Clipboard.SetText(string.Join(Environment.NewLine, Log)); } catch { } });
        ClearLogCommand = new RelayCommand(() => Log.Clear());
        BoostCommand = new AsyncCommand(() => BoostAsync(interactive: true), () => !IsBoosting);
        BoostReleaseCommand = new RelayCommand(() => _boostCts?.Cancel(), () => IsBoosting);
        ProtonConfLinkCommand = new RelayCommand(() => Process.Start(new ProcessStartInfo("https://account.protonvpn.com/downloads") { UseShellExecute = true }));
    }

    public Func<Window?> OwnerProvider { get; set; } = () => null;
    public event Action<string, string>? Notify;

    public AppSettings Settings { get; }
    public bool IsAdmin { get; }
    public bool IsNotAdmin => !IsAdmin && !_hideAdminBanner;
    public ObservableCollection<AppRowViewModel> Apps { get; } = [];
    public ObservableCollection<ProfileItem> Profiles { get; } = [];
    public ObservableCollection<LogLine> Log { get; } = [];

    public ICommand ToggleTunnelCommand { get; }
    public ICommand AddAppCommand { get; }
    public ICommand ReapplyCommand { get; }
    public ICommand ImportProfileCommand { get; }
    public ICommand UseProfileCommand { get; }
    public ICommand DeleteProfileCommand { get; }
    public ICommand BrowseSingBoxCommand { get; }
    public ICommand ResetSingBoxCommand { get; }
    public ICommand OpenDataFolderCommand { get; }
    public ICommand OpenLogsCommand { get; }
    public ICommand CopyLogCommand { get; }
    public ICommand ClearLogCommand { get; }
    public ICommand ProtonConfLinkCommand { get; }
    public ICommand BoostCommand { get; }
    public ICommand BoostReleaseCommand { get; }

    // ---------- status ----------
    public bool IsTunnelActive => _tunnel.IsActive;
    public string ToggleText => _tunnel.State == TunnelState.Starting ? "Connecting…" : _tunnel.IsActive ? "Turn tunnel off" : "Turn tunnel on";
    public string StatusTitle { get => _statusTitle; private set => Set(ref _statusTitle, value); }
    public string StatusDetail { get => _statusDetail; private set => Set(ref _statusDetail, value); }
    public Brush StatusBrush { get => _statusBrush; private set => Set(ref _statusBrush, value); }
    public string LatencyText { get => _latencyText; private set => Set(ref _latencyText, value); }
    public string ProxyText => $"local proxy 127.0.0.1:{Settings.ProxyPort}";
    public string? KillSwitchBanner { get => _killSwitchBanner; private set { if (Set(ref _killSwitchBanner, value)) OnPropertyChanged(nameof(HasKillSwitchBanner)); } }
    public bool HasKillSwitchBanner => _killSwitchBanner != null;
    public int SelectedTab { get => _selectedTab; set => Set(ref _selectedTab, value); }
    public bool HasNoProfiles => Profiles.Count == 0;
    public string SingBoxInfo { get => _singBoxInfo; private set => Set(ref _singBoxInfo, value); }

    public ProfileItem? ActiveProfile
    {
        get => _activeProfile;
        private set
        {
            _activeProfile = value;
            foreach (var p in Profiles) p.IsActive = ReferenceEquals(p, value);
            Settings.ActiveProfileId = value?.Model.Id;
            SaveNow();
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActiveProfileName));
        }
    }

    public string ActiveProfileName => _activeProfile?.Name ?? "no profile";

    // ---------- settings ----------
    public bool AutostartEnabled
    {
        get => _autostart;
        set
        {
            if (_autostart == value) return;
            _autostart = value;
            OnPropertyChanged();
            _ = ApplyAutostartAsync(value);
        }
    }

    public bool AutoConnectOnLaunch { get => Settings.AutoConnectOnLaunch; set { Settings.AutoConnectOnLaunch = value; OnPropertyChanged(); QueueSave(); } }
    public bool AutoReconnect { get => Settings.AutoReconnect; set { Settings.AutoReconnect = value; OnPropertyChanged(); QueueSave(); } }
    public bool MinimizeToTray { get => Settings.MinimizeToTray; set { Settings.MinimizeToTray = value; OnPropertyChanged(); QueueSave(); } }

    public string MaxReconnectText
    {
        get => Settings.MaxReconnectAttempts.ToString();
        set
        {
            if (int.TryParse(value, out int n) && n is >= 1 and <= 50)
            {
                Settings.MaxReconnectAttempts = n;
                QueueSave();
            }
            OnPropertyChanged();
        }
    }

    public string ProxyPortText
    {
        get => Settings.ProxyPort.ToString();
        set
        {
            if (int.TryParse(value, out int n) && n is >= 1024 and <= 65535 && n != Settings.ProxyPort)
            {
                Settings.ProxyPort = n;
                QueueSave();
                OnPropertyChanged(nameof(ProxyText));
                UpdatePortHint();
                if (_tunnel.IsActive) AppLog.Info($"Local port is now {n}; it applies from the next tunnel connection.");
            }
            OnPropertyChanged();
        }
    }

    public string PortHint { get => _portHint; private set => Set(ref _portHint, value); }

    // ---------- Discord Go Live boost ----------
    public bool IsBoosting => _boostCts != null;
    public bool IsNotBoosting => _boostCts == null;
    public bool HasDiscord => Apps.Any(IsDiscord);

    public string BoostText => _boostStatus ??
        $"Discord only needs the VPN while it signs in. Boost reopens Discord through the tunnel, holds it for {Settings.BoostHoldSeconds}s, then hands it back to your normal connection - Go Live keeps working, at full speed.";

    public string BoostHoldText
    {
        get => Settings.BoostHoldSeconds.ToString();
        set
        {
            if (int.TryParse(value, out int n) && n is >= 5 and <= 600)
            {
                Settings.BoostHoldSeconds = n;
                QueueSave();
                OnPropertyChanged(nameof(BoostText));
            }
            OnPropertyChanged();
        }
    }

    public bool BoostStopTunnelAfter { get => Settings.BoostStopTunnelAfter; set { Settings.BoostStopTunnelAfter = value; OnPropertyChanged(); QueueSave(); } }
    public bool BoostOnAutostart { get => Settings.BoostOnAutostart; set { Settings.BoostOnAutostart = value; OnPropertyChanged(); QueueSave(); } }

    public string SingBoxPath
    {
        get => Settings.SingBoxPathOverride ?? "";
        set
        {
            Settings.SingBoxPathOverride = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            OnPropertyChanged();
            QueueSave();
            _ = RefreshSingBoxInfoAsync();
        }
    }

    public bool WindowVisible
    {
        get => _windowVisible;
        set
        {
            _windowVisible = value;
            // Live status is only worth its polling while someone is looking.
            if (value)
            {
                _liveTimer.Start();
                _ = RefreshLiveAsync();
            }
            else _liveTimer.Stop();
        }
    }

    // ============================================================
    //  Startup / shutdown
    // ============================================================

    public async Task InitializeAsync(bool autostart, bool interactiveSetup = true)
    {
        foreach (var e in AppLog.Snapshot()) Log.Add(new LogLine(e));
        // Background priority: a burst of log lines queues behind input,
        // rendering and timers instead of starving them.
        AppLog.Added += e => _ui.BeginInvoke(DispatcherPriority.Background, () =>
        {
            Log.Add(new LogLine(e));
            if (Log.Count > 500) Log.RemoveAt(0);
        });

        AppLog.Info($"Moonlight Tunneling {typeof(MainViewModel).Assembly.GetName().Version?.ToString(3)} started{(IsAdmin ? "" : " (without Administrator)")}.");

        foreach (var p in ProfileStore.LoadAll()) Profiles.Add(new ProfileItem(p));
        ActiveProfile = Profiles.FirstOrDefault(p => p.Model.Id == Settings.ActiveProfileId) ?? Profiles.FirstOrDefault();
        OnPropertyChanged(nameof(HasNoProfiles));

        MergeCuratedApps();
        foreach (var a in Settings.Apps.OrderByDescending(a => a.Enabled).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
            Apps.Add(new AppRowViewModel(a, this));
        RuleSetWriter.Write(Settings.Apps, AppPaths.RuleSetFile);
        SaveNow();
        OnPropertyChanged(nameof(HasDiscord));

        UpdatePortHint();
        await RefreshSingBoxInfoAsync();
        if (IsAdmin && !Settings.RenameMigrationDone)
        {
            await RenameMigration.MigrateSystemAsync(Environment.ProcessPath!);
            Settings.RenameMigrationDone = true;
            SaveNow();
        }
        _autostart = await ScheduledTaskAutostart.IsEnabledAsync();
        OnPropertyChanged(nameof(AutostartEnabled));

        await SyncKillSwitchAsync();

        if (!interactiveSetup) return;
        if (!autostart) await OfferLegacyMigrationAsync();

        if ((autostart || Settings.AutoConnectOnLaunch) && ActiveProfile != null && IsAdmin)
        {
            if (autostart) await WaitForNetworkAsync(TimeSpan.FromSeconds(180));
            await StartTunnelAsync(interactive: !autostart);
            if (autostart && _tunnel.IsTunUp)
            {
                await LaunchAutostartAppsAsync();
                if (Settings.BoostOnAutostart && HasDiscord) await BoostAsync(interactive: false);
            }
        }
        else if (ActiveProfile == null && !autostart)
        {
            AppLog.Info("No VPN profile yet: import a .conf under \"VPN profiles\".");
            SelectedTab = 1;
        }
    }

    private void MergeCuratedApps()
    {
        var detected = AppCatalog.DetectInstalledCurated();
        foreach (var d in detected)
        {
            var existing = Settings.Apps.FirstOrDefault(a => a.CuratedKey == d.CuratedKey);
            if (existing != null)
            {
                // Follow the app if it moved; keep the user's choices.
                existing.Kind = d.Kind;
                existing.Target = d.Target;
                existing.ExeName = d.ExeName;
                existing.LaunchPath = d.LaunchPath;
                existing.LaunchArgs = d.LaunchArgs;
            }
            else if (!Settings.HiddenCuratedKeys.Contains(d.CuratedKey!))
            {
                Settings.Apps.Add(d);
            }
        }
        if (!Settings.FirstRunDone)
        {
            // The original use case: Discord goes through the tunnel out of the box.
            var discord = Settings.Apps.FirstOrDefault(a => a.CuratedKey == "discord");
            if (discord != null) discord.Enabled = true;
            Settings.FirstRunDone = true;
        }
    }

    public void StopBackgroundWork()
    {
        _liveTimer.Stop();
        _saveTimer.Stop();
    }

    /// <summary>Thread-agnostic (also runs from SessionEnding on a worker thread); call StopBackgroundWork first.</summary>
    public async Task ShutdownAsync()
    {
        SaveNow();
        if (_tunnel.IsActive) await _tunnel.StopAsync();
        await _killSwitch.SyncAsync(false, Settings.Apps.ToList());
        _tunnel.Dispose();
    }

    private static async Task WaitForNetworkAsync(TimeSpan max)
    {
        // At logon Windows publishes routes before the link can carry traffic;
        // sing-box dials WireGuard once at start, so starting early means a dead VPN.
        var deadline = DateTime.UtcNow + max;
        while (DateTime.UtcNow < deadline)
        {
            if (NetworkDetect.GetPhysicalInterface() != null && await NetworkDetect.CanReachInternetAsync(TimeSpan.FromSeconds(3)))
                return;
            await Task.Delay(3000);
        }
        AppLog.Warn("The network took too long to come up; trying to connect anyway.");
    }

    private async Task LaunchAutostartAppsAsync()
    {
        var rows = Apps.Where(r => r.LaunchOnAutostart && r.CanLaunch).ToList();
        if (rows.Count == 0) return;
        for (int i = 0; i < 30 && !DeElevatedLauncher.ShellAvailable; i++) await Task.Delay(2000);
        foreach (var r in rows)
        {
            var cmd = AppCatalog.GetLaunchCommand(r.Model)!.Value;
            bool running = ProcessPaths.Snapshot().Any(p => r.Matcher.IsMatch(p.Path));
            if (running && r.Enabled)
            {
                // Opened by its own "start with Windows" before the tunnel existed:
                // its first connections went out the normal way, so start it over.
                AppLog.Info($"{r.Name} started before the tunnel; restarting it so it begins tunneled.");
                await Task.Run(() => ProcessPaths.KillWhere(r.Matcher.IsMatch, TimeSpan.FromSeconds(6)));
                await Task.Delay(800);
                running = false;
            }
            if (running) continue;
            try
            {
                DeElevatedLauncher.Launch(cmd.Exe, cmd.Args);
                AppLog.Success(r.Enabled ? $"{r.Name} opened through the tunnel." : $"{r.Name} opened.");
            }
            catch (Exception ex) { AppLog.Error($"Couldn't open {r.Name}: {ex.Message}"); }
        }
    }

    // ============================================================
    //  Tunnel
    // ============================================================

    private async Task ToggleTunnelAsync()
    {
        if (_tunnel.IsActive)
        {
            await _tunnel.StopAsync();
            LatencyText = "—";
        }
        else
        {
            await StartTunnelAsync(interactive: true);
        }
    }

    private async Task StartTunnelAsync(bool interactive)
    {
        if (!IsAdmin)
        {
            if (interactive) Inform("The tunnel needs Moonlight Tunneling running as Administrator (that's what creates the virtual network adapter). Open it from the installer's shortcut.", MessageBoxImage.Warning);
            return;
        }
        var profile = ActiveProfile?.Model;
        if (profile == null)
        {
            if (interactive) Inform("Import a WireGuard .conf file first (VPN profiles tab).");
            SelectedTab = 1;
            return;
        }
        var exe = SingBoxBinary.Locate(Settings.SingBoxPathOverride);
        if (exe == null)
        {
            var msg = "sing-box.exe not found. Reinstall Moonlight Tunneling or set its path in Settings.";
            AppLog.Error(msg);
            if (interactive) Inform(msg, MessageBoxImage.Error);
            return;
        }
        RuleSetWriter.Write(Settings.Apps, AppPaths.RuleSetFile);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                await _tunnel.StartAsync(new TunnelStartOptions
                {
                    Profile = profile,
                    SingBoxExe = exe,
                    ProxyPort = Settings.ProxyPort,
                    AutoReconnect = Settings.AutoReconnect,
                    MaxReconnectAttempts = Settings.MaxReconnectAttempts,
                });
                return;
            }
            catch (ForeignSingBoxException ex)
            {
                var list = string.Join("\n", ex.Processes.Select(p => "• " + p.Path));
                bool kill = interactive
                    ? Ask($"Another sing-box is already running:\n\n{list}\n\nTwo tunnels at once fight over the default route and break the network (and two sharing one WireGuard key knock each other off the VPN). Stop the other one and continue?", MessageBoxImage.Warning)
                    : Settings.LegacyDisabled && ex.Processes.All(p => p.Path.Contains("Discord Single-Tunneling", StringComparison.OrdinalIgnoreCase));
                if (!kill)
                {
                    if (!interactive) Notify?.Invoke("Moonlight Tunneling", "Another sing-box is already running; the tunnel was not started.");
                    return;
                }
                foreach (var p in ex.Processes)
                {
                    try { using var proc = Process.GetProcessById(p.Pid); proc.Kill(); proc.WaitForExit(5000); }
                    catch { }
                }
                AppLog.Info("Other sing-box stopped.");
                await Task.Delay(1500);
            }
            catch (PortInUseException ex)
            {
                int free = NetworkDetect.FindFreePort(ex.Port + 1);
                if (interactive && !Ask($"Port {ex.Port} is already used by another program. Use {free} for the local proxy?"))
                    return;
                ProxyPortText = free.ToString();
                AppLog.Info($"Local proxy port changed to {free}.");
            }
            catch (Exception ex)
            {
                if (interactive) Inform(ex.Message, MessageBoxImage.Error);
                else Notify?.Invoke("Moonlight Tunneling", ex.Message);
                return;
            }
        }
    }

    private void OnTunnelState(TunnelState s, string detail)
    {
        (StatusTitle, StatusBrush) = s switch
        {
            TunnelState.Connected => ("Connected", Palette.Green),
            TunnelState.Starting => ("Connecting…", Palette.Yellow),
            TunnelState.Degraded => ("VPN not responding", Palette.Yellow),
            TunnelState.Reconnecting => ("Reconnecting…", Palette.Yellow),
            TunnelState.Failed => ("Error", Palette.Red),
            _ => ("Off", Palette.Gray),
        };
        StatusDetail = s == TunnelState.Stopped ? "The tunnel is off. No app goes through the VPN." : detail;
        if (s is TunnelState.Stopped or TunnelState.Failed) LatencyText = "—";
        UpdatePortHint();
        OnPropertyChanged(nameof(IsTunnelActive));
        OnPropertyChanged(nameof(ToggleText));
        CommandManager.InvalidateRequerySuggested();
        _ = SyncKillSwitchAsync();
        if (!_windowVisible && s is TunnelState.Failed or TunnelState.Reconnecting)
            Notify?.Invoke(s == TunnelState.Failed ? "Tunnel dropped" : "Tunnel reconnecting", detail);
        if (_windowVisible) _ = RefreshLiveAsync();
    }

    private async Task SyncKillSwitchAsync()
    {
        bool up = _tunnel.IsTunUp;
        await _killSwitch.SyncAsync(up, Settings.Apps.ToList());
        var blocked = Settings.Apps.Where(a => a.Enabled && a.KillSwitch).Select(a => a.Name).ToList();
        KillSwitchBanner = !up && blocked.Count > 0 && IsAdmin
            ? $"Kill-switch engaged: {string.Join(", ", blocked)} {(blocked.Count == 1 ? "has" : "have")} no internet until the tunnel connects."
            : null;
    }

    // ============================================================
    //  Discord Go Live boost
    // ============================================================

    private static bool IsDiscord(AppRowViewModel r) =>
        r.Model.CuratedKey is "discord" or "discord-ptb" or "discord-canary";

    /// <summary>
    /// Mirrors what people did by hand with a VPN client: connect, open Discord,
    /// then disconnect. Discord only needs the VPN while it signs in - Go Live
    /// availability is decided for that session - so afterwards it can (and
    /// should) run on the normal connection, at full speed and with no tunnel
    /// overhead. The boost does the whole dance: tunnel up, Discord restarted
    /// through it, a hold while it signs in, then the tunnel handed back.
    /// </summary>
    public async Task BoostAsync(bool interactive)
    {
        var row = Apps.FirstOrDefault(IsDiscord);
        if (row == null)
        {
            if (interactive) Inform("Discord isn't in the list. Add it with \"Add app\" first.");
            return;
        }
        if (!IsAdmin)
        {
            if (interactive) Inform("The boost needs Moonlight Tunneling running as Administrator, because it has to turn the tunnel on.", MessageBoxImage.Warning);
            return;
        }
        if (ActiveProfile == null)
        {
            if (interactive) Inform("Import a WireGuard .conf first (VPN profiles tab).");
            SelectedTab = 1;
            return;
        }
        // A kill-switch on Discord is the opposite of this feature: releasing
        // the tunnel would cut Discord off instead of handing it to the network.
        if (row.KillSwitch)
        {
            if (interactive) Inform($"{row.Name} has the kill-switch on, which blocks it whenever the tunnel isn't connected - the exact thing the boost relies on. Turn the kill-switch off for it first.", MessageBoxImage.Warning);
            return;
        }

        bool tunnelWasOff = !_tunnel.IsTunUp;
        bool wasEnabled = row.Enabled;
        var cts = new CancellationTokenSource();
        _boostCts = cts;
        RaiseBoostChanged();
        try
        {
            if (!_tunnel.IsTunUp)
            {
                SetBoostStatus("Turning the tunnel on\u2026");
                await StartTunnelAsync(interactive);
                if (!_tunnel.IsTunUp)
                {
                    AppLog.Warn("Boost stopped: the tunnel didn't come up.");
                    return;
                }
            }
            if (!wasEnabled)
            {
                // Temporary: the saved preference is restored on release.
                row.SetEnabledTemporarily(true);
                RuleSetWriter.Write(Settings.Apps, AppPaths.RuleSetFile);
                SetBoostStatus("Routing Discord through the tunnel\u2026");
                await Task.Delay(3000, cts.Token); // sing-box picks the rule-set change up
            }

            SetBoostStatus("Reopening Discord through the tunnel\u2026");
            int killed = await Task.Run(() => ProcessPaths.KillWhere(row.Matcher.IsMatch, TimeSpan.FromSeconds(6)), cts.Token);
            await Task.Delay(1200, cts.Token);
            if (AppCatalog.GetLaunchCommand(row.Model) is { } cmd) DeElevatedLauncher.Launch(cmd.Exe, cmd.Args);
            else if (interactive) Inform($"Open {row.Name} now - it will sign in through the tunnel.");
            AppLog.Info($"Boost: {row.Name} restarted through the tunnel ({killed} process(es) closed).");

            SetBoostStatus("Waiting for Discord to sign in through the VPN\u2026");
            if (!await WaitForTunneledAsync(row, TimeSpan.FromSeconds(45), cts.Token))
                AppLog.Warn("Boost: Discord hasn't opened a connection through the tunnel yet; holding anyway.");

            for (int left = Settings.BoostHoldSeconds; left > 0; left--)
            {
                SetBoostStatus($"Discord is signed in through the VPN. Handing it back in {left}s\u2026 (Release now does it immediately)");
                await Task.Delay(1000, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            AppLog.Info("Boost: released early.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Boost failed: " + ex.Message);
        }
        finally
        {
            await ReleaseBoostAsync(row, wasEnabled, tunnelWasOff);
            _boostCts = null;
            cts.Dispose();
            SetBoostStatus(null);
            RaiseBoostChanged();
        }
    }

    private async Task ReleaseBoostAsync(AppRowViewModel row, bool wasEnabled, bool tunnelWasOff)
    {
        try
        {
            if (!wasEnabled && row.Enabled)
            {
                row.SetEnabledTemporarily(false);
                RuleSetWriter.Write(Settings.Apps, AppPaths.RuleSetFile);
            }
            // Only turn off a tunnel the boost itself started, and only when
            // nothing else is relying on it.
            // Discord itself doesn't count: the whole point is handing it back.
            bool othersTunneled = Settings.Apps.Any(a => a.Enabled && !ReferenceEquals(a, row.Model));
            if (tunnelWasOff && Settings.BoostStopTunnelAfter && !othersTunneled && _tunnel.IsActive)
            {
                SetBoostStatus("Turning the tunnel back off\u2026");
                await _tunnel.StopAsync();
                AppLog.Success($"Boost done: {row.Name} is back on your normal connection and the tunnel is off. Go Live keeps working until you close {row.Name}.");
                return;
            }
            // The tunnel stays up for the other apps, so drop Discord's tunneled
            // connections instead: it reconnects directly right now, which is
            // what closing a VPN client used to do.
            if (_tunnel.Clash is { } clash)
            {
                var conns = await clash.GetConnectionsAsync();
                int closed = 0;
                foreach (var c in conns ?? [])
                    if (c.ViaVpn && c.ProcessPath != null && row.Matcher.IsMatch(c.ProcessPath))
                    {
                        await clash.CloseAsync(c.Id);
                        closed++;
                    }
                AppLog.Success($"Boost done: {row.Name} is back on your normal connection ({closed} tunneled connection(s) closed). Go Live keeps working until you close {row.Name}.");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Boost release failed: " + ex.Message);
        }
    }

    private async Task<bool> WaitForTunneledAsync(AppRowViewModel row, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var conns = _tunnel.Clash != null ? await _tunnel.Clash.GetConnectionsAsync() : null;
            if (conns != null && conns.Any(c => c.ViaVpn && c.ProcessPath != null && row.Matcher.IsMatch(c.ProcessPath)))
                return true;
            await Task.Delay(2000, ct);
        }
        return false;
    }

    private void SetBoostStatus(string? status)
    {
        _boostStatus = status;
        OnPropertyChanged(nameof(BoostText));
    }

    private void RaiseBoostChanged()
    {
        OnPropertyChanged(nameof(IsBoosting));
        OnPropertyChanged(nameof(IsNotBoosting));
        CommandManager.InvalidateRequerySuggested();
    }

    // ============================================================
    //  Apps
    // ============================================================

    public void OnAppChanged(AppRowViewModel row, AppChange change)
    {
        QueueSave();
        if (change == AppChange.LaunchOnAutostart)
        {
            if (row.LaunchOnAutostart && !AutostartEnabled)
                AppLog.Info($"{row.Name} will open with Windows once \"Start with Windows\" is on (Settings).");
            return;
        }
        if (change == AppChange.Enabled)
        {
            bool changed = RuleSetWriter.Write(Settings.Apps, AppPaths.RuleSetFile);
            if (changed)
            {
                string when = _tunnel.IsActive ? "" : " (applies when the tunnel is on)";
                if (row.Enabled)
                    AppLog.Success($"{row.Name}: new connections go through the tunnel{when}." + (row.IsRunning && _tunnel.IsActive ? " Already-open ones stay where they are; use Relaunch or Reapply." : ""));
                else
                    AppLog.Info($"{row.Name}: out of the tunnel; new connections use the normal network." + (row.IsRunning && _tunnel.IsActive ? " Ones already open through the VPN finish there unless you use Reapply." : ""));
                if (row.Enabled && AppCatalog.IsSharedWebView(row.PathText))
                    AppLog.Warn("msedgewebview2.exe is shared by several apps; all of them go through the tunnel together.");
            }
        }
        if (change == AppChange.KillSwitch)
            AppLog.Info(row.KillSwitch
                ? $"{row.Name}: kill-switch on. If the tunnel drops, it gets no internet instead of using the normal network."
                : $"{row.Name}: kill-switch off.");
        _ = SyncKillSwitchAsync();
        _ = RefreshLiveAsync();
    }

    public void RemoveApp(AppRowViewModel row)
    {
        if (row.Enabled && !Ask($"Remove {row.Name} from the list? It leaves the tunnel."))
            return;
        Apps.Remove(row);
        Settings.Apps.Remove(row.Model);
        OnPropertyChanged(nameof(HasDiscord));
        if (row.Model.CuratedKey != null) Settings.HiddenCuratedKeys.Add(row.Model.CuratedKey);
        RuleSetWriter.Write(Settings.Apps, AppPaths.RuleSetFile);
        QueueSave();
        _ = SyncKillSwitchAsync();
        AppLog.Info($"{row.Name} removed from the list.");
    }

    private void AddApps()
    {
        var dlg = new AddAppWindow { Owner = OwnerProvider() };
        if (dlg.ShowDialog() != true) return;
        foreach (var app in dlg.Result)
        {
            var pattern = PathPattern.ForApp(app);
            var existing = Apps.FirstOrDefault(r => PathPattern.ForApp(r.Model) == pattern);
            if (existing != null)
            {
                existing.Enabled = true;
                continue;
            }
            app.Enabled = true;
            Settings.Apps.Add(app);
            var row = new AppRowViewModel(app, this);
            Apps.Insert(0, row);
            OnAppChanged(row, AppChange.Enabled);
            OnPropertyChanged(nameof(HasDiscord));
        }
        QueueSave();
    }

    public async Task RelaunchAsync(AppRowViewModel row)
    {
        var where = row.Enabled ? "through the tunnel" : "on the normal network";
        if (row.Enabled && !_tunnel.IsTunUp &&
            !Ask($"The tunnel is off, so {row.Name} will open on the normal network. Relaunch anyway?"))
            return;
        if (!Ask($"Close and reopen {row.Name} {where}?\n\nAnything open in it (voice call, tabs, unsaved text) may be lost.", MessageBoxImage.Warning))
            return;
        var cmd = AppCatalog.GetLaunchCommand(row.Model);
        int killed = await Task.Run(() => ProcessPaths.KillWhere(row.Matcher.IsMatch, TimeSpan.FromSeconds(6)));
        AppLog.Info($"{row.Name}: {killed} process(es) closed.");
        if (cmd is { } c)
        {
            await Task.Delay(800);
            try
            {
                DeElevatedLauncher.Launch(c.Exe, c.Args);
                AppLog.Success($"{row.Name} reopened {where}.");
            }
            catch (Exception ex)
            {
                AppLog.Error($"Couldn't reopen {row.Name}: {ex.Message}");
            }
        }
        else
        {
            Inform($"{row.Name} was closed. Open it again from the Start menu; it will already go {where}.");
        }
        await Task.Delay(1500);
        await RefreshLiveAsync();
    }

    /// <summary>Makes already-open connections follow the current rules by closing the ones on the wrong side.</summary>
    private async Task ReapplyAsync()
    {
        var clash = _tunnel.Clash;
        if (clash == null) return;
        var conns = await clash.GetConnectionsAsync();
        if (conns == null)
        {
            AppLog.Warn("Couldn't read the tunnel's connections.");
            return;
        }
        var enabled = Apps.Where(r => r.Enabled).Select(r => r.Matcher).ToList();
        int closed = 0;
        foreach (var c in conns)
        {
            if (c.ProcessPath == null) continue;
            bool shouldTunnel = enabled.Any(m => m.IsMatch(c.ProcessPath));
            if (shouldTunnel != c.ViaVpn)
            {
                await clash.CloseAsync(c.Id);
                closed++;
            }
        }
        AppLog.Info(closed == 0
            ? "All open connections already follow the rules."
            : $"{closed} connection(s) on the wrong side were closed; the apps reconnect on their own, following the rules.");
        await Task.Delay(1000);
        await RefreshLiveAsync();
    }

    private async Task RefreshLiveAsync()
    {
        if (_liveBusy) return;
        _liveBusy = true;
        try
        {
            var rows = Apps.ToList();
            bool up = _tunnel.IsTunUp;
            var clash = up ? _tunnel.Clash : null;
            var result = await Task.Run(async () =>
            {
                var procs = ProcessPaths.Snapshot();
                var conns = clash != null ? await clash.GetConnectionsAsync() : null;
                return (conns != null, rows.Select(r =>
                {
                    var running = procs.Where(p => r.Matcher.IsMatch(p.Path)).Select(p => p.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    int vpn = 0, direct = 0;
                    if (conns != null)
                        foreach (var c in conns)
                            if (c.ProcessPath != null && r.Matcher.IsMatch(c.ProcessPath))
                            {
                                if (c.ViaVpn) vpn++;
                                else direct++;
                            }
                    return (Row: r, Running: running, Vpn: vpn, Direct: direct);
                }).ToList());
            });
            bool dirty = false;
            foreach (var x in result.Item2)
            {
                x.Row.UpdateLive(x.Running.Count > 0, x.Vpn, x.Direct, up, result.Item1);
                // Pattern-based entries remember concrete exes for the kill-switch.
                if (x.Row.Model.Kind is AppMatchKind.Regex or AppMatchKind.Folder)
                    foreach (var path in x.Running)
                        if (!x.Row.Model.SeenPaths.Contains(path, StringComparer.OrdinalIgnoreCase) && x.Row.Model.SeenPaths.Count < 50)
                        {
                            x.Row.Model.SeenPaths.Add(path);
                            dirty = true;
                        }
            }
            if (dirty) QueueSave();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
        finally
        {
            _liveBusy = false;
        }
    }

    // ============================================================
    //  Profiles
    // ============================================================

    private void ImportProfiles()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Pick one or more WireGuard .conf files",
            Filter = "WireGuard (*.conf)|*.conf|All files (*.*)|*.*",
            Multiselect = true,
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        };
        if (dlg.ShowDialog(OwnerProvider()) != true) return;
        foreach (var file in dlg.FileNames)
        {
            try
            {
                var cfg = WireGuardConf.Parse(File.ReadAllText(file));
                if (Profiles.Any(p => p.Model.PeerPublicKey == cfg.PeerPublicKey && p.Model.EndpointHost == cfg.EndpointHost &&
                                      p.Model.Addresses.SequenceEqual(cfg.Addresses)))
                {
                    AppLog.Info($"{Path.GetFileName(file)} was already imported.");
                    continue;
                }
                var profile = WireGuardConf.ToProfile(cfg, Path.GetFileNameWithoutExtension(file), file);
                ProfileStore.Save(profile);
                var item = new ProfileItem(profile);
                Profiles.Add(item);
                AppLog.Success($"Profile \"{profile.Name}\" imported ({profile.EndpointDisplay}).");
                ActiveProfile ??= item;
            }
            catch (Exception ex)
            {
                AppLog.Error($"{Path.GetFileName(file)}: {ex.Message}");
                Inform($"Couldn't import {Path.GetFileName(file)}:\n\n{ex.Message}", MessageBoxImage.Error);
            }
        }
        OnPropertyChanged(nameof(HasNoProfiles));
    }

    private async Task UseProfileAsync(ProfileItem p)
    {
        if (ReferenceEquals(p, ActiveProfile)) return;
        ActiveProfile = p;
        AppLog.Info($"Active profile: {p.Name}.");
        if (_tunnel.IsActive)
        {
            await _tunnel.StopAsync();
            await StartTunnelAsync(interactive: true);
        }
    }

    private void DeleteProfile(ProfileItem p)
    {
        if (ReferenceEquals(p, ActiveProfile) && _tunnel.IsActive)
        {
            Inform("This profile is in use. Turn the tunnel off before deleting it.");
            return;
        }
        if (!Ask($"Delete the profile \"{p.Name}\"? Its key is erased from this PC (the original .conf is not touched).")) return;
        ProfileStore.Delete(p.Model);
        Profiles.Remove(p);
        if (ReferenceEquals(p, ActiveProfile)) ActiveProfile = Profiles.FirstOrDefault();
        OnPropertyChanged(nameof(HasNoProfiles));
        AppLog.Info($"Profile \"{p.Name}\" deleted.");
    }

    /// <summary>
    /// Offered until the old tool is actually off, not just once: if disabling
    /// failed (or the user said no), the two tunnels would keep fighting.
    /// </summary>
    private async Task OfferLegacyMigrationAsync()
    {
        LegacyInstall? l;
        try { l = await LegacyDiscordTunneling.DetectAsync(); }
        catch { l = null; }
        if (l == null) return;

        bool taskOn = l.TaskExists && await LegacyDiscordTunneling.IsTaskEnabledAsync();
        bool stillActive = taskOn || l.StartupShortcut != null || l.Running.Count > 0;
        bool canImport = l.ConfigPath != null && !Profiles.Any(p => p.Model.Source == l.ConfigPath);
        if (!stillActive && !canImport) return;
        if (Settings.LegacyMigrationHandled && !Settings.LegacyDisabled && !canImport) return; // user said no before

        var parts = new List<string>();
        if (canImport) parts.Add("• import its VPN profile (same WireGuard key)");
        if (taskOn || l.StartupShortcut != null) parts.Add("• turn off its startup");
        if (l.Running.Count > 0) parts.Add("• stop the old tunnel that's running now");
        if (!Ask("Found Discord Tunneling (the old version) on this PC. I can:\n\n" + string.Join("\n", parts) +
                 "\n\nIts files are not deleted; you can uninstall it from Control Panel later. Do it now?"))
        {
            Settings.LegacyMigrationHandled = true;
            SaveNow();
            AppLog.Info("Discord Tunneling migration declined; if both run at once, Moonlight Tunneling asks before turning on.");
            return;
        }

        // Turn the old tunnel off first: it's the part that matters, and it
        // shouldn't depend on anything after it succeeding.
        bool disabled = true;
        if (stillActive)
        {
            if (IsAdmin) disabled = await LegacyDiscordTunneling.DisableAsync(l);
            else
            {
                disabled = false;
                AppLog.Warn("Without Administrator the old Discord Tunneling can't be turned off.");
            }
        }
        Settings.LegacyMigrationHandled = true;
        Settings.LegacyDisabled = disabled;
        SaveNow();

        if (canImport)
        {
            var p = LegacyDiscordTunneling.ImportProfile(l.ConfigPath!);
            if (p != null && !Profiles.Any(x => x.Model.PeerPublicKey == p.PeerPublicKey && x.Model.EndpointHost == p.EndpointHost))
            {
                ProfileStore.Save(p);
                var item = new ProfileItem(p);
                Profiles.Add(item);
                ActiveProfile ??= item;
                OnPropertyChanged(nameof(HasNoProfiles));
                AppLog.Success($"Profile imported from Discord Tunneling ({p.EndpointDisplay}).");
            }
            else if (p == null)
            {
                AppLog.Warn("Couldn't read Discord Tunneling's profile; import the .conf manually.");
            }
        }
        if (!disabled)
            Inform("The old Discord Tunneling couldn't be fully turned off (see the log). Uninstall it from Control Panel so it doesn't start with Windows again.", MessageBoxImage.Warning);
    }

    // ============================================================
    //  Misc
    // ============================================================

    private async Task ApplyAutostartAsync(bool on)
    {
        var exe = Environment.ProcessPath!;
        RunResult r = on ? await ScheduledTaskAutostart.EnableAsync(exe) : await ScheduledTaskAutostart.DisableAsync();
        bool ok = r.ExitCode == 0 || (!on && !await ScheduledTaskAutostart.IsEnabledAsync());
        if (ok)
        {
            AppLog.Info(on ? "Start with Windows: on (scheduled task, no UAC prompt at logon)." : "Start with Windows: off.");
        }
        else
        {
            AppLog.Error("Couldn't change Start with Windows: " + r.Combined);
            _autostart = !on;
            OnPropertyChanged(nameof(AutostartEnabled));
        }
    }

    private void BrowseSingBox()
    {
        var dlg = new OpenFileDialog { Title = "Pick sing-box.exe", Filter = "sing-box.exe|sing-box.exe|Programs (*.exe)|*.exe" };
        if (dlg.ShowDialog(OwnerProvider()) == true) SingBoxPath = dlg.FileName;
    }

    private async Task RefreshSingBoxInfoAsync()
    {
        var exe = SingBoxBinary.Locate(Settings.SingBoxPathOverride);
        if (exe == null)
        {
            SingBoxInfo = Settings.SingBoxPathOverride != null ? "File not found." : "Bundled sing-box not found; reinstall Moonlight Tunneling.";
            return;
        }
        var v = await SingBoxBinary.GetVersionAsync(exe);
        SingBoxInfo = v == null ? $"{exe} failed to run."
            : v < SingBoxBinary.Minimum ? $"sing-box {v} is too old (minimum {SingBoxBinary.Minimum})."
            : $"sing-box {v} · {exe}";
    }

    private void UpdatePortHint()
    {
        int port = Settings.ProxyPort;
        PortHint = _tunnel.IsActive && _tunnel.ProxyPort == port ? "in use by the tunnel"
            : NetworkDetect.IsLocalPortFree(port) ? "free" : "in use by another program";
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    /// <summary>Snapshot renders come from an unelevated Debug build; show the UI as the elevated release looks.</summary>
    public void PrepareForScreenshots()
    {
        _hideAdminBanner = true;
        OnPropertyChanged(nameof(IsNotAdmin));
        Log.Clear();
    }

    public void ShowTrayHintOnce()
    {
        if (Settings.TrayHintShown) return;
        Settings.TrayHintShown = true;
        QueueSave();
        Notify?.Invoke("Moonlight Tunneling is still running", "The tunnel keeps running from the tray. To quit, right-click the icon > Exit.");
    }

    private void QueueSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveNow()
    {
        try { JsonStore.Save(AppPaths.SettingsFile, Settings); }
        catch (Exception ex) { AppLog.Error("Couldn't save settings: " + ex.Message); }
    }

    private bool Ask(string text, MessageBoxImage icon = MessageBoxImage.Question)
    {
        var owner = OwnerProvider();
        var r = owner is { IsVisible: true }
            ? MessageBox.Show(owner, text, "Moonlight Tunneling", MessageBoxButton.YesNo, icon)
            : MessageBox.Show(text, "Moonlight Tunneling", MessageBoxButton.YesNo, icon);
        return r == MessageBoxResult.Yes;
    }

    private void Inform(string text, MessageBoxImage icon = MessageBoxImage.Information)
    {
        var owner = OwnerProvider();
        if (owner is { IsVisible: true }) MessageBox.Show(owner, text, "Moonlight Tunneling", MessageBoxButton.OK, icon);
        else MessageBox.Show(text, "Moonlight Tunneling", MessageBoxButton.OK, icon);
    }
}
