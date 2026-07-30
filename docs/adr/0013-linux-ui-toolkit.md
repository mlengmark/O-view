# ADR-0013: Avalonia for a Linux head, alongside the existing WPF head

- **Status:** Accepted
- **Date:** 2026-07-30
- **Deciders:** @mlengmark
- **Evidence:** [findings/linux-tray-spike.md](../findings/linux-tray-spike.md)
- **Follows:** [ADR-0012](0012-linux-support.md) · **Amends:** [ADR-0005](0005-native-tray-integration.md)

## Context

[ADR-0012](0012-linux-support.md) put Linux in scope and deliberately left the toolkit open, because [ADR-0003](0003-windows-tray-constraints.md) had rejected Avalonia on reasoning that was only half expired:

> tray behaviour is precisely where cross-platform frameworks are weakest — the abstraction would leak on the one feature that matters most

That half had not expired, so this decision waited on a spike rather than an opinion. The spike is done and its results are the basis for everything below.

Two facts frame the decision.

**The Windows head is a shipped product on in-place auto-update.** A regression does not wait to be downloaded; it is pushed. Anything that rewrites working Windows UI spends risk on users who already have something that works.

**On Linux the tray is a protocol, not a surface.** StatusNotifierItem over D-Bus, rendered by a *host* — and stock Ubuntu GNOME provides no host at all.

## Decision

### 1. Add an Avalonia head for Linux. Leave the WPF head alone.

`O-view.Core` and `O-view.App` are already shared and platform-neutral ([#73](https://github.com/mlengmark/O-view/issues/73)). The Linux head sits on `O-view.App` exactly as `O-view.Tray` does.

The spike confirmed Avalonia does the thing that actually matters, rather than the thing that is easy to demo: a **live-rendered** `RenderTargetBitmap` works as a tray icon and can be replaced repeatedly on a timer. SNI also accepts a themed icon *name*, which would have been useless for a gauge — that was the first thing checked, and it passed.

**The Windows head is not rewritten.** Consequences of that, accepted deliberately:

- The panel UI exists twice. This codebase's recurring failure mode is duplication, so this is a real cost, not a footnote. It is mitigated by everything below XAML already being shared — formatters, statistics, scope reports, the engine — and issues [#55](https://github.com/mlengmark/O-view/issues/55)/[#56](https://github.com/mlengmark/O-view/issues/56) are the standing reminder of what happens when *facts* get duplicated rather than views.
- The frame-by-frame-measured flyout curves in `FlyoutAnimation.cs`, the `Shell_NotifyIconGetRect` docking, and the WinForms `NotifyIcon` integration all stay as they are, unrisked.

### 2. On a desktop with no SNI host, O-view must say so — and it must ask the bus, not the toolkit

This is the product decision the spike existed to inform, and it is not optional.

**Measured:** an Avalonia `TrayIcon` reports `IsVisible = true` whether or not a host exists. The app's own output is *identical* with and without one. Trusting the toolkit therefore produces an app that is silently invisible on the single most likely Linux configuration — the exact failure [CLAUDE.md](../../CLAUDE.md) rule 6 was written after.

So the Linux head **must**:

1. Probe the session bus for `org.kde.StatusNotifierWatcher` — measured to return `False` with no host and `True` with one.
2. When absent, tell the user plainly what was observed and what to do: that no notification-area host was found on the session bus, that GNOME needs an AppIndicator/KStatusNotifierItem extension, and how to reach the panel meanwhile. **State the observation, never a guess about their machine.**
3. Watch for the name appearing later (`WatchNameOwnerAsync`) and register without a restart — a user may install the extension while O-view is running. This is the Linux `TaskbarCreated` (ADR-0003 item 5).
4. **Never** run that probe synchronously on the UI thread. Measured: blocking the dispatcher on a D-Bus round trip deadlocks the app outright.

Silently invisible is not an acceptable outcome. Neither is a message asserting the extension is missing when what O-view actually observed was an absent bus name.

### 3. The docked flyout is not promised on Linux

SNI exposes no icon rectangle — there is no `Shell_NotifyIconGetRect` equivalent — and Wayland clients generally cannot position their own surfaces. [#78](https://github.com/mlengmark/O-view/issues/78) should implement whatever positioning is genuinely achievable and stop there. A panel that lands in the wrong place every time reads as broken; a plainly-placed window does not.

### 4. ADR-0005's zero-dependency guarantee holds on Windows only

[ADR-0005](0005-native-tray-integration.md) claimed **zero third-party runtime dependencies**, and that was worth having — it is why H.NotifyIcon was dropped.

**It cannot survive on Linux, and the cost is now measured, not estimated:** 25 third-party managed assemblies (21 `Avalonia.*`, plus `SkiaSharp`, `HarfBuzzSharp`, `MicroCom.Runtime`, `Tmds.DBus.Protocol`), two third-party native libraries (`libSkiaSharp.so` 10.7 MB, `libHarfBuzzSharp.so` 2.7 MB), and ~100 MB self-contained against the Windows build's 76.7 MB.

Windows has a first-party tray and a first-party rasteriser. Linux has neither, so this is a property of the platform rather than a choice being made badly.

**Amendment:** ADR-0005's guarantee is hereby scoped to the Windows head, which keeps it in full. The Linux head takes Avalonia and its closure. `Tmds.DBus.Protocol` arriving transitively is a small consolation — the bus probe in decision 2 needs no dependency that Avalonia has not already brought.

## Alternatives considered

| Option | Why rejected |
|---|---|
| **Avalonia for both platforms** | One UI codebase, and the duplication in decision 1 disappears. Rejected: it rewrites a shipped, tuned Windows product — including measured animation curves and the docked-flyout positioning — to serve a platform with no users yet, and would add 25 third-party assemblies plus Skia to a Windows build that today has none. Risk lands on the people who already have something working. **Revisit only if the duplicated panel actually causes drift**, at which point the trade-off has changed. |
| **Direct SNI over `Tmds.DBus.Protocol`, GTK or nothing for the panel** | Most control, no toolkit tax. Rejected: it means hand-writing the SNI object, its properties, its menu protocol and a panel toolkit binding — all of which Avalonia already ships and the spike verified. The dependency saving is illusory, since `Tmds.DBus.Protocol` would be a dependency either way. |
| **Ship Linux with a tray icon only, no panel** | Smaller. Rejected: the panel holds everything the icon cannot say — the model split, the 31-day graph, `Est.` tiles, the data-source badge and the no-data explanation. A coloured dot with no way to see why is not the product. |
| **Wait for GNOME to support SNI natively** | GNOME removed tray icons deliberately and has not reversed it. Waiting is a decision not to ship. |
| **Require the AppIndicator extension, and refuse to run without it** | Honest, but hostile: it makes a third-party GNOME extension a hard dependency of an app the user has already installed. Decision 2 informs instead of refusing. |

## Consequences

**Positive**

- The shipped Windows product is untouched; all new risk sits in new code.
- The spike verified the load-bearing capability (live-rendered, repeatedly-replaced icon) rather than assuming it.
- The GNOME problem is now a known, detectable state with a defined response, instead of a support queue full of "it doesn't appear".
- The bus probe needs no dependency Avalonia has not already introduced.

**Negative**

- **The panel exists twice.** The largest deliberate duplication in the project, in a codebase whose last release was a consolidation of exactly this kind of drift.
- **Zero third-party dependencies is gone on Linux** — 25 assemblies and two native libraries, ~100 MB.
- **Linux will not look like Windows.** No docked flyout, different animation, different notification style. The support matrix ([#83](https://github.com/mlengmark/O-view/issues/83)) must be honest about this rather than implying parity.
- **A large part of the behaviour is still unverified on a real desktop.** Whether hosts draw the gauge legibly, recolour it, survive a shell restart, or deliver clicks are all open — listed in the findings. They gate [#77](https://github.com/mlengmark/O-view/issues/77) and [#78](https://github.com/mlengmark/O-view/issues/78), not this decision.
- Avalonia becomes a version the project must track, including its own Skia and HarfBuzz.
