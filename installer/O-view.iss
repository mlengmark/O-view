; O-view installer (Inno Setup) — per-user, no elevation, no code signing.
;
; Why an installer at all: O-view shipped as a loose single-file exe with no
; install location and no Start Menu entry, so once it was closed there was no way
; to find and relaunch it (issue #7). This produces the Start Menu executable and
; the run-at-startup entry that make the app persist across reboots.
;
; Why Inno Setup and not MSIX: MSIX cannot be installed without a trusted signing
; certificate, and O-view deliberately ships unsigned (a cert costs more than a free
; tool justifies — see README). Inno produces an unsigned per-user setup that needs
; no certificate and no elevation. See docs/adr/0008-installer-distribution.md.
;
; Build:  ISCC.exe /DAppVersion=1.2.3 installer\O-view.iss
;         (AppVersion defaults to 0.0.0-dev for a local compile.)

#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif

#define AppName "O-view"
#define AppPublisher "Maximilian Lengmark"
#define AppUrl "https://github.com/mlengmark/O-view"
#define AppExeName "O-view.Tray.exe"

; Must match StartupRegistration in src/O-view.Tray/Tray/StartupRegistration.cs
; exactly — same Run value name and the same quoted-path data — so the installer
; and the app's "Run at startup" menu item manage one single registry value rather
; than two competing ones.
#define RunValueName "O-view"

[Setup]
; Stable identity for upgrades and the uninstall entry. Never change this GUID.
AppId={{9E5F2B7A-3C4D-4E8A-9F1B-6D2A7C0E4B31}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases

; Per-user install: no admin rights, lands under %LOCALAPPDATA%\Programs\O-view,
; Start Menu shortcut under the user's own programs. PrivilegesRequired=lowest makes
; {autopf} resolve to {localappdata}\Programs and {group} to the per-user Start Menu.
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto

; x64 self-contained payload.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Close a running instance during install/upgrade via Restart Manager (it detects the
; file lock on the exe even though the tray app has no visible top-level window).
CloseApplications=yes
RestartApplications=no

; Brand the setup .exe itself (issue #10 — it defaulted to a generic download
; icon). The installed exe and the uninstall entry already carry the icon via the
; app's embedded ApplicationIcon; this is the icon of Setup.exe in Explorer.
SetupIconFile=..\brand\o-view.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
OutputDir=.
OutputBaseFilename=O-view-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Tasks]
Name: "startup"; Description: "Start {#AppName} automatically when I sign in to Windows"; GroupDescription: "Startup:"

[Files]
; Single self-contained file — PublishSingleFile with IncludeNativeLibrariesForSelfExtract
; embeds the WPF/SQLite native DLLs, so there is nothing else to copy.
Source: "..\publish\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Comment: "Claude usage in the notification area"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"

[Registry]
; Run at startup, pointing at the installed (stable) path. Only written when the user
; ticks the Startup task; removed on uninstall. Same value name as the app's own
; StartupRegistration, so the in-app "Run at startup" toggle stays authoritative.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "{#RunValueName}"; ValueData: """{app}\{#AppExeName}"""; \
    Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName} now"; \
    Flags: nowait postinstall skipifsilent

[UninstallRun]
; The tray process holds the exe open; end it before the uninstaller deletes files.
Filename: "{sys}\taskkill.exe"; Parameters: "/IM {#AppExeName} /F"; \
    Flags: runhidden; RunOnceId: "StopOView"
