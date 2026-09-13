using System.Text.Json.Nodes;
using MingalTunnel.Platform;

namespace MingalTunnel.Core;

public sealed record LegacyInstall(string Dir, string? ConfigPath, bool TaskExists, string? StartupShortcut, List<ProcessEntry> Running);

/// <summary>Finds the old discord-tunneling setup so the switch is one click and the two tunnels never fight.</summary>
public static class LegacyDiscordTunneling
{
    public const string TaskName = "DiscordTunneling";

    private static IEnumerable<string> CandidateDirs()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        foreach (var name in new[] { "Discord Single-Tunneling", "Discord Tunneling" })
        {
            yield return Path.Combine(local, "Programs", name);
            yield return Path.Combine(pf, name);
            yield return Path.Combine(pf86, name);
        }
    }

    public static async Task<LegacyInstall?> DetectAsync()
    {
        var dir = CandidateDirs().FirstOrDefault(d => File.Exists(Path.Combine(d, "installer.ps1")) || File.Exists(Path.Combine(d, "config.json")));
        var task = (await ProcessRunner.RunAsync("schtasks.exe", $"/Query /TN \"{TaskName}\"")).ExitCode == 0;
        var startup = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Discord (Tunneling).lnk");
        var running = dir == null ? [] : ProcessPaths.Snapshot()
            .Where(p => p.Path.StartsWith(dir, StringComparison.OrdinalIgnoreCase) && p.Path.EndsWith("sing-box.exe", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (dir == null && !task && !File.Exists(startup)) return null;
        var cfg = dir != null && File.Exists(Path.Combine(dir, "config.json")) ? Path.Combine(dir, "config.json") : null;
        return new LegacyInstall(dir ?? "", cfg, task, File.Exists(startup) ? startup : null, running);
    }

    /// <summary>Rebuilds a profile from the sing-box config the old installer generated (1.12+ endpoints or pre-1.12 outbounds).</summary>
    public static VpnProfile? ImportProfile(string configPath)
    {
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(configPath))!;
            JsonNode? wg = root["endpoints"]?.AsArray().FirstOrDefault(e => (string?)e?["type"] == "wireguard");
            var p = new VpnProfile { Name = "Importado do Discord Tunneling", Source = configPath };
            string? privateKey, psk = null;

            if (wg != null)
            {
                privateKey = (string?)wg["private_key"];
                p.Addresses = wg["address"]?.AsArray().Select(a => (string)a!).ToList() ?? [];
                p.Mtu = (int?)wg["mtu"];
                var peer = wg["peers"]?.AsArray().FirstOrDefault();
                p.EndpointHost = (string?)peer?["address"] ?? "";
                p.EndpointPort = (int?)peer?["port"] ?? 0;
                p.PeerPublicKey = (string?)peer?["public_key"] ?? "";
                psk = (string?)peer?["pre_shared_key"];
            }
            else
            {
                wg = root["outbounds"]?.AsArray().FirstOrDefault(o => (string?)o?["type"] == "wireguard");
                if (wg == null) return null;
                privateKey = (string?)wg["private_key"];
                var la = wg["local_address"];
                p.Addresses = la is JsonArray arr ? arr.Select(a => (string)a!).ToList() : la != null ? [(string)la!] : [];
                p.EndpointHost = (string?)wg["server"] ?? "";
                p.EndpointPort = (int?)wg["server_port"] ?? 0;
                p.PeerPublicKey = (string?)wg["peer_public_key"] ?? "";
                psk = (string?)wg["pre_shared_key"];
            }

            var vpnDns = root["dns"]?["servers"]?.AsArray()
                .FirstOrDefault(s => (string?)s?["tag"] == "dns-vpn");
            var dnsServer = (string?)vpnDns?["server"] ?? (string?)vpnDns?["address"];
            if (dnsServer != null && System.Net.IPAddress.TryParse(dnsServer, out _) && dnsServer != "1.1.1.1") p.Dns = [dnsServer];

            if (string.IsNullOrEmpty(privateKey) || string.IsNullOrEmpty(p.PeerPublicKey) || string.IsNullOrEmpty(p.EndpointHost) || p.EndpointPort == 0 || p.Addresses.Count == 0)
                return null;
            p.PrivateKeyProtected = Dpapi.Protect(privateKey);
            if (!string.IsNullOrEmpty(psk)) p.PresharedKeyProtected = Dpapi.Protect(psk);
            return p;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Turns the old tool off without deleting it (it can still be uninstalled normally).</summary>
    public static async Task DisableAsync(LegacyInstall l)
    {
        if (l.TaskExists)
        {
            var r = await ProcessRunner.RunAsync("schtasks.exe", $"/Change /TN \"{TaskName}\" /DISABLE");
            if (r.ExitCode == 0) AppLog.Info("Tarefa de inicialização do Discord Tunneling desativada.");
            else AppLog.Warn("Não consegui desativar a tarefa DiscordTunneling: " + r.Combined);
        }
        if (l.StartupShortcut != null)
        {
            try
            {
                File.Delete(l.StartupShortcut);
                AppLog.Info("Atalho de inicialização do Discord Tunneling removido.");
            }
            catch (Exception ex) { AppLog.Warn("Não consegui remover o atalho de inicialização antigo: " + ex.Message); }
        }
        foreach (var p in l.Running)
        {
            try
            {
                using var proc = System.Diagnostics.Process.GetProcessById(p.Pid);
                proc.Kill();
                proc.WaitForExit(5000);
                AppLog.Info("Túnel antigo (sing-box do Discord Tunneling) encerrado.");
            }
            catch (Exception ex) { AppLog.Warn("Não consegui encerrar o sing-box antigo: " + ex.Message); }
        }
    }
}
