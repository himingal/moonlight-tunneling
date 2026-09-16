<p align="center">
  <img src="assets/app_logo.png" width="84" alt="Mingal Tunnel">
</p>

<h1 align="center">Mingal Tunnel</h1>

<p align="center">
  <b>Per-app split tunneling for Windows.</b><br>
  Pick which programs go through your VPN. The rest of your PC stays on your normal connection.
</p>

<p align="center">
  <a href="https://github.com/himingal/mingal-tunnel/releases/latest"><img src="https://img.shields.io/github/v/release/himingal/mingal-tunnel?include_prereleases&color=F657A9&label=download" alt="Download"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-D63C8C" alt="Windows 10 | 11">
  <img src="https://img.shields.io/badge/VPN-WireGuard-F657A9" alt="WireGuard">
  <img src="https://img.shields.io/github/license/himingal/mingal-tunnel?color=D63C8C" alt="MIT">
</p>

<p align="center">
  <img src="docs/screenshots/apps.png" width="880" alt="Mingal Tunnel app list">
</p>

Works with any WireGuard VPN, including the ones without built-in split tunneling (Proton VPN free, Mullvad, your own server). It started as a way to get **Discord Go Live** working without pushing the whole PC through the VPN. Now it works for any app: browsers, games, launchers.

It replaces [discord-tunneling](https://github.com/himingal/discord-tunneling). You get a real window, an app list and profiles, and you never touch a shortcut.

## What it does

| | |
|---|---|
| 🎯 **Per process, TCP and UDP** | Voice, video, Go Live and games go through the tunnel, not only web traffic. |
| ⚡ **Applies instantly** | Tick or untick an app and new connections follow within seconds. The tunnel doesn't restart. |
| 🔄 **Survives updates** | Discord/Slack (`app-*` folders) and Microsoft Store apps stay tunneled after they update. |
| 🛡️ **Per-app kill-switch** | Optional. If the tunnel drops, the app loses internet instead of leaking onto your normal network. |
| 🌐 **No IPv6 leaks** | If your network has IPv6 and the VPN doesn't, IPv6 from tunneled apps is blocked. |
| 🔁 **Reconnects on its own** | Handles crashes, network switches (Ethernet ↔ Wi-Fi) and unresponsive VPNs, with a retry limit. |
| 🔍 **Exit IP check** | Compares your exit IP through the VPN with your normal IP. |
| 🚀 **Starts with Windows** | No UAC prompt at logon. Opens your chosen apps once the tunnel is up. |

## Discord Go Live boost

Discord decides whether Go Live is available when it signs in, so it only needs the VPN for that one moment. That's the trick people did by hand: connect the VPN, open Discord, disconnect the VPN, and the stream keeps working.

**Go Live boost** does the whole thing in one click. It turns the tunnel on, reopens Discord through it, waits until Discord has signed in through the VPN, holds for a while (60s by default), then hands Discord back to your normal connection and turns the tunnel off if nothing else needs it. The call itself then runs at full speed with no tunnel in the way, and stays that way until you close Discord.

In Settings you can change the hold, keep the tunnel up afterwards, or run the boost automatically at Windows startup. Prefer Discord tunneled the whole time? Just tick it in the list and ignore the boost. One thing that doesn't mix: the kill-switch blocks Discord whenever the tunnel isn't connected, which is exactly what the boost does on purpose, so the boost refuses to run while it's on.

<table>
  <tr>
    <td width="50%"><img src="docs/screenshots/add-app.png" alt="Add app"><br><sub>Add any program: apps using the network right now, the Start menu, a single .exe, or a whole folder (games with launchers).</sub></td>
    <td width="50%"><img src="docs/screenshots/profiles.png" alt="VPN profiles"><br><sub>Multiple WireGuard profiles. Private keys are encrypted for your Windows user.</sub></td>
  </tr>
  <tr>
    <td colspan="2"><img src="docs/screenshots/settings.png" alt="Settings"><br><sub>Startup, reconnection, local proxy port and engine.</sub></td>
  </tr>
</table>

<sub>Screenshots use sample profiles (documentation addresses, not real servers).</sub>

## Install

1. Download `MingalTunnel-Setup-x.y.z.exe` from [Releases](https://github.com/himingal/mingal-tunnel/releases).
2. Install it and open it. Windows asks for Administrator rights because the app has to create a virtual network adapter.
3. On the **VPN profiles** tab, click **Import .conf**.
   For Proton: account.protonvpn.com → Downloads → WireGuard configuration → pick a server.
4. Click **Turn tunnel on**. Discord is ticked by default; tick any other apps you want.

If the old **Discord Tunneling** is installed, Mingal Tunnel offers to import its profile and switch it off, since two tunnels would fight over the default route.

## How it works

```
WireGuard .conf ─► profile (key encrypted with DPAPI)
ticked apps     ─► tunneled-apps.json (rule-set sing-box reloads on its own)
                           │
             config over stdin (the key never touches disk)
                           ▼
                sing-box (child process of the app)
                 ├─ TUN adapter "MingalTunnel"
                 ├─ ticked process? ─► WireGuard ─► internet through the VPN
                 └─ everything else ─► network card ─► normal internet
```

The engine is [sing-box](https://github.com/SagerNet/sing-box) in TUN mode. It works out which process opened each connection and picks the exit based on that. The app writes the config, supervises the process and shows what's happening. If Mingal Tunnel closes or crashes, Windows takes sing-box down with it, so no tunnel is ever left running orphaned.

## Known limitations

- **The tunnel covers the whole process.** Ticking Brave sends the entire browser, not one tab.
- **WebView2 apps share one process.** New Teams, WhatsApp Desktop and the new Outlook all use `msedgewebview2.exe`, so tunneling it tunnels all of them together.
- **Needs Administrator**, because the TUN adapter requires it.
- **All IPv4 traffic on the PC passes through sing-box**, even traffic that goes out directly. That's the cost of routing per process. The overhead is small, but it's there.
- **Connections already open** when you tick or untick an app finish where they started. Use **Reapply**, which closes the ones on the wrong side, or **Relaunch**, which restarts the app.

## Checking that it works

1. With the tunnel on, click **Check**. "Via VPN" should show a different IP from "Normal".
2. Click **Relaunch** on Discord. The **Now** column should show `In tunnel · N connections`.
3. Open [ipleak.net](https://ipleak.net) in a tunneled app and in a non-tunneled one. The IP and country should differ.
4. Turn on the kill-switch for an app, then turn the tunnel off. That app should lose internet.
5. Switch networks with the tunnel on. The log shows "Network changed…" and the tunnel comes back by itself.

If something goes wrong, the log is in the window itself (**Copy** button) and in `%LOCALAPPDATA%\MingalTunnel\logs`.

## Privacy

Everything stays in `%LOCALAPPDATA%\MingalTunnel`. There's no telemetry and no update check. The app only makes its own network requests in three cases:

- the tunnel you configured;
- **Check** (api.ipify.org), only when you click it;
- a Cloudflare `generate_204` probe sent through the tunnel to monitor VPN health.

## Building

Requires .NET SDK 10 and Inno Setup 6.

```powershell
build\fetch-deps.ps1    # sing-box 1.14.0 + wintun 0.14.1, SHA256-verified
build\publish.ps1       # tests + self-contained app + installer in installer\output
```

Debug builds run without Administrator: the UI works, but the tunnel won't turn on. `MingalTunnel.exe --snapshot <folder>` renders the screenshots above.

```
src/MingalTunnel.App        WPF (window, tray, dialogs)
src/MingalTunnel.Core       profiles, sing-box config, supervisor, kill-switch
src/MingalTunnel.Platform   Win32: processes, network, firewall, scheduled task
tests/                      parser, patterns, configs validated by the real sing-box
installer/                  Inno Setup
```

## Credits

[sing-box](https://github.com/SagerNet/sing-box) (GPLv3, shipped unmodified) · [Wintun](https://www.wintun.net) · MIT License

<p align="center"><sub>made by mingal</sub></p>
