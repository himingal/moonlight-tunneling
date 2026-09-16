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

Moonlight Tunneling works with any WireGuard VPN, including ones with no built-in split tunneling (Proton VPN free, Mullvad, your own server). It started as a way to get **Discord Go Live** working without sending the whole PC through the VPN, and it works for any app: browsers, games, launchers.

## Features

| | |
|---|---|
| 🌙 **Discord Go Live boost** | One click gives Discord the VPN while it signs in, then hands it back to your normal connection. |
| 🎯 **Per process, TCP and UDP** | Voice, video and game traffic go through the tunnel too, not just web requests. |
| ⚡ **Applies instantly** | Tick or untick an app and its new connections follow within seconds, without restarting the tunnel. |
| 🔄 **Survives updates** | Discord, Slack (`app-*` folders) and Microsoft Store apps stay tunneled after they update. |
| 🛡️ **Per-app kill-switch** | Optional. If the tunnel drops, that app loses internet instead of leaking onto your normal connection. |
| 🌐 **No IPv6 leaks** | If your network has IPv6 and the VPN doesn't, tunneled apps' IPv6 is blocked. |
| 🔁 **Reconnects on its own** | Recovers from crashes, network switches (Ethernet ↔ Wi-Fi) and a silent VPN, with a retry limit. |
| 🚀 **Starts with Windows** | No UAC prompt at logon. It can run the boost or open your apps once the tunnel is up. |

## Discord Go Live boost

Discord decides whether Go Live is available at the moment it signs in, so it only needs the VPN for that moment. That's why this old trick works: connect a VPN, open Discord, disconnect the VPN, and you can still stream.

**Go Live boost** does that in one click:

1. It turns the tunnel on and reopens Discord through it.
2. It waits until Discord has signed in through the VPN, then holds for **20 seconds**. The hold time is configurable, and **Release now** skips it.
3. It hands Discord back to your normal connection and turns the tunnel off if no other app needs it.

After that, the call runs at full speed with no tunnel in the way, and it stays that way until you close Discord.

If you'd rather keep Discord tunneled all the time, tick it in the list and ignore the boost. The boost won't run while Discord's kill-switch is on, because the kill-switch cuts Discord off exactly when the tunnel is released.

<table>
  <tr>
    <td width="50%"><img src="docs/screenshots/add-app.png" alt="Add app"><br><sub>Add any program: something using the network right now, a Start menu entry, a single .exe, or a whole folder (games with launchers).</sub></td>
    <td width="50%"><img src="docs/screenshots/profiles.png" alt="VPN profiles"><br><sub>Keep as many WireGuard profiles as you like. Private keys are encrypted for your Windows user.</sub></td>
  </tr>
  <tr>
    <td colspan="2"><img src="docs/screenshots/settings.png" alt="Settings"><br><sub>Startup, reconnection, the boost, the local proxy port and the engine.</sub></td>
  </tr>
</table>

<sub>Screenshots use sample profiles with documentation addresses, not real servers.</sub>

## Install

1. Download `MoonlightTunneling-Setup-x.y.z.exe` from [Releases](https://github.com/himingal/moonlight-tunneling/releases/latest).
2. Install it and open it. Windows asks for Administrator rights because the app creates a virtual network adapter.
3. On the **VPN profiles** tab, click **Import .conf**. For Proton, go to account.protonvpn.com → Downloads → WireGuard configuration and pick a server.
4. Click **Go Live boost** for Discord, or tick the apps you want tunneled and click **Turn tunnel on**.

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
