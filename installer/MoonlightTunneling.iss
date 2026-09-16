; ============================================================
;  Moonlight Tunneling - Inno Setup script
;  Build with build\publish.ps1 (publishes the app into dist\app
;  and then compiles this file).
; ============================================================

#define MyAppName "Moonlight Tunneling"
#define MyAppVersion "1.2.0"
#define MyAppPublisher "mingal"
#define MyAppExeName "MoonlightTunneling.exe"

[Setup]
AppId={{6C1E5B7A-3F2D-4B8E-9A61-2D7C0F4E8B19}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://github.com/himingal/moonlight-tunneling
DefaultDirName={autopf}\{#MyAppName}
; Always the new folder, even when upgrading an install made as "Mingal Tunnel".
UsePreviousAppDir=no
DisableProgramGroupPage=yes
; Program Files, not the user profile: the app runs elevated (and can autostart
; elevated), so its binaries must not live in a folder any user process can
; overwrite - that would be a free privilege escalation.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=output
OutputBaseFilename=MoonlightTunneling-Setup-{#MyAppVersion}
SetupIconFile=..\assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=force

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; Self-contained single-file app + engine\sing-box.exe + engine\wintun.dll.
; Wintun needs no separate driver install step: sing-box loads wintun.dll and
; Wintun installs its (signed) driver on the fly when the adapter is created.
Source: "..\dist\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; What versions released as "Mingal Tunnel" left behind. The app itself moves
; its data folder, autostart task and firewall rules on first start.
Type: files; Name: "{autoprograms}\Mingal Tunnel.lnk"
Type: files; Name: "{autodesktop}\Mingal Tunnel.lnk"
Type: filesandordirs; Name: "{autopf}\Mingal Tunnel"

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Windows caches icons per exe path; refresh so the taskbar shows the new one.
Filename: "{sys}\ie4uinit.exe"; Parameters: "-show"; Flags: runhidden nowait skipifdoesntexist
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent shellexec

[UninstallRun]
; Stopping the app also stops sing-box (it runs inside the app's job object).
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#MyAppExeName}"; Flags: runhidden; RunOnceId: "StopApp"
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN MoonlightTunneling /F"; Flags: runhidden; RunOnceId: "RemoveAutostart"
; Leftover kill-switch rules would keep apps offline after the uninstall.
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""MoonlightTunneling Kill-Switch"""; Flags: runhidden; RunOnceId: "RemoveKillSwitch"

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  // The old build runs under another exe name, so Restart Manager won't
  // close it; stop it (and its tunnel) before its files are replaced.
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM MingalTunnel.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(1500);
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\MoonlightTunneling');
    if DirExists(DataDir) then
      if MsgBox('Also delete the settings and VPN profiles (WireGuard keys) saved in ' + DataDir + '?',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
