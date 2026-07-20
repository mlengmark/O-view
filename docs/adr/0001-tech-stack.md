# ADR-0001: Technology stack — .NET 10 + WPF

- **Status:** Accepted
- **Date:** 2026-07-20
- **Deciders:** @mlengmark

## Context

O-view is a Windows notification-area application that must:

- Render a **dynamically generated** tray icon (usage % drawn into a bitmap) and update it every few minutes
- Show a lightweight popup panel anchored near the taskbar
- Read local JSON Lines files and make periodic HTTPS calls
- Idle for hours at negligible CPU and memory cost
- Distribute as a small, self-contained executable to a non-developer audience

Idle resource cost matters more than usual here: this app runs all day doing nothing most of the time. A heavyweight runtime is a poor trade for a widget that displays two numbers.

The development machine has `git`, `gh` (authenticated), and the `dotnet` host — but **no .NET SDK and no Node or Rust toolchain**.

## Decision

Build O-view as a **.NET 10 (LTS) WPF** application, C#, targeting `net10.0-windows`.

Supporting choices:

- **[H.NotifyIcon.Wpf](https://github.com/HavenDV/H.NotifyIcon)** for notification-area integration — WPF has no first-party tray control, and this library is the maintained, WPF-native option
- **System.Drawing / GDI+** for rasterising the tray icon bitmap
- **xUnit** for tests
- **Single-file, self-contained** publish (`win-x64`)

### Why .NET 10 rather than .NET 8

Earlier planning assumed .NET 8. Revised: as of mid-2026, **.NET 8 LTS support ends in November 2026** — starting a new project on a runtime with roughly four months of support remaining would mean an immediate migration. .NET 10 is the current LTS with support into 2028.

## Alternatives considered

| Option | Why rejected |
|---|---|
| **Rust + Tauri** | Smallest binaries and lowest memory, genuinely attractive. Rejected because it requires installing *both* the Rust toolchain and Node (neither present), and drawing text into a tray icon means hand-rolling rasterisation. The binary-size win does not pay for the setup and implementation cost at this scope. |
| **Python + pystray** | Fastest prototype. Rejected: the machine's `python` is the non-functional Microsoft Store alias stub, and PyInstaller output routinely triggers antivirus false positives — unacceptable for a distributed tray app. |
| **Electron / Node** | Familiar web UI. Rejected: ~150 MB and high idle RAM for a widget showing two numbers, and no Node installed. Directly contradicts the idle-cost requirement. |
| **WinForms instead of WPF** | `NotifyIcon` is first-party in WinForms, which is a real advantage. Rejected because the popup panel wants modern layout, animation, and per-monitor DPI handling, all of which are markedly better in WPF. H.NotifyIcon closes the tray gap. |
| **WinUI 3 / Windows App SDK** | Most modern stack, best toast-notification story. Rejected as primary: heavier deployment story (framework dependency or bulky self-contained output) and more friction for unpackaged apps. Reconsider only if toast notifications become a headline feature. |

## Consequences

**Positive**
- Zero new toolchains beyond the SDK itself
- Native access to every Windows API the project needs (Shell, DPAPI, registry, per-monitor DPI) without interop shims
- Strong typing and a mature test story for the window-arithmetic logic, which is where correctness bugs will live

**Negative**
- Self-contained single-file output is ~60–90 MB. Mitigate with trimming; accept as the cost of a no-install binary.
- **The .NET 10 SDK must be installed before the first build** — currently only the 3.1 and 6.0 *runtimes* are present.
- Tray support comes from a third-party dependency (H.NotifyIcon). Contain the risk by keeping all usage behind an internal `ITrayHost` abstraction so it can be swapped for direct `Shell_NotifyIcon` P/Invoke if the library stalls.
- Windows-only by construction. This is intended — see [ADR-0003](0003-windows-tray-constraints.md).
