# Architecture

O-view reads Claude's usage from files on this machine and shows it in the notification area on Windows and Linux. This page describes the rules the code follows. Some older code doesn't follow them yet; it is being moved over. **All new code follows them.**

## Layers

References run one way: **skins → App → Presentation → Core**. A project may use only the ones to its right. Core uses none of them, and Presentation never uses App or a skin.

| Project | Holds | Never holds |
|---|---|---|
| `O-view.Core` | Reading Claude's files, the usage store, pricing, reset and divergence detection, usage snapshots and statistics | Display text, formatting, colours, UI or platform APIs |
| `O-view.Presentation` | Everything a user reads or sees: wording, number and date formats, colour bands, icon geometry, and view models for the panel, tooltip and menu | WPF, Avalonia, Win32, D-Bus; anything platform-specific |
| `O-view.App` | The shell: polling, settings, alerts, updates, and the two interfaces to the skins | Formatting; any knowledge of which skin is running |
| `O-view.Tray` | The Windows skin: XAML layout and styles, `NotifyIcon`, the registry, the Windows updater | Formatting or business logic |
| `O-view.Linux` | The Linux skin: Avalonia layout and styles, StatusNotifierItem over D-Bus, XDG autostart | Formatting or business logic |

**The test for any change:** a new panel tile is added once, in Presentation. Each skin then needs only layout to show it. If a change needs the same logic written in both skins, it belongs in Presentation instead.

## How data flows

    Claude's files ──▶ Core (providers ▶ snapshot + statistics)
                         │
                         ▼
                       App (poll loop, settings, alerts) ──▶ Presentation (view models, text)
                         │                                        │
                         ▼                                        ▼
                    IShellToSkin  ◀── skins render view models ◀──┘
                    ISkinToShell  ──▶ user commands (open panel, toggle setting, exit)

- **Snapshots are chosen whole**, from one source at a time, and never merged field by field.
- **Every value says what it is** (real, estimated or unknown). Unknown is shown as unknown, never as zero.
- **The shell calls skins on the UI thread, always.** On Linux, D-Bus callbacks arrive on another thread: they go through `BusCallback`, which posts to the UI thread and never blocks it.

## Where platform differences live

They live in whichever implementation gets constructed, never in `#if` blocks or runtime OS checks scattered through the logic. The one deliberate runtime check is data-root discovery (`ClaudeDataRoots`), which lives in a single named place.

## What enforces this

- **A reference test:** Presentation references no UI framework or platform API.
- **Golden images:** every shared sample state is rendered for both skins and compared, pixel for pixel, with `tests/golden/` (`tools/check.ps1`, and CI's `visual` job).
- **The smoke test:** the real engine runs against `tests/fixtures/claude-home`, so files on disk must reach the numbers the panel shows.
- **`docs/feature-checklist.md`:** each feature has a test or sample state that proves it.
- **CI's `guard-ratchet` check:** no PR removes a required check, a feature, its proof or a test.
- **CI's `merge-order` check:** parallel PRs never change the same files, and land in their planned order.
- **Once display text has moved out of Core,** a reflection test keeps it out.

Changes to the checks themselves, to CI or to dependencies are guarded. They're made only when a phase brief lists them.

## Unchanged from before

These rules all still hold; their reasons are in `CLAUDE.md` and the ADRs:
- read-only against Claude's files;
- no credential handling;
- local machine only;
- de-duplication of usage records by request id;
- weekly resets as a reported constant (ADR-0014);
- no third-party tray library on Windows (ADR-0005).
