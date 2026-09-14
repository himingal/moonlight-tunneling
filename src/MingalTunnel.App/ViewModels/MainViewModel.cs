using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using MingalTunnel.Core;
using MingalTunnel.Platform;

namespace MingalTunnel.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly Dispatcher _ui;
    private readonly TunnelSupervisor _tunnel = new();
    private readonly KillSwitch _killSwitch = new();
    private readonly DispatcherTimer _liveTimer;
    private readonly DispatcherTimer _saveTimer;
    private bool _windowVisible, _liveBusy, _autostartMode;

    private string _statusTitle = "Desligado", _statusDetail = "O túnel está desligado. Nenhum app passa pela VPN.";
    private Brush _statusBrush = Palette.Gray;
    private string _latencyText = "—", _vpnIp = "—", _directIp = "—", _ipVerdict = "Clique em Verificar pra comparar o IP da VPN com o seu IP normal.";
    private Brush _ipVerdictBrush = Palette.Gray;
    private bool _checkingIp;
    private string? _killSwitchBanner;
    private int _selectedTab;
    private ProfileItem? _activeProfile;
    private bool _autostart;
    private string _singBoxInfo = "";
    private string _portHint = "";

    public MainViewModel(Dispatcher ui)
    {
        _ui = ui;
        Settings = JsonStore.LoadOrDefault<AppSettings>(AppPaths.SettingsFile);
        IsAdmin = Elevation.IsAdministrator();

        _liveTimer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (_, _) => _ = RefreshLiveAsync(), ui);
        _saveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => { _saveTimer!.Stop(); SaveNow(); }, ui);
        _saveTimer.Stop();
        _liveTimer.Stop();

        _tunnel.StateChanged += (s, d) => _ui.BeginInvoke(() => OnTunnelState(s, d));
        _tunnel.LatencyChanged += ms => _ui.BeginInvoke(() => LatencyText = ms is int v ? $"{v} ms" : "sem resposta");

        ToggleTunnelCommand = new AsyncCommand(ToggleTunnelAsync, () => _tunnel.State != TunnelState.Starting);
        CheckIpCommand = new AsyncCommand(CheckIpAsync, () => !_checkingIp);
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
        ProtonConfLinkCommand = new RelayCommand(() => Process.Start(new ProcessStartInfo("https://account.protonvpn.com/downloads") { UseShellExecute = true }));
    }

    public Func<Window?> OwnerProvider { get; set; } = () => null;
    public event Action<string, string>? Notify;

    public AppSettings Settings { get; }
    public bool IsAdmin { get; }
    public bool IsNotAdmin => !IsAdmin && !_hideAdminBanner;
    private bool _hideAdminBanner;

    /// <summary>Snapshot renders come from an unelevated Debug build; show the UI as the elevated release looks.</summary>
    public void PrepareForScreenshots()
    {
        _hideAdminBanner = true;
        OnPropertyChanged(nameof(IsNotAdmin));
        Log.Clear();
    }
    public ObservableCollection<AppRowViewModel> Apps { get; } = [];
    public ObservableCollection<ProfileItem> Profiles { get; } = [];
    public ObservableCollection<LogLine> Log { get; } = [];

    public ICommand ToggleTunnelCommand { get; }
    public ICommand CheckIpCommand { get; }
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

    // ---------- status ----------
    public TunnelState State => _tunnel.State;
    public bool IsTunnelActive => _tunnel.IsActive;
    public string ToggleText => _tunnel.State == TunnelState.Starting ? "Conectando…" : _tunnel.IsActive ? "Desligar túnel" : "Ligar túnel";
    public string StatusTitle { get => _statusTitle; private set => Set(ref _statusTitle, value); }
    public string StatusDetail { get => _statusDetail; private set => Set(ref _statusDetail, value); }
    public Brush StatusBrush { get => _statusBrush; private set => Set(ref _statusBrush, value); }
    public string LatencyText { get => _latencyText; private set => Set(ref _latencyText, value); }
    public string VpnIpText { get => _vpnIp; private set => Set(ref _vpnIp, value); }
    public string DirectIpText { get => _directIp; private set => Set(ref _directIp, value); }
    public string IpVerdict { get => _ipVerdict; private set => Set(ref _ipVerdict, value); }
    public Brush IpVerdictBrush { get => _ipVerdictBrush; private set => Set(ref _ipVerdictBrush, value); }
    public string ProxyText => $"proxy local 127.0.0.1:{Settings.ProxyPort}";
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
            QueueSave();
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActiveProfileName));
        }
    }

    public string ActiveProfileName => _activeProfile?.Name ?? "nenhum perfil";

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
                if (_tunnel.IsActive) AppLog.Info($"Porta local agora é {n}; vale a partir da próxima conexão do túnel.");
            }
            OnPropertyChanged();
        }
    }

    public string PortHint { get => _portHint; private set => Set(ref _portHint, value); }

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
        _autostartMode = autostart;
        foreach (var e in AppLog.Snapshot()) Log.Add(new LogLine(e));
        AppLog.Added += e => _ui.BeginInvoke(() =>
        {
            Log.Add(new LogLine(e));
            if (Log.Count > 500) Log.RemoveAt(0);
        });

        AppLog.Info($"Mingal Tunnel {typeof(MainViewModel).Assembly.GetName().Version?.ToString(3)} iniciado{(IsAdmin ? "" : " (sem Administrador)")}.");

        foreach (var p in ProfileStore.LoadAll()) Profiles.Add(new ProfileItem(p));
        ActiveProfile = Profiles.FirstOrDefault(p => p.Model.Id == Settings.ActiveProfileId) ?? Profiles.FirstOrDefault();
        OnPropertyChanged(nameof(HasNoProfiles));

        MergeCuratedApps();
        foreach (var a in Settings.Apps.OrderByDescending(a => a.Enabled).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
            Apps.Add(new AppRowViewModel(a, this));
        RuleSetWriter.Write(Settings.Apps, AppPaths.RuleSetFile);
        SaveNow();

        UpdatePortHint();
        await RefreshSingBoxInfoAsync();
        _autostart = await ScheduledTaskAutostart.IsEnabledAsync();
        OnPropertyChanged(nameof(AutostartEnabled));

        await SyncKillSwitchAsync();

        if (!interactiveSetup) return;
        if (!autostart && !Settings.LegacyMigrationHandled) await OfferLegacyMigrationAsync();

        if ((autostart || Settings.AutoConnectOnLaunch) && ActiveProfile != null && IsAdmin)
        {
            if (autostart) await WaitForNetworkAsync(TimeSpan.FromSeconds(180));
            await StartTunnelAsync(interactive: !autostart);
            if (autostart && _tunnel.IsTunUp) await LaunchAutostartAppsAsync();
        }
        else if (ActiveProfile == null && !autostart)
        {
            AppLog.Info("Nenhum perfil VPN ainda: importe um .conf em \"Perfis VPN\".");
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
        AppLog.Warn("A rede demorou pra ficar pronta; tentando conectar mesmo assim.");
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
                AppLog.Info($"{r.Name} abriu antes do túnel; reiniciando pra já entrar tunelado.");
                await Task.Run(() => ProcessPaths.KillWhere(r.Matcher.IsMatch, TimeSpan.FromSeconds(6)));
                await Task.Delay(800);
                running = false;
            }
            if (running) continue;
            try
            {
                DeElevatedLauncher.Launch(cmd.Exe, cmd.Args);
                AppLog.Success($"{r.Name} aberto {(r.Enabled ? "pelo túnel" : "")}.".Replace(" .", "."));
            }
            catch (Exception ex) { AppLog.Error($"Não consegui abrir {r.Name}: {ex.Message}"); }
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
            if (interactive) Inform("O túnel precisa do Mingal Tunnel rodando como Administrador (é ele que cria o adaptador de rede virtual). Abra pelo atalho do instalador.", MessageBoxImage.Warning);
            return;
        }
        var profile = ActiveProfile?.Model;
        if (profile == null)
        {
            if (interactive) Inform("Importe um arquivo .conf do WireGuard primeiro (aba Perfis VPN).");
            SelectedTab = 1;
            return;
        }
        var exe = SingBoxBinary.Locate(Settings.SingBoxPathOverride);
        if (exe == null)
        {
            var msg = "Não achei o sing-box.exe. Reinstale o Mingal Tunnel ou aponte o caminho em Configurações.";
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
                    ? Ask($"Já tem outro sing-box rodando:\n\n{list}\n\nDois túneis ao mesmo tempo disputam a rota padrão e derrubam a rede (e dois usando a mesma chave WireGuard derrubam a VPN). Encerrar o outro e continuar?", MessageBoxImage.Warning)
                    : Settings.LegacyDisabled && ex.Processes.All(p => p.Path.Contains("Discord Single-Tunneling", StringComparison.OrdinalIgnoreCase));
                if (!kill)
                {
                    if (!interactive) Notify?.Invoke("Mingal Tunnel", "Outro sing-box já está rodando; o túnel não foi ligado.");
                    return;
                }
                foreach (var p in ex.Processes)
                {
                    try { using var proc = Process.GetProcessById(p.Pid); proc.Kill(); proc.WaitForExit(5000); }
                    catch { }
                }
                AppLog.Info("Outro sing-box encerrado.");
                await Task.Delay(1500);
            }
            catch (PortInUseException ex)
            {
                int free = NetworkDetect.FindFreePort(ex.Port + 1);
                if (interactive && !Ask($"A porta {ex.Port} já está em uso por outro programa. Usar a {free} pro proxy local?"))
                    return;
                ProxyPortText = free.ToString();
                AppLog.Info($"Porta do proxy local trocada para {free}.");
            }
            catch (Exception ex)
            {
                if (interactive) Inform(ex.Message, MessageBoxImage.Error);
                else Notify?.Invoke("Mingal Tunnel", ex.Message);
                return;
            }
        }
    }

    private void OnTunnelState(TunnelState s, string detail)
    {
        (StatusTitle, StatusBrush) = s switch
        {
            TunnelState.Connected => ("Conectado", Palette.Green),
            TunnelState.Starting => ("Conectando…", Palette.Yellow),
            TunnelState.Degraded => ("VPN sem resposta", Palette.Yellow),
            TunnelState.Reconnecting => ("Reconectando…", Palette.Yellow),
            TunnelState.Failed => ("Erro", Palette.Red),
            _ => ("Desligado", Palette.Gray),
        };
        StatusDetail = s == TunnelState.Stopped ? "O túnel está desligado. Nenhum app passa pela VPN." : detail;
        if (s is TunnelState.Stopped or TunnelState.Failed) LatencyText = "—";
        UpdatePortHint();
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsTunnelActive));
        OnPropertyChanged(nameof(ToggleText));
        CommandManager.InvalidateRequerySuggested();
        _ = SyncKillSwitchAsync();
        if (!_windowVisible && s is TunnelState.Failed or TunnelState.Reconnecting)
            Notify?.Invoke(s == TunnelState.Failed ? "Túnel caiu" : "Túnel reconectando", detail);
        if (_windowVisible) _ = RefreshLiveAsync();
    }

    private async Task SyncKillSwitchAsync()
    {
        bool up = _tunnel.IsTunUp;
        await _killSwitch.SyncAsync(up, Settings.Apps.ToList());
        var blocked = Settings.Apps.Where(a => a.Enabled && a.KillSwitch).Select(a => a.Name).ToList();
        KillSwitchBanner = !up && blocked.Count > 0 && IsAdmin
            ? $"Kill-switch ativo: {string.Join(", ", blocked)} {(blocked.Count == 1 ? "está" : "estão")} sem internet até o túnel conectar."
            : null;
    }

    private async Task CheckIpAsync()
    {
        _checkingIp = true;
        IpVerdict = "Consultando…";
        IpVerdictBrush = Palette.Gray;
        try
        {
            var directTask = IpCheck.GetPublicIpAsync(null);
            var vpnTask = _tunnel.IsTunUp ? IpCheck.GetPublicIpAsync(_tunnel.ProxyPort) : Task.FromResult<string?>(null);
            var direct = await directTask;
            var vpn = await vpnTask;
            DirectIpText = direct ?? "falhou";
            VpnIpText = _tunnel.IsTunUp ? vpn ?? "sem resposta" : "túnel desligado";
            (IpVerdict, IpVerdictBrush) = (vpn, direct) switch
            {
                (null, _) when !_tunnel.IsTunUp => ("Ligue o túnel pra ver o IP de saída da VPN.", Palette.Gray),
                (null, _) => ("A VPN não respondeu. Os apps tunelados estão sem saída agora.", Palette.Red),
                (_, null) => ("VPN ok; não consegui ver o IP normal.", Palette.Yellow),
                var (v, d) when v == d => ("⚠ Os dois IPs são iguais: a VPN não está mudando a saída.", Palette.Red),
                _ => ("✔ Apps tunelados saem por outro IP. O resto do PC continua no normal.", Palette.Green),
            };
            AppLog.Info($"IP de saída: VPN {VpnIpText} · normal {DirectIpText}.");
        }
        finally
        {
            _checkingIp = false;
            CommandManager.InvalidateRequerySuggested();
        }
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
                AppLog.Info($"{row.Name} vai abrir junto quando o \"Iniciar com o Windows\" estiver ligado (Configurações).");
            return;
        }
        if (change == AppChange.Enabled)
        {
            bool changed = RuleSetWriter.Write(Settings.Apps, AppPaths.RuleSetFile);
            if (changed)
            {
                string when = _tunnel.IsActive ? "" : " (vale quando o túnel ligar)";
                if (row.Enabled)
                    AppLog.Success($"{row.Name}: conexões novas vão pelo túnel{when}." + (row.IsRunning && _tunnel.IsActive ? " As já abertas continuam onde estavam; use Reabrir ou Reaplicar." : ""));
                else
                    AppLog.Info($"{row.Name}: fora do túnel; conexões novas saem pela rede normal." + (row.IsRunning && _tunnel.IsActive ? " As já abertas pela VPN terminam por lá, a menos que você use Reaplicar." : ""));
                if (row.Enabled && AppCatalog.IsSharedWebView(row.PathText))
                    AppLog.Warn("msedgewebview2.exe é compartilhado por vários apps; todos eles vão junto pro túnel.");
            }
        }
        if (change == AppChange.KillSwitch)
            AppLog.Info(row.KillSwitch
                ? $"{row.Name}: kill-switch ligado. Se o túnel cair, ele fica sem internet em vez de sair pela rede normal."
                : $"{row.Name}: kill-switch desligado.");
        _ = SyncKillSwitchAsync();
        _ = RefreshLiveAsync();
    }

    public void RemoveApp(AppRowViewModel row)
    {
        if (row.Enabled && !Ask($"Remover {row.Name} da lista? Ele sai do túnel."))
            return;
        Apps.Remove(row);
        Settings.Apps.Remove(row.Model);
        if (row.Model.CuratedKey != null) Settings.HiddenCuratedKeys.Add(row.Model.CuratedKey);
        RuleSetWriter.Write(Settings.Apps, AppPaths.RuleSetFile);
        QueueSave();
        _ = SyncKillSwitchAsync();
        AppLog.Info($"{row.Name} removido da lista.");
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
        }
        QueueSave();
    }

    public async Task RelaunchAsync(AppRowViewModel row)
    {
        var where = row.Enabled ? "pelo túnel" : "pela rede normal";
        if (row.Enabled && !_tunnel.IsTunUp &&
            !Ask($"O túnel está desligado, então {row.Name} vai abrir pela rede normal. Reabrir mesmo assim?"))
            return;
        if (!Ask($"Fechar e reabrir {row.Name} {where}?\n\nO que estiver aberto nele (chamada de voz, abas, textos não salvos) pode ser perdido.", MessageBoxImage.Warning))
            return;
        var cmd = AppCatalog.GetLaunchCommand(row.Model);
        int killed = await Task.Run(() => ProcessPaths.KillWhere(row.Matcher.IsMatch, TimeSpan.FromSeconds(6)));
        AppLog.Info($"{row.Name}: {killed} processo(s) fechado(s).");
        if (cmd is { } c)
        {
            await Task.Delay(800);
            try
            {
                DeElevatedLauncher.Launch(c.Exe, c.Args);
                AppLog.Success($"{row.Name} reaberto {where}.");
            }
            catch (Exception ex)
            {
                AppLog.Error($"Não consegui reabrir {row.Name}: {ex.Message}");
            }
        }
        else
        {
            Inform($"{row.Name} foi fechado. Abra de novo pelo menu Iniciar; ele já vai {where}.");
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
            AppLog.Warn("Não consegui ler as conexões do túnel.");
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
            ? "Todas as conexões abertas já seguem as regras."
            : $"{closed} conexão(ões) do lado errado foram fechadas; os apps reconectam sozinhos já seguindo as regras.");
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
            Title = "Escolha um ou mais arquivos .conf do WireGuard",
            Filter = "WireGuard (*.conf)|*.conf|Todos os arquivos (*.*)|*.*",
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
                    AppLog.Info($"{Path.GetFileName(file)} já estava importado.");
                    continue;
                }
                var profile = WireGuardConf.ToProfile(cfg, Path.GetFileNameWithoutExtension(file), file);
                ProfileStore.Save(profile);
                var item = new ProfileItem(profile);
                Profiles.Add(item);
                AppLog.Success($"Perfil \"{profile.Name}\" importado ({profile.EndpointDisplay}).");
                ActiveProfile ??= item;
            }
            catch (Exception ex)
            {
                AppLog.Error($"{Path.GetFileName(file)}: {ex.Message}");
                Inform($"Não consegui importar {Path.GetFileName(file)}:\n\n{ex.Message}", MessageBoxImage.Error);
            }
        }
        OnPropertyChanged(nameof(HasNoProfiles));
    }

    private async Task UseProfileAsync(ProfileItem p)
    {
        if (ReferenceEquals(p, ActiveProfile)) return;
        ActiveProfile = p;
        AppLog.Info($"Perfil ativo: {p.Name}.");
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
            Inform("Esse perfil está em uso. Desligue o túnel antes de excluir.");
            return;
        }
        if (!Ask($"Excluir o perfil \"{p.Name}\"? A chave dele é apagada deste PC (o .conf original não é tocado).")) return;
        ProfileStore.Delete(p.Model);
        Profiles.Remove(p);
        if (ReferenceEquals(p, ActiveProfile)) ActiveProfile = Profiles.FirstOrDefault();
        OnPropertyChanged(nameof(HasNoProfiles));
        AppLog.Info($"Perfil \"{p.Name}\" excluído.");
    }

    private async Task OfferLegacyMigrationAsync()
    {
        LegacyInstall? l;
        try { l = await LegacyDiscordTunneling.DetectAsync(); }
        catch { l = null; }
        Settings.LegacyMigrationHandled = true;
        QueueSave();
        if (l == null) return;

        var parts = new List<string>();
        if (l.ConfigPath != null) parts.Add("• importar o perfil VPN dele (a mesma chave WireGuard)");
        if (l.TaskExists || l.StartupShortcut != null) parts.Add("• desativar a inicialização automática dele");
        if (l.Running.Count > 0) parts.Add("• encerrar o túnel antigo que está rodando agora");
        if (parts.Count == 0) return;
        if (!Ask("Encontrei o Discord Tunneling (a versão antiga) neste PC. Posso:\n\n" + string.Join("\n", parts) +
                 "\n\nOs arquivos dele não são apagados; dá pra desinstalar pelo Painel de Controle depois. Fazer isso agora?"))
        {
            AppLog.Info("Migração do Discord Tunneling recusada; se os dois rodarem juntos, o Mingal avisa antes de ligar.");
            return;
        }
        if (l.ConfigPath != null)
        {
            var p = LegacyDiscordTunneling.ImportProfile(l.ConfigPath);
            if (p != null && !Profiles.Any(x => x.Model.PeerPublicKey == p.PeerPublicKey && x.Model.EndpointHost == p.EndpointHost))
            {
                ProfileStore.Save(p);
                var item = new ProfileItem(p);
                Profiles.Add(item);
                ActiveProfile ??= item;
                OnPropertyChanged(nameof(HasNoProfiles));
                AppLog.Success($"Perfil importado do Discord Tunneling ({p.EndpointDisplay}).");
            }
            else if (p == null)
            {
                AppLog.Warn("Não consegui ler o perfil do Discord Tunneling; importe o .conf manualmente.");
            }
        }
        Settings.LegacyDisabled = true;
        QueueSave();
        if (IsAdmin) await LegacyDiscordTunneling.DisableAsync(l);
        else AppLog.Warn("Sem Administrador não dá pra desativar o Discord Tunneling antigo.");
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
            AppLog.Info(on ? "Iniciar com o Windows: ligado (tarefa agendada, sem pedir UAC no logon)." : "Iniciar com o Windows: desligado.");
        }
        else
        {
            AppLog.Error("Não consegui alterar o início com o Windows: " + r.Combined);
            _autostart = !on;
            OnPropertyChanged(nameof(AutostartEnabled));
        }
    }

    private void BrowseSingBox()
    {
        var dlg = new OpenFileDialog { Title = "Escolha o sing-box.exe", Filter = "sing-box.exe|sing-box.exe|Executáveis (*.exe)|*.exe" };
        if (dlg.ShowDialog(OwnerProvider()) == true) SingBoxPath = dlg.FileName;
    }

    private async Task RefreshSingBoxInfoAsync()
    {
        var exe = SingBoxBinary.Locate(Settings.SingBoxPathOverride);
        if (exe == null)
        {
            SingBoxInfo = Settings.SingBoxPathOverride != null ? "Arquivo não encontrado." : "sing-box embutido não encontrado; reinstale o Mingal Tunnel.";
            return;
        }
        var v = await SingBoxBinary.GetVersionAsync(exe);
        SingBoxInfo = v == null ? $"{exe} não executou."
            : v < SingBoxBinary.Minimum ? $"sing-box {v} é antigo demais (mínimo {SingBoxBinary.Minimum})."
            : $"sing-box {v} · {exe}";
    }

    private void UpdatePortHint()
    {
        int port = Settings.ProxyPort;
        PortHint = _tunnel.IsActive && _tunnel.ProxyPort == port ? "em uso pelo túnel"
            : NetworkDetect.IsLocalPortFree(port) ? "livre" : "em uso por outro programa";
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    public void ShowTrayHintOnce()
    {
        if (Settings.TrayHintShown) return;
        Settings.TrayHintShown = true;
        QueueSave();
        Notify?.Invoke("Mingal Tunnel continua aqui", "O túnel segue rodando na bandeja. Pra fechar de vez, clique com o botão direito no ícone > Sair.");
    }

    private void QueueSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveNow()
    {
        try { JsonStore.Save(AppPaths.SettingsFile, Settings); }
        catch (Exception ex) { AppLog.Error("Não consegui salvar as configurações: " + ex.Message); }
    }

    private bool Ask(string text, MessageBoxImage icon = MessageBoxImage.Question)
    {
        var owner = OwnerProvider();
        var r = owner is { IsVisible: true }
            ? MessageBox.Show(owner, text, "Mingal Tunnel", MessageBoxButton.YesNo, icon)
            : MessageBox.Show(text, "Mingal Tunnel", MessageBoxButton.YesNo, icon);
        return r == MessageBoxResult.Yes;
    }

    private void Inform(string text, MessageBoxImage icon = MessageBoxImage.Information)
    {
        var owner = OwnerProvider();
        if (owner is { IsVisible: true }) MessageBox.Show(owner, text, "Mingal Tunnel", MessageBoxButton.OK, icon);
        else MessageBox.Show(text, "Mingal Tunnel", MessageBoxButton.OK, icon);
    }
}
