using System.Text.RegularExpressions;
using System.Windows.Input;
using System.Windows.Media;
using MingalTunnel.App.Services;
using MingalTunnel.Core;

namespace MingalTunnel.App.ViewModels;

public enum AppChange { Enabled, KillSwitch, LaunchOnAutostart }

public sealed class AppRowViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private string _liveText = "—";
    private Brush _liveBrush = Palette.Gray;
    private ImageSource? _icon;

    public AppRowViewModel(TunneledApp model, MainViewModel owner)
    {
        Model = model;
        _owner = owner;
        Matcher = PathPattern.ToRegex(PathPattern.ForApp(model));
        RelaunchCommand = new AsyncCommand(() => _owner.RelaunchAsync(this));
        RemoveCommand = new RelayCommand(() => _owner.RemoveApp(this));
        var iconPath = AppCatalog.IconSource(model);
        Task.Run(() => IconCache.Get(iconPath)).ContinueWith(t => Icon = t.Result, TaskScheduler.FromCurrentSynchronizationContext());
    }

    public TunneledApp Model { get; }
    public Regex Matcher { get; }
    public ICommand RelaunchCommand { get; }
    public ICommand RemoveCommand { get; }

    public ImageSource? Icon { get => _icon; private set => Set(ref _icon, value); }
    public string Name => Model.Name;
    public string PathText => AppCatalog.DisplayPath(Model);
    public bool CanLaunch => AppCatalog.GetLaunchCommand(Model) != null;

    public string MethodText => Model.Kind switch
    {
        AppMatchKind.Squirrel => "Processo · segue updates",
        AppMatchKind.Folder => "Pasta inteira",
        AppMatchKind.Regex => "Pacote da Store",
        _ => "Processo",
    };

    public string MethodTip => Model.Kind switch
    {
        AppMatchKind.Squirrel => "App que se autoatualiza em pastas app-x.y.z (Discord, Slack…). A regra vale pra qualquer versão, então updates não tiram ele do túnel.",
        AppMatchKind.Folder => "Todo executável dentro desta pasta vai pelo túnel (bom pra jogos e launchers com vários .exe).",
        AppMatchKind.Regex => "App da Microsoft Store: a regra vale pra qualquer versão do pacote.",
        _ => "Todo o tráfego (TCP e UDP) deste executável vai pelo túnel.",
    };

    public string? Warning =>
        !AppCatalog.Exists(Model) ? "Não encontrado neste PC."
        : AppCatalog.IsSharedWebView(PathText) ? "WebView2 é compartilhado: tunelar ele tunela todos os apps que usam WebView2 (novo Teams, WhatsApp, novo Outlook, Widgets…)."
        : Model.Kind == AppMatchKind.Regex ? "Se este app for feito com WebView2 (ex.: WhatsApp), a rede sai pelo msedgewebview2.exe e tunelar só o app pode não ter efeito."
        : null;

    public bool HasWarning => Warning != null;

    public bool Enabled
    {
        get => Model.Enabled;
        set
        {
            if (Model.Enabled == value) return;
            Model.Enabled = value;
            OnPropertyChanged();
            _owner.OnAppChanged(this, AppChange.Enabled);
        }
    }

    public bool KillSwitch
    {
        get => Model.KillSwitch;
        set
        {
            if (Model.KillSwitch == value) return;
            Model.KillSwitch = value;
            OnPropertyChanged();
            _owner.OnAppChanged(this, AppChange.KillSwitch);
        }
    }

    public bool LaunchOnAutostart
    {
        get => Model.LaunchOnAutostart;
        set
        {
            if (Model.LaunchOnAutostart == value) return;
            Model.LaunchOnAutostart = value;
            OnPropertyChanged();
            _owner.OnAppChanged(this, AppChange.LaunchOnAutostart);
        }
    }

    public string LiveText { get => _liveText; private set => Set(ref _liveText, value); }
    public Brush LiveBrush { get => _liveBrush; private set => Set(ref _liveBrush, value); }
    public bool IsRunning { get; private set; }

    public void UpdateLive(bool running, int vpn, int direct, bool tunnelUp, bool haveConnections)
    {
        IsRunning = running;
        (LiveText, LiveBrush) = (running, tunnelUp, haveConnections, Enabled) switch
        {
            (false, _, _, _) => ("Fechado", Palette.Gray),
            (true, false, _, true) => ("Aberto · túnel desligado", Palette.Yellow),
            (true, false, _, false) => ("Aberto · rede normal", Palette.Gray),
            (true, true, false, _) => ("Aberto", Palette.Gray),
            (true, true, true, true) when direct > 0 && vpn == 0 => ("Aberto fora do túnel · reabra", Palette.Yellow),
            (true, true, true, true) when direct > 0 => ($"No túnel · {direct} conexão(ões) antiga(s) fora", Palette.Yellow),
            (true, true, true, true) when vpn > 0 => ($"No túnel · {vpn} conexão(ões)", Palette.Green),
            (true, true, true, true) => ("No túnel · sem conexões agora", Palette.Green),
            (true, true, true, false) when vpn > 0 => ($"Ainda no túnel ({vpn}) até reconectar", Palette.Yellow),
            _ => ("Rede normal", Palette.Gray),
        };
    }
}

public sealed class ProfileItem(VpnProfile model) : ObservableObject
{
    private bool _isActive;

    public VpnProfile Model { get; } = model;

    public string Name
    {
        get => Model.Name;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value.Trim() == Model.Name) return;
            Model.Name = value.Trim();
            ProfileStore.Save(Model);
            OnPropertyChanged();
        }
    }

    public string EndpointText => "Servidor " + Model.EndpointDisplay;
    public string AddressText => "Endereço " + string.Join(", ", Model.Addresses);
    public string Ipv6Text => Model.HasIPv6 ? "IPv6 pela VPN: sim" : "IPv6 pela VPN: não (IPv6 dos apps tunelados é bloqueado, não vaza)";
    public string ImportedText => $"Importado em {Model.ImportedAt:dd/MM/yyyy}";
    public bool IsActive { get => _isActive; set { if (Set(ref _isActive, value)) OnPropertyChanged(nameof(IsInactive)); } }
    public bool IsInactive => !_isActive;
}
