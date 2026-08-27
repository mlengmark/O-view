# CLAUDE.md — O-view

Guidance for AI coding assistants working in this repository. Read this before writing code.

## What this is

A **desktop notification-area (system tray) application** that displays Claude AI token usage and time until the next usage-limit reset. **Windows 11 and Linux are both supported targets as of v0.6.0** ([ADR-0012](docs/adr/0012-linux-support.md)).

Status: all build phases complete (see [docs/build-plan.md](docs/build-plan.md)). Windows has been working end to end and in daily use since v0.1.0. Linux shipped in v0.6.0 and **has now been run on a physical desktop exactly once** — Arch-based, KDE Plasma, Wayland, tarball install, reporting against v0.6.1.

That one report is the whole of what anyone has seen, and it splits three ways:

- **Confirmed working:** data resolution (every path, 5,645 of 5,645 plan-history samples, Cowork logs, account tier) and the tray icon appearing.
- **Fixed, never re-tested — four of them:** the first left click deadlocked the app (#124) and the menu took tens of seconds (#125), both found by the report and fixed in v0.6.4; then reading that code turned up a third nobody had hit, the panel dismissing itself when a compositor refuses to focus it (#129), fixed in v0.6.5. A second report then found the left click **segfaulting** — Avalonia raises SNI callbacks on the D-Bus thread and the head built its window there (#143); the menu items had the same defect and were fixed with it. All four are reasoned from the code and guarded by structural tests, but **no test can reach a live session bus and a dispatcher together** — which is exactly how the first one shipped, and how the fourth shipped past the fix for the first.
- **Still never observed:** the panel (nobody has seen it — it was blocked first behind the deadlock, then behind the crash), **including its work-area corner placement, added in #144 and never seen on a compositor**; the tooltip, notifications, theme following, X11, GNOME without an AppIndicator extension, and the `.deb` on hardware.

**Released is not the same as verified, and neither is fixed. Do not describe Linux panel or theme behaviour as working, and do not describe #124, #125, #129 or #143 as confirmed fixed** until a re-test lands. The README's support matrix carries these three states deliberately, and rule 6 applies to our own claims about our own app as much as to usage numbers.

**Threading on the Linux head is not a detail.** Anything the session bus raises — `TrayIcon.Clicked`, `NativeMenuItem.Click` — arrives on Tmds.DBus's thread, because `DBusHelper` clears the `SynchronizationContext` when it builds the connection. Touching the toolkit from there is a SIGSEGV, not an exception, so no `try/catch` will save it and no log will name it. Route every such callback through `BusCallback` (`Post`, never a blocking invoke — waiting on the UI thread from a bus reply is #124 pointed the other way).

The ADRs in `docs/adr/` are decided, not drafts — follow them. If you believe one is wrong, say so and propose superseding it; do not silently deviate. ADR-0009 carries a worked example of why: its Linux amendment asserted that `apt upgrade` handled updates, which was untrue in both halves and survived undetected until the release gate.

## Hard rules

### 1. Two platforms — and the layer decides what you may use

**Windows 11 and Linux** (Ubuntu 22.04+ / Debian 12+, x64 + arm64). **macOS is out of scope.** This is [ADR-0012](docs/adr/0012-linux-support.md), which supersedes *only* the target scope of [ADR-0003](docs/adr/0003-windows-tray-constraints.md) — ADR-0003's icon analysis and its ten platform behaviours still govern the Windows head.

Where code lives decides what it may touch:

| Layer | Target | May use |
|---|---|---|
| `O-view.Core` | `net10.0` | BCL and SQLite only. No UI, no Win32, no `System.Drawing`, no `Microsoft.Win32`. **Must build and pass its tests on Linux.** |
| `O-view.App` | `net10.0` | The same. Orchestration only; platform behaviour arrives through injected interfaces. |
| `O-view.Tray` — Windows head | `net10.0-windows` | Win32, WPF, WinForms `NotifyIcon`, **directly. No abstraction tax.** |
| `O-view.Linux` — Linux head | `net10.0` | D-Bus, StatusNotifierItem, Avalonia + SkiaSharp ([ADR-0013](docs/adr/0013-linux-ui-toolkit.md)). |

Resolve platform differences by **which implementation gets constructed**, not with `#if` or `OperatingSystem.IsLinux()` sprinkled through logic. Where a runtime check genuinely is the simplest correct answer — data-root discovery is the real case — it lives in exactly one named place, and an ADR says where.

> **All four layers now exist.** The rule that binds hardest is still the first row — nothing Windows-specific enters `Core` or `App` — and CI enforces it by building and testing both on `ubuntu-latest`. The second-hardest is that neither head may grow logic the other needs: see *Intended structure* below.

Watch for macOS assumptions leaking in from AI training data, because the closest prior art is a Mac app. None of the left column is right on **either** target:

| Wrong (macOS) | Right (Windows) | Right (Linux) |
|---|---|---|
| Tray icon can show a text label | **It cannot.** `Shell_NotifyIcon` takes an `HICON` only. Text must be rasterised into a 16/20/24 px bitmap. | **Also cannot.** StatusNotifierItem takes an icon name or ARGB32 pixmap. |
| Keychain | DPAPI / Windows Credential Manager | Secret Service (libsecret) — and v1 still handles no credentials at all (rule 3) |
| `~/Library/Application Support` | `%APPDATA%` / `%LOCALAPPDATA%` | XDG: `~/.config`, `~/.local/share` |
| LaunchAgents / `SMAppService` | `HKCU\...\CurrentVersion\Run` | `~/.config/autostart/*.desktop` |
| `NSPopover` anchors itself | Position manually via `Shell_NotifyIconGetRect` + work area | **Worse than manual:** SNI cannot report where its icon was drawn, so *anchoring to the icon* is impossible. A work-area **corner** is achievable and is what ships (#144) — `Screens` gives both rectangles and `WorkAreaPlacement` is shared with Windows. Setting the position is still only a request a compositor may refuse |
| Menu bar item always visible | Windows 11 hides new tray icons in the overflow flyout | **GNOME has no tray at all** without a third-party AppIndicator extension |

### 2. Clean-room — do not read existing usage monitors

This project was inspired by the *product concept* of existing macOS menu-bar apps that track AI token usage (macOS/Swift). **Do not read, clone, fetch, or browse the source code** of any such app, and do not reproduce its naming or file layout. If asked "how does [an existing usage monitor] do X", decline and reason from first-party platform documentation instead — Microsoft's for Windows, and the freedesktop/XDG specifications for Linux. Full policy: [ADR-0004](docs/adr/0004-clean-room-provenance.md).

Those macOS designs are not merely encumbered — they are *incorrect* for this platform.

### 3. v1 handles no credentials — keep it that way

**`OAuthUsageProvider` is deferred out of v1** ([ADR-0007](docs/adr/0007-plan-history-primary-provider.md)). The primary source is a local file Claude Desktop already maintains, so v1 needs no token, no network call, and no credential handling at all. Do not reintroduce auth to solve a problem a local file already solves.

If OAuth is ever built: never log the token, persist it, include it in exception messages, or write it to diagnostics. Memory only.

**Never write to `%APPDATA%\Claude\plan-usage-history.json`** — it belongs to another application. Read-only, always.

### 4. De-duplicate JSONL by `requestId`

Non-negotiable, and the single easiest way to ship a silently broken app. Assistant records are written multiple times as responses stream; the sample file had **28 records for 12 distinct `requestId`s** — a naive sum overcounts by ~2.3×. Group by `requestId`, keep the last occurrence. Details: [docs/findings/jsonl-schema.md](docs/findings/jsonl-schema.md).

**The id has two spellings.** Claude Code transcripts write `requestId`; **Cowork** audit logs write `request_id` on an otherwise identical record. Reading only one spelling ingests *nothing* from the other source — no error, just an empty tile. See rule 9.

### 5. Tray integration — the rules differ by head

**Windows: no third-party tray library, and free the GDI handle.** Tray integration uses the **first-party `System.Windows.Forms.NotifyIcon`** (`<UseWPF>` + `<UseWindowsForms>` together). Do not add H.NotifyIcon or any other tray package; the dependency was evaluated and deliberately dropped ([ADR-0005](docs/adr/0005-native-tray-integration.md)). WinForms is for `NotifyIcon` **only** — no WinForms controls, forms, or `Application.Run`.

`Bitmap.GetHicon()` allocates an unmanaged GDI handle that `Icon` does not own. **Call `DestroyIcon` on every icon refresh** or the process leaks a handle per update — a slow leak in an app designed to run for days.

**Linux: ADR-0005's zero-dependency guarantee does not extend here** ([ADR-0013](docs/adr/0013-linux-ui-toolkit.md) scopes it to Windows). There is no first-party option: the head uses Avalonia, SkiaSharp and `Tmds.DBus.Protocol`. Two things about it are counter-intuitive and both are measured, not assumed:

- **An Avalonia `TrayIcon` reports `IsVisible = true` whether or not a notification-area host exists.** The toolkit cannot be asked whether the icon is actually anywhere. Ask the *bus* — `SniHostProbe` checks for an owner of `org.kde.StatusNotifierWatcher` — and tell the user when there is none, because silently invisible is indistinguishable from broken ([findings](docs/findings/linux-tray-spike.md)).
- **Probe before the toolkit starts.** Blocking Avalonia's UI thread on a D-Bus round trip deadlocks the app outright. `Program.Main` does it first, deliberately.

Do not "simplify" either back out.

**Icon design (measured, not assumed):** the tray icon is the O-view **brand mark** — a colour-coded **ring gauge with a centre pupil** (the "eye"), **no digits**. Colour carries urgency (green <50, amber 50–69, red ≥70; `OView.Core.UsageLevels`), the exact number lives in the tooltip. The history: digits alone were the spike's winner, but a ring *plus* digits starved each other at 16 px, so when product direction required a graph the digits were dropped and the ring kept (GitHub issue #1). The pupil (added 2026-07-22 to unify the tray icon with the exe icon) is **not** a second signal — it carries no number and takes the arc's colour, so it coexists with the ring where digits could not. Scale the geometry to the icon size — never hard-code, a fixed size clips at some DPI scales. See [findings/tray-icon-rendering.md](docs/findings/tray-icon-rendering.md) and [IconRenderer.cs](src/O-view.Tray/Tray/IconRenderer.cs).

### 6. Never fabricate a number

If data is unavailable, show a neutral icon and explain in the popup. If data is estimated (JSONL-derived), label it **"local estimate"**. A monitoring tool that confidently displays a wrong number is worse than one that admits uncertainty.

Five specific applications of this rule:

- **"Est. value" tiles are not money charged.** Within plan limits the marginal cost is £0; these figures price tokens at public API rates. Always prefix `Est.`
- **Partial history must state its coverage** — `3 of 31 days recorded`. A small 31-day number without that caveat reads as low usage rather than short history.
- **Days before install have no data, not zero data.** Never render them as zero-height bars in the graph.
- **An unpriced model yields a labelled partial, not a blank tile.** `CostEstimator` has no rate for a model Anthropic released after the table was last updated, so `PanelStatistics` sums what it *can* price and names the rest in `UnpricedModels` (`excludes claude-x (no published rate)`); it returns null only when **nothing** was priceable. Do **not** "restore" the older rule of nulling the whole total on any unpriced model — that blanked both Est. tiles for every user the moment one new model id appeared (it did, for `claude-opus-5`), with no explanation. And never invent a rate to fill the gap: look it up, or leave it labelled. `<synthetic>` is not a model — it is Claude Code's marker for locally generated messages, and it is **dropped at parse time by `TranscriptReader`**, so it never reaches the store, the estimator or the breakdown. That filter is the single treatment: do not add a "price it at 0" or "call it Local" branch downstream, which is what issue #57 removed — those were unreachable and disagreed with the reader on case sensitivity. Measured on real transcripts, every synthetic record carries all-zero usage, so storing them would add nothing to any total while inflating `RequestCount` with messages no model produced.
- **Never assert something about the user's machine that O-view hasn't observed.** The "no usage data" banner once read *"Install and run the Claude Desktop app"* at a user who had it open — a file O-view cannot read is not evidence the app is absent. State the observation and the path checked ([ADR-0010](docs/adr/0010-post-update-relaunch.md)).

### 7. Ingestion must be idempotent

The rollup store ([ADR-0006](docs/adr/0006-local-rollup-store.md)) is re-fed from JSONL on every poll. Upsert by natural key or track a per-file watermark — never blind `INSERT`. Together with `requestId` de-duplication this is one of two silent double-counting bugs the design is exposed to; **both need explicit tests.**

### 8. Read the fields that actually hold data

Account info comes from `~/.claude.json` → `oauthAccount` (no token, no network). **Tier is `organizationType`** (e.g. `claude_pro`). `seatTier` and `userRateLimitTier` are empty strings on the dev account — the obvious-looking fields are the wrong ones and silently render a blank badge.

### 9. Tokens come from two places, and chat is neither

Local token counts have **two** sources, and scanning one of them is the historical bug ([findings/cowork-audit-logs.md](docs/findings/cowork-audit-logs.md), issue #44):

| Surface | Transcript |
|---|---|
| Claude Code (CLI **and** hosted in Desktop) | `%USERPROFILE%\.claude\projects\**\*.jsonl` |
| **Cowork**, older builds | `<claude-data-root>\local-agent-mode-sessions\…\<session>\audit.jsonl` |
| **Cowork**, current builds | `%USERPROFILE%\.claude\projects\**\*.jsonl` — the **same files as Claude Code** |
| Chat | **none — no local usage record exists** |

**Location is not authorship, and this table is why that had to be said twice.** Cowork now runs
its sessions through Claude Code, so its transcripts land in the Claude Code row. Reading the
table as "a file under `.claude\projects` is Claude Code usage" is what made `JsonlUsageProvider`
label by locator — and on the development machine that reported `Cowork: 0 rows` while **28 of 30
transcripts there, 107.7 MB of 107.9 MB, belonged to registered Cowork sessions** (issue #218).

The authority on which surface wrote a transcript is Cowork's own register:
`<claude-data-root>\claude-code-sessions\…\local_<id>.json` names the `cliSessionId` its session
writes under, and that id is the transcript's file name. `CoworkSessionIndex` is the one place
that match is made; do not re-derive a surface from a path.

Three traps, all silent:

- **`<claude-data-root>` is not always `%APPDATA%\Claude`.** Desktop ships as MSIX and Windows redirects it into `%LOCALAPPDATA%\Packages\<family>\LocalCache\Roaming\Claude`. Use `ClaudeDataRoots` — never hard-code the canonical path, which is how O-view once reported "no usage data" at a user running Desktop. Roots can mirror each other; scanning the union is safe because ingestion de-duplicates on request id.

- Each Cowork sandbox contains a `.claude\projects` directory. It was documented here as **always
  empty**, and that was wrong: a session that runs Claude Code inside its sandbox writes its
  transcript there. Measured — 4 such files on the dev machine, **38** on the machine that
  reported #218, none of them ever scanned (issue #224). `CoworkAuditLocator` therefore takes
  `*.jsonl` under a session root, not the single name `audit.jsonl`.

  It stayed invisible because a plain recursive enumeration of that tree returns nothing — it
  contains the broken junction below — so a hand-rolled check agreed with this paragraph and only
  `TranscriptFileScan` disagreed. Its presence still makes a projects-only scan look like it
  succeeded, which is the original trap; it is now *also* a place real usage hides.
- That tree contains a **broken directory junction**. `Directory.GetFiles(..., AllDirectories)` aborts the entire walk on it and `DirectoryNotFoundException` derives from `IOException`, so the usual catch turns one bad folder into "no transcripts on this machine". Always enumerate per-directory — use `TranscriptFileScan`, don't hand-roll it again.

"Claude Desktop" is **not** the dividing line — Claude Code sessions hosted in Desktop write to the normal user-profile location. Cowork is the odd one out, and chat is the one that genuinely cannot be measured. Don't write UI copy that blames "Desktop".

## Prerequisites

**.NET 10 SDK `10.0.302` is installed and verified** (2026-07-20), including `Microsoft.WindowsDesktop.App 10.0.10` for WPF. Verified end to end: a `net10.0-windows` WPF project scaffolds and builds clean.

Available: `git`, `gh` (authenticated as `mlengmark`), `dotnet` 10.0.302. Not available: Node, Rust. The `python` on PATH is the non-functional Microsoft Store alias stub — do not use it for tooling scripts; use PowerShell or C#.

Older runtimes (3.1, 6.0) are also present on the machine. Ignore them. Target **`net10.0`** everywhere except `O-view.Tray`, which is WPF and must be `net10.0-windows`. Everything else carries nothing Windows-specific and must build on Linux; CI enforces that on `ubuntu-latest`.

**The development machine is Windows, and no Linux hardware is available here.** The Linux head is therefore verified by unit tests, by offscreen render hooks (`--samples`, `--panel-samples`, `--probe`, `--diagnose` — all of which run with no display and no bus), and by container installs in the packaging workflow. None of that is a desktop.

One hardware report exists (see *What this is* above) and it covers the tray icon and data resolution only. **Do not describe Linux panel or theme behaviour as verified, and do not treat the #124, #125 and #129 fixes as confirmed** — they have never run against a live session bus and a dispatcher together. The README's support matrix distinguishes *seen working* from *never observed* from *fixed but not re-tested* deliberately, and rule 6 applies to our own claims about our own app.

`--diagnose` and `--probe` earned their keep here: the single report was legible enough to diagnose two distinct bugs without a follow-up question. When adding a Linux code path that a desktop could break, ask what a bug report against it would need to contain, and make sure one round trip can carry it.

## Intended structure

```
O-view.slnx
├── src/
│   ├── O-view.Core/      # net10.0        Providers, rollup store, window math — no UI, no Win32
│   ├── O-view.App/       # net10.0        Orchestration: UsageEngine, update policy, diagnostics,
│   │                     #                shared rendering geometry and panel text. Platform
│   │                     #                behaviour arrives through injected interfaces only.
│   ├── O-view.Tray/      # net10.0-windows  WPF + WinForms NotifyIcon, icon rendering, popup
│   └── O-view.Linux/     # net10.0        Avalonia + SkiaSharp, SNI over D-Bus, freedesktop
│                         #                notifications, XDG autostart, portal theme
├── tests/
│   ├── O-view.Core.Tests/   # xUnit, 425
│   ├── O-view.App.Tests/    # xUnit, 184
│   └── O-view.Linux.Tests/  # xUnit,  40
└── packaging/linux/build.sh  # .deb + tarball; the same script CI runs
```

Those counts are a **snapshot, not a contract** — measured 2026-08-21, with #163 merged, and
nothing enforces them. They are here for the shape they show (Core carries most of the logic,
and that is the design working) rather than as figures to cite. If one matters to an argument,
run the suite and use what it says; do not quote this block as evidence. The ADRs' own test
counts are different and correctly so — those record what was true when each decision was
taken.

**Both heads are thin.** Anything a Linux user and a Windows user would expect to behave
identically — what the countdown says, which release asset to offer, what the panel's text
reads — belongs in `App`, not duplicated into each head. `PanelText` and `TrayIconGeometry`
exist because that duplication had already started.

On Linux, build the projects, not the solution: `O-view.Tray` is WPF and will not build there.

The full UI contract is [docs/ui-spec.md](docs/ui-spec.md) — read it before building any panel.
Phased work breakdown with acceptance criteria: [docs/build-plan.md](docs/build-plan.md).

Keep `Core` free of UI and Win32 dependencies so the accounting logic stays testable without a desktop session.

### Provider design

```
IUsageProvider
 ├─ PlanHistoryProvider       (primary)  → session/weekly %, derived reset times
 ├─ CachedUtilizationProvider (primary)  → session/weekly % AND exact reset times, from
 │                                         Claude Code's ~/.claude.json cache
 ├─ OAuthUsageProvider        (deferred) → post-v1 enhancement only
 ├─ JsonlUsageProvider        (fallback) → local token counts, offline (Claude Code + Cowork)
 └─ CompositeUsageProvider               → resolution, caching, source labelling
```

Resolution: authoritative percentages → OAuth if it ever exists → JSONL (labelled estimate) → no data. See [ADR-0002](docs/adr/0002-usage-data-providers.md) and [ADR-0007](docs/adr/0007-plan-history-primary-provider.md).

**Within a tier, the most accurate reading wins — not the first one listed.** Two sources now report the same meters (Desktop's sampled series and Claude Code's cache), so `CompositeUsageProvider` picks the snapshot that carries **more meters** first, and among equals the one **captured most recently**; ties fall back to argument order so the result stays deterministic. Desktop samples every ~5 minutes while the cache refreshes on use, so neither is reliably fresher and neither gets a standing preference. Do not reintroduce a fixed precedence between them.

**Snapshots are chosen whole, never merged field-by-field.** A session figure from one source beside a weekly figure from another describes an account state that existed at no instant, under a single `Source` label that can only be true of one of them — a rule 6 fabrication that looks entirely real on screen.

**The percentages are no longer Desktop-only.** Claude Code caches the figures behind `/status` → Usage in `~/.claude.json` → `cachedUsageUtilization`, so a machine with no Claude Desktop can fill the top two bars from a local file — the population that used to see two permanently empty gauges. Same rules as every other source: local, read-only, no token, no network (rule 3). Details and the shape: [findings/cached-usage-utilization.md](docs/findings/cached-usage-utilization.md).

**Reset times are derived unless Claude Code has reported them.** Prefer a reported instant whenever one exists, and derive only as the fallback:

- **Reported** — `cachedUsageUtilization.utilization.{five_hour,seven_day}.resets_at`. Exact, so it carries **zero uncertainty** and must render without the `~` that marks an approximation. `UsageEngine.WithReportedResets` folds these onto whichever snapshot wins the chain, *overriding* both the derived value and the user's entered one — those are attempts to recover a number this states outright. It is the only exact reset time O-view has ever had.
- **Derived** — the fallback, and unchanged. Detect a decrease of ≥2 points, anchor on it, step forward by the window length: **5h** from `fh`, **7 days** from `sd` ([ADR-0011](docs/adr/0011-weekly-reset-derivation.md); both lengths are measured, and 72h for the weekly window is disproved). Before any drop is observed the reset time is genuinely **unknown** — show it as unknown, never guessed.

**A cached percentage whose window has rolled over is not a reading.** Claude Code refreshes that file while it runs, so leave it closed across a boundary and it still reports the old window's figure — 91% for a window that reset to nothing hours ago. Each bar carries its own `resets_at`, so this is checkable; check it, and report unknown rather than the stale number. And **never step a passed `resets_at` forward** to the next window: for the five-hour window that rebuilds the grid bug #180 removed, and for the weekly one it dresses an inference in a reported value's zero uncertainty.

Two things about the weekly one are easy to "tidy up" back into the bug they fix:

- **A drop across a gap in Desktop's sampling is a real reset, not a restart snap.** Weekly resets land overnight and Desktop is closed then, so rejecting gap-crossing drops rejects *every* observation — which is exactly why the panel showed no weekly reset for weeks. What the gap costs is precision, so the observation is stored as the bracket `(previous sample, drop sample]`, predicted from its upper bound, and marked `~` in the UI.
- **Observed resets live in `%LOCALAPPDATA%\O-view\weekly-resets.json`, not in the rollup store.** The store is a rebuildable cache and wipes itself on corruption (rule 7 / issue #16); a weekly reset cannot be rebuilt and costs a week to re-observe. Do not move it back.

## Build order

Deliberately **not** in order of importance — in order of ascending unknowns:

1. ~~Icon rasterisation spike~~ — **done, and its conclusion has since been superseded twice.** The shipped icon carries **no digits**: it is the ring-gauge brand mark (ring + pupil) with the number in the tooltip. Rule 5 above is the current design. The spike's "digits only" result is history — digits were dropped when product direction required the icon to show a graph (issue #1). See [findings](docs/findings/tray-icon-rendering.md), whose later sections are marked historical for the same reason.
2. ~~Token-discovery spike~~ — **no longer needed.** Superseded by `PlanHistoryProvider` ([ADR-0007](docs/adr/0007-plan-history-primary-provider.md)).
2b. **`PlanHistoryProvider`** — parse `%APPDATA%\Claude\plan-usage-history.json`, plus the reset-drop detector. This is the primary source; build it first.
3. **`JsonlUsageProvider`** — no auth, no network, fully testable. First test is the `requestId` de-duplication test.
4. **Rolling-window math** — 5h window rolls from first use, not a wall clock. UTC throughout.
4b. **Rollup store** (ADR-0006) — SQLite daily aggregates; idempotency test alongside.
5. **Tray shell** — icon, tooltip, popup positioning, `TaskbarCreated` re-registration, single-instance mutex.
6. **`OAuthUsageProvider`** — backoff with jitter, `retry-after`, nullable-everything parsing, ≥5 min polling.
7. **Polish** — notifications, startup registration, settings, publish.

## Conventions

- C# nullable reference types enabled; treat warnings as errors in `Core`
- All external data (JSONL fields, HTTP responses) parsed defensively — assume every field can be absent
- Times stored and computed in UTC; converted to local only at the display edge
- Tests use fixtures derived from real session files, with any identifying content scrubbed

### Version numbers

Tags are `vMAJOR.LARGE.MINOR` — major generation, substantial change, fix. **Not semver**: this
is an application, nothing consumes it as an API, and no vector carries a breaking-change
contract. Higher numbers are newer, compared **left to right, numerically per vector**.

Four rules bind, and three of them exist because of a failure mode rather than a preference:

- **Never compare a version as text.** `0.6.10` is newer than `0.6.9`; a string comparison says
  the opposite, and the result is not an error — every installed copy reports "up to date"
  forever. `ReleaseVersion` parses each vector to an `int`, and `ReleaseVersionTests` pins the
  width-crossing cases (9→10, 99→100) in all three positions. Any new code that touches version
  ordering goes through `ReleaseVersion`; do not hand-roll a comparison.
- **No cap on a vector.** Any non-negative integer. There is no 0–99 ceiling, because nothing
  enforces one and a cap only ever forces a higher vector to move for no reason.
- **No leading zeros.** `release.yml` rejects them. `0.06.10` parses to 0.6.10, so the app would
  report one string while the published asset names — built from the raw tag — carry another.
- **Publish in ascending order.** The updater reads `releases/latest`, which is the most recently
  *published* release, not the highest-numbered. A fix published to an older line after a newer
  release is offered to everyone as an upgrade. ADR-0009 leans on this being true.

## Reference docs

- [docs/adr/](docs/adr/) — decisions and their rationale
- [docs/findings/jsonl-schema.md](docs/findings/jsonl-schema.md) — verified local data schema
