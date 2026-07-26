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

; Brand the wizard itself. Setup was the one surface still wearing stock Inno
; chrome — a grey placeholder panel and no mark — so it did not read as the same
; product as the app it installs. Generated from brand/o-view-mark.svg; the 2x
; variants exist so a high-DPI display gets a crisp image rather than an upscale.
WizardImageFile=brand\wizard-large.bmp,brand\wizard-large-2x.bmp
WizardSmallImageFile=brand\wizard-small.bmp,brand\wizard-small-2x.bmp

; The large image only appears on the Welcome and Finished pages, and modern style
; suppresses Welcome by default — so without this the branding would show up only
; once the install had already finished. One extra click on a first-time install
; buys a setup that identifies itself; the auto-update path runs /SILENT and never
; sees this page.
DisableWelcomePage=no

[Messages]
; Setup's own copy, in the app's voice rather than Inno's defaults. The stock
; welcome text says nothing about what is being installed; these two lines do.
WelcomeLabel1=Install {#AppName}
WelcomeLabel2=Claude usage and time-to-reset, live in your Windows notification area.%n%nInstalls for your account only — no administrator rights needed, and nothing is written outside your user profile.
FinishedHeadingLabel={#AppName} is installed
FinishedLabelNoIcons=Look for the ring icon in the notification area. Left-click it for the detail panel, right-click for settings.
FinishedLabel=Look for the ring icon in the notification area. Left-click it for the detail panel, right-click for settings.

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
; In-app auto-update relaunch (ADR-0009). When O-view's own updater runs this installer it
; passes "/SILENT /update=1"; the postinstall entry above is skipped in silent mode, so this
; entry (not a postinstall action, therefore it runs even silently) relaunches the app once
; the upgrade is applied. A normal user install does not pass /update, so WantsRelaunch is
; false and this is skipped — no double launch.
;
; Launched THROUGH EXPLORER, not directly. A process started as a child of the installer
; inherits the installer's token and environment; an instance relaunched that way was
; observed to be permanently unable to read %APPDATA%\Claude\plan-usage-history.json —
; File.Exists returned false for a path that was correct, present, and readable by every
; other process of the same user, and it never recovered across hours of 60 s polls. A
; freshly launched instance of the same exe read it immediately. Handing the launch to
; explorer.exe re-parents the app to the shell, so it starts with the ordinary interactive
; user context instead of whatever it inherits mid-install. Explorer returns at once, hence
; nowait and no window flash.
Filename: "{win}\explorer.exe"; Parameters: """{app}\{#AppExeName}"""; \
    Flags: nowait; Check: WantsRelaunch

[Code]
// True only when launched by the in-app updater as "...\O-view-Setup.exe /update=1".
function WantsRelaunch(): Boolean;
begin
  Result := ExpandConstant('{param:update|0}') = '1';
end;

[UninstallRun]
; The tray process holds the exe open; end it before the uninstaller deletes files.
Filename: "{sys}\taskkill.exe"; Parameters: "/IM {#AppExeName} /F"; \
    Flags: runhidden; RunOnceId: "StopOView"
