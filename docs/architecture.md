# Architecture

O-view reads Claude's usage from files on this machine and shows it in the notification area on Windows and Linux. This page describes the rules the code follows, and **all new code follows them.** Display text has moved out of Core into Presentation; what's left in Core by design is Core's own provider reports, which compose prose from data they resolve, and `DiagnosticsHint`, a phrase each head supplies once at startup.

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
- **The shell calls skins on the UI thread, always, and only through `IUiDispatcher`.** `ShellHost` is the one place the engine is built, started and disposed; it posts every `IShellToSkin` push onto the dispatcher, including the ones that already arrive on the UI thread, so a skin never has to work out which of the four it can assume the thread on. On Linux, D-Bus callbacks arrive on a thread of their own first: they go through `BusCallback`, which posts to the UI thread and never blocks it, before reaching `ShellHost`'s dispatcher the same way every other push does.

## The two interfaces

- **`ISkinToShell`** — what a skin asks of the shell: the latest snapshot, settings, cached usage, the panel's statistics, and the refresh and weekly-reset commands. `UsageEngine` implements it.
- **`IShellToSkin`** — what the shell tells a skin: show a snapshot, show a notification, and the two "something is due" pushes (an update check, a rate-card check). Both skins implement it; `ShellHost` is what calls it, always through `IUiDispatcher`.

## UsageEngine's pieces

`UsageEngine` now composes the following, each owning one part of what it used to do all itself; `UsageEngine` forwards to them rather than re-implementing, so no caller outside `O-view.App` changed:

- **`UsageProviders`** — the account read, the plan-history and cached-utilization sources, the JSONL fallback, and the composite provider they feed.
- **`PollRunner`** — the off-thread read/publish mechanics and the per-cadence gate, shared by the full poll, the plan poll and the usage-cache refresh.
- **`PlanHistoryPoll`** — the cheap plan-history-only cadence, and the panel's statistics build.
- **`WeeklyResetCoordinator`** — the weekly reset end to end: the manual override, Claude Code's reported instant, and the conflict between the two (ADR-0014).
- **`UsageAlerts`** — the threshold and off-plan notification decisions (the wording itself lives in Presentation's `NoticeText` and `OffPlanText`).
- **`UsageCacheRefresh`** — the floor, freshness gate and billed-charge latch behind the Claude-CLI usage-cache refresh (issue #234).

`ShellHost` sits outside this list: it is the attach point a skin is built from, not a piece `UsageEngine` was carved out of.

## Where platform differences live

They live in whichever implementation gets constructed, never in `#if` blocks or runtime OS checks scattered through the logic. The one deliberate runtime check is data-root discovery (`ClaudeDataRoots`), which lives in a single named place.

## The linux-runtime check

CI's `linux-runtime` job runs a handful of tests under a virtual display (`xvfb-run`) and a real session bus (`dbus-run-session`), with `OVIEW_LINUX_RUNTIME=1` telling them to run rather than return early. A stand-in tray host stands in for a notification area — test code that owns `org.kde.StatusNotifierWatcher` on the bus and talks the real protocol — so the real Linux app's tray icon, click and menu reach it over a real D-Bus connection with Avalonia's own dispatcher on the other end.

It proves a real left click opens the panel end to end, and that the tray menu answers within its budget, both under that virtual display and Avalonia 12.1.3. It does not prove `BusCallback`'s UI-thread hand-off: in this job the click already arrives on the UI thread, so there is no wrong-thread moment to catch. The mocked `BusCallbackTests` prove the hand-off instead, by choosing the thread themselves.

## What enforces this

- **A reference test:** `PresentationBoundaryTests` fails if Presentation's compiled assembly references a UI framework or platform API.
- **A source scan:** `SkinBoundaryTests` fails if either skin's source names `UsageEngine` or any of the classes it split into — a skin may reach the shell only through `ISkinToShell` and `IShellToSkin`.
- **A dispatch test:** `ShellToSkinDispatchTests` fails if any `IShellToSkin` push reaches a skin before `IUiDispatcher` has run it, including the three pushes that already arrive on the UI thread.
- **Golden images:** every shared sample state is rendered for both skins and compared, pixel for pixel, with `tests/golden/` (`tools/check.ps1`, and CI's `visual` job).
- **The smoke test:** the real engine runs against `tests/fixtures/claude-home`, so files on disk must reach the numbers the panel shows.
- **`docs/feature-checklist.md`:** each feature has a test or sample state that proves it.
- **CI's `guard-ratchet` check:** no PR removes a required check, a feature, its proof or a test.
- **CI's `merge-order` check:** parallel PRs never change the same files, and land in their planned order.
- **A reflection test keeps display text out of `OView.Core.Models`:** `CoreModelsHoldNoDisplayTextTests.NoPublicMemberInCoreModelsReadsLikeDisplayCopy` fails on a public member named like copy (ending `Note`, `Label`, `Caption`, `Hint`, `Title`, `Detail`, `Caveat`, `Heading`, `Message`, `Summary`, `Text` or `Instruction`) or a public string constant whose value reads as a sentence.

Changes to the checks themselves, to CI or to dependencies are guarded. They're made only when a phase brief lists them.

## Unchanged from before

These rules all still hold; their reasons are in `CLAUDE.md` and the ADRs:
- read-only against Claude's files;
- no credential handling;
- local machine only;
- de-duplication of usage records by request id;
- weekly resets as a reported constant (ADR-0014);
- no third-party tray library on Windows (ADR-0005).
