# ADR-0003: Windows-only target and notification-area design constraints

- **Status:** Accepted
- **Date:** 2026-07-20
- **Deciders:** @mlengmark

## Context

The reference product that inspired O-view is a **macOS menu-bar app**. The macOS menu bar and the Windows notification area are not equivalent surfaces, and designing against the macOS model produces a product that cannot be built on Windows.

The decisive difference:

> **macOS:** `NSStatusItem` exposes a `title` property. Text renders natively in the menu bar at variable width. `"47% · 2h14m"` is a supported, trivial case.
>
> **Windows:** `Shell_NotifyIcon` accepts an **`HICON` only** — 16×16, 20×20, or 24×24 px depending on DPI. **There is no text label.** Any text must be rasterised into the icon bitmap.

At 16×16 px roughly **two characters** are legible. The macOS presentation is not reproducible, and no amount of effort makes it so.

## Decision

**Target Windows 11 exclusively.** No cross-platform abstraction layer, no macOS or Linux target, and no design borrowed from the macOS model. Windows platform APIs may be used directly throughout.

### Design consequences of the icon constraint

Information is tiered across three surfaces by available space:

| Surface | Capacity | Content |
|---|---|---|
| **Tray icon** | ~2 glyphs | **2 digits only, no ring** (`47`), digit colour green → amber → red. No `%` sign — the glyph budget is better spent on digits. At 100% use a full-ring `!` symbol rather than 3 digits. *(Revised — see below.)* |
| **Tooltip** | **127 chars** | `5h: 47% · resets 16:32 · 7d: 61%` (32 chars) |

> **Revised 2026-07-20 by spike.** This ADR originally specified a ring gauge *plus* digits. Measurement showed the ring is counterproductive at 16 px: it consumes the outer ~25% of the canvas, forcing the font from 13.5 px down to 9.5 px and rendering the digits mushy. The ring only ever duplicated the number already shown. **Digits-only is the design.** Evidence and measurements: [findings/tray-icon-rendering.md](../findings/tray-icon-rendering.md).
>
> **Revised 2026-07-21 by product direction.** The icon must carry a **% graph**, not digits alone. Implemented as digits plus a **~3 px proportional fill bar along the bottom edge** — the graph requirement satisfied without the ring's cost; legibility re-verified at 16/24 px on both themes. The ring remains rejected.
>
> **Revised again 2026-07-21 (GitHub issue #1).** Digits-plus-bar read as cluttered at 16 px. Adopted a **ring-only gauge — a proportional arc, no digits.** This does *not* reverse the spike: the spike rejected ring **plus** digits because they starved each other; **removing the digits removes that conflict**, letting the ring use the whole canvas. The exact percentage moves to the tooltip. Colour bands set by issue #2: green <50, amber 50–69, red ≥70 (a shared `UsageLevels` classifier, so the icon and popup cannot drift apart). Ring legibility verified at 16/24 px on both themes, all three bands. This supersedes the two revisions above.
>
> Tooltip capacity also corrected: `NotifyIcon.Text` caps at **127** characters, not 128.
| **Popup panel** | Unconstrained | Full breakdown, token counts, model split, data-source badge, settings |

**Colour must never be the sole signal** — the gauge fill level and the digits carry the same information, for colour-blind users and for monochrome/high-contrast taskbar themes.

### Platform behaviours that must be handled explicitly

These have no macOS analogue and are each a real work item:

1. **Overflow flyout.** Windows 11 hides new tray icons behind the chevron by default. The app is effectively invisible on first run. Requires first-run onboarding telling the user how to pin it.
2. **Popup positioning.** No `NSPopover` equivalent. The taskbar can sit on any screen edge, on any monitor, at any DPI. Use `Shell_NotifyIconGetRect` plus work-area geometry, with a fallback to the cursor position.
3. **Per-monitor DPI v2.** The icon must be re-rendered at the correct pixel size when moved between monitors of differing scale.
4. **Theme changes.** Light/dark taskbar changes do not restyle the icon automatically. Watch for the setting change and re-render.
5. **Explorer restarts.** When Explorer crashes or restarts, the tray icon is destroyed. Listen for the `TaskbarCreated` message and re-register, or the app silently vanishes.
6. **Startup.** `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (per-user, no elevation). Not LaunchAgents.
7. **Single instance.** A named mutex; two instances would mean two icons and double polling.
8. **Credential storage.** DPAPI / Windows Credential Manager. Not Keychain.
9. **Paths.** `%APPDATA%` / `%LOCALAPPDATA%`. Not `~/Library/Application Support`.
10. **Distribution.** Unsigned executables trigger SmartScreen. No notarisation equivalent; an EV certificate is expensive. Ship a portable exe and document the warning honestly.

### Explicitly out of scope

- Multi-provider support (the reference tracks 60+; O-view tracks Claude)
- A bundled CLI
- Browser cookie extraction — Chrome/Edge App-Bound Encryption makes this both fragile and improper
- macOS or Linux builds

## Alternatives considered

| Option | Why rejected |
|---|---|
| **Cross-platform (Avalonia/MAUI)** | Adds abstraction cost for platforms with no requirement, and tray behaviour is precisely where cross-platform frameworks are weakest — the abstraction would leak on the one feature that matters most. |
| **Taskbar toolbar / deskband** | Would allow wide text like macOS. Rejected: the deskband API is **deprecated and non-functional on Windows 11**. |
| **Widgets board / desktop widget** | More display space. Rejected: not always-visible, which defeats an at-a-glance monitor. |
| **Text-free icon (gauge only)** | Cleaner at 16px. Rejected: users want the number, and the spike confirmed digits *are* legible at 16 px (13.5 px font) — so there is no legibility argument for dropping them. |
| **Ring gauge + digits** (as originally specified here) | Rejected after measurement: the ring starves the digits of space at 16 px. See the revision note above. |

## Consequences

**Positive**
- Direct use of Win32/WPF APIs with no abstraction tax
- Design is honest about the platform rather than fighting it
- Clear scope boundary prevents drift toward a cross-platform rewrite

**Negative**
- Nothing is reusable for a future macOS version — accepted; that is not a goal
- The tiered-information design must be validated by a **rasterisation spike before UI work begins**. If two digits are illegible at 16×16, the icon design changes and this ADR is superseded.
- Items 1–7 above are individually small but collectively a meaningful slice of the build. They are easy to underestimate because they are all invisible when they work.
