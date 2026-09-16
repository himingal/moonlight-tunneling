using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Input;
using System.Windows.Media;
using MoonlightTunneling.App.Services;
using MoonlightTunneling.Core;

namespace MoonlightTunneling.App.ViewModels;

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
        AppMatchKind.Squirrel => "Process · follows updates",
        AppMatchKind.Folder => "Whole folder",
        AppMatchKind.Regex => "Store package",
        _ => "Process",
    };

    public string MethodTip => Model.Kind switch
    {
        AppMatchKind.Squirrel => "Self-updating app installed in app-x.y.z folders (Discord, Slack…). The rule matches every version, so updates never drop it out of the tunnel.",
        AppMatchKind.Folder => "Every executable inside this folder goes through the tunnel (handy for games and launchers with several .exe files).",
        AppMatchKind.Regex => "Microsoft Store app: the rule matches any version of the package.",
        _ => "All traffic (TCP and UDP) from this executable goes through the tunnel.",
    };

    public string? Warning =>
        !AppCatalog.Exists(Model) ? "Not found on this PC."
        : AppCatalog.IsSharedWebView(PathText) ? "WebView2 is shared: tunneling it tunnels every app that uses WebView2 (new Teams, WhatsApp, new Outlook, Widgets…)."
        : Model.Kind == AppMatchKind.Regex ? "If this app is built on WebView2 (e.g. WhatsApp), its traffic comes from msedgewebview2.exe and tunneling the app alone may have no effect."
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

    /// <summary>
    /// Flips the tunnel rule without touching the saved preference or logging a
    /// user action - used by the Go Live boost, which puts the app back after.
    /// </summary>
    public void SetEnabledTemporarily(bool value)
    {
        Model.Enabled = value;
        OnPropertyChanged(nameof(Enabled));
    }

    public string LiveText { get => _liveText; private set => Set(ref _liveText, value); }
    public Brush LiveBrush { get => _liveBrush; private set => Set(ref _liveBrush, value); }
    public bool IsRunning { get; private set; }

    private static string Conns(int n) => n == 1 ? "1 connection" : $"{n} connections";

    public void UpdateLive(bool running, int vpn, int direct, bool tunnelUp, bool haveConnections)
    {
        IsRunning = running;
        (LiveText, LiveBrush) = (running, tunnelUp, haveConnections, Enabled) switch
        {
            (false, _, _, _) => ("Closed", Palette.Gray),
            (true, false, _, true) => ("Open · tunnel off", Palette.Yellow),
            (true, false, _, false) => ("Open · normal network", Palette.Gray),
            (true, true, false, _) => ("Open", Palette.Gray),
            (true, true, true, true) when direct > 0 && vpn == 0 => ("Open outside the tunnel · relaunch", Palette.Yellow),
            (true, true, true, true) when direct > 0 => ($"In tunnel · {Conns(direct)} still outside", Palette.Yellow),
            (true, true, true, true) when vpn > 0 => ($"In tunnel · {Conns(vpn)}", Palette.Green),
            (true, true, true, true) => ("In tunnel · no connections now", Palette.Green),
            (true, true, true, false) when vpn > 0 => ($"Still in tunnel ({vpn}) until it reconnects", Palette.Yellow),
            _ => ("Normal network", Palette.Gray),
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

    public string EndpointText => "Server " + Model.EndpointDisplay;
    public string AddressText => "Address " + string.Join(", ", Model.Addresses);
    public string Ipv6Text => Model.HasIPv6 ? "IPv6 through the VPN: yes" : "IPv6 through the VPN: no (tunneled apps' IPv6 is blocked, never leaked)";
    public string ImportedText => "Imported " + Model.ImportedAt.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
    public bool IsActive { get => _isActive; set { if (Set(ref _isActive, value)) OnPropertyChanged(nameof(IsInactive)); } }
    public bool IsInactive => !_isActive;
}
