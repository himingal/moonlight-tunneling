using System.Text.Json.Serialization;

namespace MingalTunnel.Core;

public enum AppMatchKind
{
    /// <summary>One specific executable.</summary>
    ExactPath,
    /// <summary>Squirrel app (Discord, Slack…): Target = install root, matches root\app-*\ExeName across auto-updates.</summary>
    Squirrel,
    /// <summary>Every executable under a folder (games, launchers with several exes).</summary>
    Folder,
    /// <summary>Raw RE2/.NET-compatible pattern (Store packages, whose folder carries the version).</summary>
    Regex,
}

public sealed class TunneledApp
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public AppMatchKind Kind { get; set; }
    public string Target { get; set; } = "";
    public string? ExeName { get; set; }
    public bool Enabled { get; set; }
    public bool KillSwitch { get; set; }
    public bool LaunchOnAutostart { get; set; }
    public string? LaunchPath { get; set; }
    public string? LaunchArgs { get; set; }
    public string? CuratedKey { get; set; }
    /// <summary>Concrete exe paths seen running for this app; feeds the kill-switch for pattern-based kinds.</summary>
    public List<string> SeenPaths { get; set; } = [];
}

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public List<TunneledApp> Apps { get; set; } = [];
    public List<string> HiddenCuratedKeys { get; set; } = [];
    public string? ActiveProfileId { get; set; }
    public int ProxyPort { get; set; } = 1080;
    public bool AutoConnectOnLaunch { get; set; } = true;
    public bool AutoReconnect { get; set; } = true;
    public int MaxReconnectAttempts { get; set; } = 5;
    public bool MinimizeToTray { get; set; } = true;
    public string? SingBoxPathOverride { get; set; }
    public bool LegacyMigrationHandled { get; set; }
    /// <summary>User accepted turning the old discord-tunneling off (so its sing-box may be stopped unattended).</summary>
    public bool LegacyDisabled { get; set; }
    public bool TrayHintShown { get; set; }
    public bool FirstRunDone { get; set; }
}

public sealed class VpnProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public List<string> Addresses { get; set; } = [];
    public string PrivateKeyProtected { get; set; } = "";
    public string PeerPublicKey { get; set; } = "";
    public string? PresharedKeyProtected { get; set; }
    public string EndpointHost { get; set; } = "";
    public int EndpointPort { get; set; }
    public List<string> Dns { get; set; } = [];
    public int? Mtu { get; set; }
    public DateTime ImportedAt { get; set; } = DateTime.Now;
    public string? Source { get; set; }

    [JsonIgnore] public bool HasIPv4 => Addresses.Any(a => !a.Contains(':'));
    [JsonIgnore] public bool HasIPv6 => Addresses.Any(a => a.Contains(':'));
    [JsonIgnore] public string EndpointDisplay => EndpointHost.Contains(':') ? $"[{EndpointHost}]:{EndpointPort}" : $"{EndpointHost}:{EndpointPort}";
}
