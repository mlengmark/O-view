# ADR-0008: Per-user installer for distribution and relaunch

- **Status:** Accepted
- **Date:** 2026-07-22
- **Deciders:** @mlengmark
- **Resolves:** [#7](https://github.com/mlengmark/O-view/issues/7) — "Application not opening"

## Context

O-view shipped as a single loose `O-view.Tray.exe` that the user runs from wherever it downloaded (typically the Downloads folder). That distribution model has a discoverability hole reported in issue #7: **once the tray icon is closed there is nothing to relaunch it.** There is no install location, no Start Menu entry, and no uninstaller.

The app already had run-at-startup ([ADR-0003](0003-windows-tray-constraints.md) item 6, via `HKCU\...\CurrentVersion\Run`), but:

1. It **defaults to off** and is only reachable from the right-click menu — which the user cannot open if the app is closed and cannot be found.
2. It registers `Environment.ProcessPath`, so if the loose exe is moved or the Downloads folder is cleared, the startup entry points at a file that no longer exists.

Both problems are the same root cause: **there is no stable install location and no durable entry point.** The reporter asked specifically for "a start menu executable that can re-launch the program."

## Decision

**Ship a per-user installer built with [Inno Setup](https://jrsoftware.org/isinfo.php)** ([installer/O-view.iss](../../installer/O-view.iss)), produced by the release workflow alongside the existing portable exe.

The installer:

- Installs to `%LOCALAPPDATA%\Programs\O-view` — **per-user, `PrivilegesRequired=lowest`, no elevation.** Consistent with every other O-view surface being per-user (HKCU, `%APPDATA%`), and it means SmartScreen is the only friction, not a UAC prompt.
- Creates a **Start Menu shortcut** — the durable, discoverable entry point the issue asked for.
- Offers an **optional "start automatically when I sign in" task** that writes the same `HKCU\...\Run` value the app's own `StartupRegistration` manages — **the same value name and the same quoted-path format**, so the installer and the in-app toggle stay one authoritative setting rather than two competing ones. It points at the stable installed path, fixing the moved-exe fragility.
- Registers a **Programs & Features / Settings → Apps uninstall entry** (`uninsdeletevalue` also removes the startup value on uninstall).

The portable single-file exe remains a first-class download for users who prefer not to install.

## Alternatives considered

| Option | Assessment |
|---|---|
| **MSIX** | The modern Windows packaging story, with clean install/uninstall and Start Menu integration for free. **Rejected: MSIX cannot be installed without a trusted code-signing certificate.** O-view deliberately ships unsigned — a certificate costs more than a free tool justifies (README, [ADR-0004](0004-clean-room-provenance.md) context) — so MSIX would reintroduce exactly the cost the project chose to avoid. |
| **WiX / MSI** | Capable and standard, but heavier authoring, and a per-user MSI is awkward (MSI is oriented toward per-machine, elevated installs). More ceremony than a single-file tray utility warrants. |
| **First-run self-install** (exe copies itself to `%LOCALAPPDATA%` and creates the shortcut) | No second artifact and no installer toolchain. Rejected: it hides an install behind a "just run the exe" gesture, provides no uninstaller, and duplicates in bespoke C# the file-copy/shortcut/registry/uninstall logic Inno already does correctly. |
| **Install script (`install.ps1` / `--install` flag)** | Minimal to build, but adds a manual step and PowerShell execution-policy friction, and still hand-rolls uninstall. A worse version of the installer. |
| **Documentation only** ("make a shortcut yourself") | Zero engineering, zero product. Does not resolve the report. |

Inno Setup is a build-time-only dependency: it produces an unsigned per-user setup with no runtime footprint, and adds nothing to the portable exe.

## Consequences

**Positive**
- The Start Menu entry makes the app **findable and relaunchable** — the issue's actual request.
- Startup persistence now points at a **stable installed path**, so it survives reboots and a cleared Downloads folder.
- A real **uninstall entry** — previously there was no clean removal path at all.
- Per-user install needs **no admin rights**; SmartScreen remains the only first-run friction, unchanged from the portable exe.
- The portable exe still exists for users who want zero install.

**Negative**
- A **second release artifact** to build and keep working; the release workflow now installs Inno Setup and compiles the script (`choco install innosetup`, then `ISCC.exe`).
- **Still unsigned** — SmartScreen will warn on the installer exactly as it does on the loose exe. Accepted; signing is out of scope for a free tool.
- The `HKCU\...\Run` value is now written by two places (installer task and in-app toggle). Mitigated by making them write an **identical value name and data format** so they are idempotent; the in-app toggle remains authoritative and the round trip is covered by the existing `--startup-on` / `--startup-off` verification hooks in `App.xaml.cs`.
- Inno Setup is Windows-only tooling — a non-issue for a Windows-only product ([ADR-0003](0003-windows-tray-constraints.md)).

## Verification (2026-07-22)

Compiled with Inno Setup 6.7.3 and exercised as a silent install/uninstall round trip on the target machine, not assumed:

| Probe | Result |
|---|---|
| `ISCC.exe /DAppVersion=1.0.0 installer\O-view.iss` compiles | ✅ no warnings |
| Install lands `O-view.Tray.exe` under `%LOCALAPPDATA%\Programs\O-view` | ✅ |
| Start Menu `O-view.lnk` created, target = installed exe | ✅ |
| Startup task writes `HKCU\...\Run` `O-view` = `"…\O-view.Tray.exe"` (matches `StartupRegistration`) | ✅ |
| Uninstall entry present in per-user Programs & Features (v1.0.0) | ✅ |
| Silent uninstall removes install dir, shortcut, Run value, and uninstall entry | ✅ all gone |
