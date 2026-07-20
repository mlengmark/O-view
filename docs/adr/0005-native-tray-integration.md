# ADR-0005: Native tray integration — drop H.NotifyIcon

- **Status:** Accepted
- **Date:** 2026-07-20
- **Deciders:** @mlengmark
- **Supersedes:** the H.NotifyIcon dependency choice in [ADR-0001](0001-tech-stack.md) (that ADR otherwise stands)

## Context

[ADR-0001](0001-tech-stack.md) selected **H.NotifyIcon.Wpf** for notification-area integration, on the reasoning that WPF has no first-party tray control. That reasoning was incomplete.

**What H.NotifyIcon is:** a community open-source library (MIT, maintained by HavenDV), successor to the older `Hardcodet.NotifyIcon.Wpf`. Functionally it is **a P/Invoke wrapper around a single Win32 function, `Shell_NotifyIcon`**, plus XAML/DataContext binding, its own popup and balloon-tip handling, and helper attached properties.

Two observations undermine the original choice:

1. **We would use very little of it.** [ADR-0003](0003-windows-tray-constraints.md) already commits O-view to rendering its own icon bitmaps and positioning its own popup via `Shell_NotifyIconGetRect`. The library's XAML binding, popup, and balloon features are all things we have decided to implement ourselves. We would take a full dependency to use roughly the wrapper alone.

2. **A first-party option exists.** WPF has no tray control, but **`System.Windows.Forms.NotifyIcon` is Microsoft-maintained and ships inside `Microsoft.WindowsDesktop.App`** — the runtime the project already requires. A WPF app consumes it by enabling both `<UseWPF>` and `<UseWindowsForms>`, which is a supported configuration, not a workaround.

Every added dependency is a supply-chain surface, an upgrade obligation, and a potential abandonment risk. For a small always-running utility handling an auth token, minimising that surface has real value.

## Decision

**Use the first-party `System.Windows.Forms.NotifyIcon`.** Do not take a dependency on H.NotifyIcon or any other third-party tray library.

```xml
<UseWPF>true</UseWPF>
<UseWindowsForms>true</UseWindowsForms>
```

**Retain the `ITrayHost` abstraction** mandated by ADR-0001. Its purpose is unchanged, only its fallback target: if WinForms interop ever proves limiting, the implementation swaps to direct `Shell_NotifyIcon` P/Invoke without touching callers.

WPF remains the framework for the popup window and all visible UI. WinForms is used **only** for `NotifyIcon` — no WinForms controls, forms, or `Application.Run`.

## Verification (2026-07-20)

Measured on the target machine (.NET SDK 10.0.302), not assumed:

| Probe | Result |
|---|---|
| `NotifyIcon` instantiated in a `net10.0-windows` WPF+WinForms project | ✅ OK |
| `NotifyIcon.Text` maximum length | **127 chars** (ADR-0003 said 128 — corrected) |
| Planned tooltip `5h: 47% · resets 16:32 · 7d: 61%` | 32 chars — fits comfortably |
| `Shell_NotifyIconGetRect` resolves from `shell32.dll` | ✅ (P/Invoke fallback is viable) |
| `Bitmap` → `GetHicon()` → `Icon.FromHandle()` | ✅ 16×16 |
| `DestroyIcon` releases the handle | ✅ |

## Alternatives considered

| Option | Assessment |
|---|---|
| **H.NotifyIcon.Wpf** (original choice) | Verified working on .NET 10 (v2.4.1), well maintained, and a reasonable library. Rejected only because it is unnecessary: it duplicates work we have already decided to do ourselves, in exchange for a third-party dependency. |
| **Direct `Shell_NotifyIcon` P/Invoke** | Zero dependencies, maximum control. Rejected as the default because it requires hand-rolling a hidden message window, `NOTIFYICONDATA` marshalling, version negotiation, and `TaskbarCreated` re-registration — roughly 300 lines of fiddly interop that WinForms already provides, correctly, for free. **Retained as the documented fallback behind `ITrayHost`.** |
| **WinUI 3 tray support** | No first-party tray primitive; would reintroduce a third-party dependency and a heavier deployment story. |

## Consequences

**Positive**
- **Zero third-party runtime dependencies** for the tray surface — the original goal of this reconsideration
- The tray code path is Microsoft-maintained and has ~20 years of production hardening
- `NotifyIcon` manages the hidden message window and re-registers after Explorer restarts, which resolves [ADR-0003](0003-windows-tray-constraints.md) item 5 with no bespoke code. **Still verify this behaviour explicitly** — it is relied upon, so it warrants a test rather than trust.
- `NotifyIcon.Icon` accepts a `System.Drawing.Icon`, exactly what the rasteriser produces — no conversion layer

**Negative**
- Pulls WinForms assemblies into a WPF app, modestly increasing self-contained output size. Acceptable: they come from the already-required WindowsDesktop runtime, not a new package.
- Two UI frameworks in one process. Contained by confining WinForms strictly to `NotifyIcon`.
- `NotifyIcon` exposes no modern toast API; its balloon tips use the legacy path. Toast notifications, if implemented, need a separate mechanism — and were already flagged as possibly deferred in ADR-0003.
- **`GetHicon()` allocates an unmanaged GDI handle that `Icon` does not own.** Every icon refresh must call `DestroyIcon`, or the app leaks a handle per update — a slow leak in a process designed to run for days. This is a mandatory code-review checkpoint.
