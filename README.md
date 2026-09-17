<p align="center">
  <img src="docs/banner.png" alt="Moonlight Tunneling" width="100%">
</p>

<p align="center">
  <a href="https://github.com/himingal/moonlight-tunneling/releases/latest"><img src="https://img.shields.io/github/v/release/himingal/moonlight-tunneling?color=9B87F5&label=download" alt="Download"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-7462D8" alt="Windows 10 | 11">
  <img src="https://img.shields.io/badge/VPN-WireGuard-9B87F5" alt="WireGuard">
  <img src="https://img.shields.io/github/license/himingal/moonlight-tunneling?color=7462D8" alt="MIT">
</p>

<p align="center">
  <b>Pick which apps go through your VPN. The rest of your PC stays on your normal connection.</b>
</p>

<p align="center">
  <img src="docs/screenshots/apps.png" width="880" alt="Moonlight Tunneling">
</p>

Moonlight Tunneling works with any WireGuard VPN, including ones with no built-in split tunneling (Proton VPN free, Mullvad, your own server). Tunnel Discord, a browser, a game or any other program, and leave everything else untouched.

## Features

| | |
|---|---|
| 🎯 **Per process, TCP and UDP** | Voice, video and game traffic go through the tunnel too, not just web requests. |
| ⚡ **Applies instantly** | Tick or untick an app and its new connections follow within seconds, without restarting the tunnel. |
| 🔄 **Survives updates** | Discord, Slack (`app-*` folders) and Microsoft Store apps stay tunneled after they update. |
| 🛡️ **Per-app kill-switch** | Optional. If the tunnel drops, that app loses internet instead of leaking onto your normal connection. |
| 🌐 **No IPv6 leaks** | If your network has IPv6 and the VPN doesn't, tunneled apps' IPv6 is blocked. |
| 🔁 **Reconnects on its own** | Recovers from crashes, network switches (Ethernet ↔ Wi-Fi) and a silent VPN, with a retry limit. |
| 🚀 **Starts with Windows** | No UAC prompt at logon. It opens your chosen apps once the tunnel is up. |

<table>
  <tr>
    <td width="50%"><img src="docs/screenshots/add-app.png" alt="Add app"><br><sub>Add any program: something using the network right now, a Start menu entry, a single .exe, or a whole folder (games with launchers).</sub></td>
    <td width="50%"><img src="docs/screenshots/profiles.png" alt="VPN profiles"><br><sub>Keep as many WireGuard profiles as you like. Private keys are encrypted for your Windows user.</sub></td>
  </tr>
  <tr>
    <td colspan="2"><img src="docs/screenshots/settings.png" alt="Settings"><br><sub>Startup, reconnection, the local proxy port and the engine.</sub></td>
  </tr>
</table>

<sub>Screenshots use sample profiles with documentation addresses, not real servers.</sub>

## Install

1. Download `MoonlightTunneling-Setup-x.y.z.exe` from [Releases](https://github.com/himingal/moonlight-tunneling/releases/latest).
2. Install it and open it. Windows asks for Administrator rights because the app creates a virtual network adapter.
3. On the **VPN profiles** tab, click **Import .conf**. For Proton, go to account.protonvpn.com → Downloads → WireGuard configuration and pick a server.
4. Tick the apps you want tunneled and click **Turn tunnel on**.

Upgrading from Mingal Tunnel? Install on top. It closes the old version, and on first start Moonlight Tunneling moves your profiles, settings, autostart and kill-switch rules over to the new name.

## How it works

```
WireGuard .conf ─► profile (key encrypted with DPAPI)
ticked apps     ─► rule-set that sing-box reloads on its own
                           │
             config over stdin (the key never touches disk)
                           ▼
                sing-box (child process of the app)
                 ├─ virtual TUN adapter
                 ├─ ticked process? ─► WireGuard ─► internet through the VPN
                 └─ everything else ─► network card ─► normal internet
```

The engine is [sing-box](https://github.com/SagerNet/sing-box) in TUN mode. It identifies which process opened each connection and routes it accordingly. Moonlight Tunneling generates the config, supervises sing-box and shows what's going on. If the app closes or crashes, Windows stops sing-box with it, so a tunnel is never left running on its own.

## Known limitations

- **The tunnel covers the whole process.** Ticking Brave sends the entire browser, not a single tab.
- **WebView2 apps share a process.** The new Teams, WhatsApp Desktop and the new Outlook all run on `msedgewebview2.exe`, so they are tunneled together.
- **Administrator rights are required**, because creating the TUN adapter needs them.
- **All IPv4 traffic passes through sing-box**, even traffic that goes out directly. That's the cost of routing per process. The overhead is small but not zero.
- **Connections that are already open** when you tick or untick an app finish on the route they started on. **Reapply** closes the ones on the wrong side, and **Relaunch** restarts the app.

## Troubleshooting

The **Log** bar at the bottom of the window opens the activity log, and **Copy** puts it on your clipboard for bug reports. Log files live in `%LOCALAPPDATA%\MoonlightTunneling\logs`.

## Privacy

Everything stays on your PC. There's no telemetry and no update check. The only traffic the app creates itself is the tunnel you configured, plus a tiny `generate_204` health probe sent through that tunnel.

## Building

Requires .NET SDK 10 and Inno Setup 6.

```powershell
build\fetch-deps.ps1                  # sing-box 1.14.0 + wintun 0.14.1, SHA256-verified
powershell -STA build\make-logo.ps1   # regenerates the icon, logo and banner
build\publish.ps1                     # tests + self-contained app + installer
```

Debug builds run without Administrator: the UI works, but the tunnel won't turn on. `MoonlightTunneling.exe --snapshot <folder>` renders the screenshots above.

## Credits

[sing-box](https://github.com/SagerNet/sing-box) (GPLv3, shipped unmodified) · [Wintun](https://www.wintun.net) · MIT License

<p align="center"><sub>made by mingal</sub></p>
