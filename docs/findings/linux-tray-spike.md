# Finding: Linux tray behaviour — Avalonia + StatusNotifierItem

**Date:** 2026-07-30 · **Spike for:** [#75](https://github.com/mlengmark/O-view/issues/75) · **Feeds:** [ADR-0013](../adr/0013-linux-ui-toolkit.md)

The Linux notification area is not a tray you draw into — it is a **D-Bus protocol**, StatusNotifierItem (SNI), and whether anything renders your icon depends on a *host* being present. This spike measured what an Avalonia `TrayIcon` actually does on Linux, and what happens when no host exists.

## Headline

> **An Avalonia tray icon reports `IsVisible = true` whether or not anything is there to display it.** With no `StatusNotifierWatcher` on the session bus, the app constructs its icon, replaces it on a timer, and runs indefinitely — happy, healthy, and completely invisible.

Stock Ubuntu ships GNOME, and **GNOME provides no SNI host by default**. So the naive implementation produces exactly the failure [CLAUDE.md](../../CLAUDE.md) rule 6 forbids: an app that cannot tell it is not working.

A session-bus probe **does** distinguish the two cases reliably. That is the remedy, and it is measured, not assumed.

## Method, and its limits

Run on `ubuntu-latest` (Ubuntu 24.04.4 LTS, x86_64) under `xvfb-run` + `dbus-run-session` — a real session bus, a real X11 display, and a throwaway Avalonia 12.1.1 app that renders a ring gauge into a `RenderTargetBitmap` and replaces it five times on a 700 ms timer.

Two cases, identical app:

| | Session bus |
|---|---|
| **A** | no `StatusNotifierWatcher` — *stock Ubuntu GNOME, from the app's point of view* |
| **B** | a minimal Python `StatusNotifierWatcher` owning the name — *standing in for the AppIndicator extension, or Plasma* |

**What this cannot answer.** There is no GNOME Shell or Plasma here, so nothing ever *draws* the icon. Everything below is about the protocol and the app's own behaviour. Whether the icon is legible on a real panel, how hosts recolour it, and what sizes they request are all still open — see [Outstanding](#outstanding-needs-a-real-desktop).

## Measured

### 1. Avalonia's `TrayIcon` takes a live-rendered icon, and replacing it works

This was the first thing to establish, because SNI also accepts a *themed icon name* — useless for a gauge whose whole job is to change. A `RenderTargetBitmap` drawn per tick and handed over as a `WindowIcon` worked in both cases:

```
[spike] tray icon constructed and IsVisible=true
[spike] icon replaced tick=1 … tick=5
[spike] spike end: survived 5 icon replacements, process healthy
```

### 2. Registration over D-Bus happens when a host exists

Case B's watcher recorded it:

```
[watcher] owning org.kde.StatusNotifierWatcher
[watcher] REGISTERED item service='org.kde.StatusNotifierItem-3151-0' sender=':1.3'
```

### 3. The app cannot tell the difference — this is the finding that matters

The `[spike]` output for Case A and Case B is **identical**, line for line, including `IsVisible=true`. Avalonia neither throws nor reports failure when there is no host. A build that trusted `IsVisible` would tell the user everything was fine while showing them nothing.

### 4. A session-bus probe does tell the difference

Listing bus names and looking for `org.kde.StatusNotifierWatcher`:

| | names on bus | `StatusNotifierWatcher` present |
|---|---|---|
| **A** — no host | 2 | **False** |
| **B** — host | 4 | **True** |

Reliable, cheap, and available through `Tmds.DBus.Protocol`, which **Avalonia already depends on** via `Avalonia.FreeDesktop` — so the check adds no new dependency.

### 5. The probe must not run on the UI thread

Discovered by breaking it. Calling the probe synchronously from `OnFrameworkInitialized` — blocking the dispatcher on a D-Bus round trip — **deadlocked the app outright**: it printed its two startup lines and hung until the CI timeout killed it, and the watcher recorded `0 item(s) registered`.

Moving the probe before `StartWithClassicDesktopLifetime` fixed it. This is a real constraint on the implementation, not a curiosity.

### 6. What an Avalonia head costs

Self-contained publish of the spike (a *minimal* app — O-view's real head will be larger):

| RID | files | native `.so` | total |
|---|---|---|---|
| `linux-x64` | 219 | 16 | **100 MB** |
| `linux-arm64` | 219 | 16 | **105 MB** |

Third-party **managed** assemblies: 21 `Avalonia.*` plus `SkiaSharp`, `HarfBuzzSharp`, `MicroCom.Runtime`, `Tmds.DBus.Protocol` — **25 in total**.

Third-party **native** libraries: `libSkiaSharp.so` (10.7 MB) and `libHarfBuzzSharp.so` (2.7 MB).

For comparison the Windows single-file build is **76.7 MB with zero third-party dependencies**. [ADR-0005](../adr/0005-native-tray-integration.md)'s "zero third-party runtime dependencies" cannot survive a Linux head; it is measurably, unavoidably gone there. See ADR-0013.

## Identified but not yet built

**Re-registration when a host appears or disappears** — the Linux analogue of `TaskbarCreated` ([ADR-0003](../adr/0003-windows-tray-constraints.md) item 5). `Tmds.DBus.Protocol` exposes `WatchNameOwnerAsync(string)`, which is the right mechanism: watch `org.kde.StatusNotifierWatcher` and re-register when ownership changes. Not exercised by this spike; belongs to [#77](https://github.com/mlengmark/O-view/issues/77).

This matters more on Linux than on Windows, because a GNOME user can install the AppIndicator extension *while O-view is running* — and the app must pick it up without a restart.

## Outstanding — needs a real desktop

None of these can be answered on a headless runner. They are the reason ADR-0013 records the toolkit decision as settled while leaving the on-device behaviour open.

| Question | Why it matters |
|---|---|
| Does GNOME Shell (with AppIndicator) and Plasma actually **draw** the gauge, legibly? | The whole product is a glance. Legibility was measured for Windows at 16/20/24 px; Linux hosts commonly request 22–24 px and 48 px on HiDPI. |
| Do hosts **recolour or theme** the icon? | Several do. Colour is already never the sole signal — the arc's sweep encodes the number — so this should degrade rather than break, but it needs confirming. |
| Does the icon survive a **shell restart** or the extension being toggled? | The `WatchNameOwnerAsync` mechanism above is untested. |
| **Wayland**: can the panel be positioned at all? | The spike ran on X11 (`WAYLAND_DISPLAY` unset). Ubuntu 22.04+ defaults to Wayland, where clients generally cannot position their own surfaces. SNI also exposes no icon rectangle — there is no `Shell_NotifyIconGetRect` equivalent — so the docked flyout is likely not reproducible. Drives [#78](https://github.com/mlengmark/O-view/issues/78). |
| Do **Activate / ContextMenu** reach the app? | No host, so nothing to click. Avalonia requires a `NativeMenu` rather than an Avalonia `Menu`, which has design consequences for the existing menu. |

## Reproducing

The spike lived on the throwaway branch `spike/linux-tray-75` (PR [#90](https://github.com/mlengmark/O-view/pull/90), closed unmerged) and was deleted per #75. It was a ~120-line Avalonia app, a ~70-line Python `StatusNotifierWatcher`, and a workflow running both cases under `xvfb-run dbus-run-session`. Nothing from it ships.
