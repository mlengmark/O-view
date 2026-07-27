# CLAUDE.md — O-view

Guidance for AI coding assistants working in this repository. Read this before writing code.

## What this is

A **Windows 11 notification-area (system tray) application** that displays Claude AI token usage and time until the next usage-limit reset.

Status: **all five build phases complete** (see [docs/build-plan.md](docs/build-plan.md)) — the app is working end to end. The ADRs in `docs/adr/` are decided, not drafts — follow them. If you believe one is wrong, say so and propose superseding it; do not silently deviate.

## Hard rules

### 1. Windows only

Target `net10.0-windows`. Use Win32 and WPF APIs directly. **Do not** add cross-platform abstractions, `#if` platform guards, or macOS/Linux considerations. Windows-only is a decision ([ADR-0003](docs/adr/0003-windows-tray-constraints.md)), not an unfinished state.

Watch for macOS assumptions leaking in from AI training data, because the closest prior art is a Mac app. Specifically **wrong on Windows**:

| Wrong (macOS) | Right (Windows) |
|---|---|
| Tray icon can show a text label | **It cannot.** `Shell_NotifyIcon` takes an `HICON` only. Text must be rasterised into a 16/20/24 px bitmap. |
| Keychain | DPAPI / Windows Credential Manager |
| `~/Library/Application Support` | `%APPDATA%` / `%LOCALAPPDATA%` |
| LaunchAgents / `SMAppService` | `HKCU\...\CurrentVersion\Run` |
| `NSPopover` anchors itself | Position manually via `Shell_NotifyIconGetRect` + work area |
| Menu bar item always visible | Windows 11 hides new tray icons in the overflow flyout |

### 2. Clean-room — do not read existing usage monitors

This project was inspired by the *product concept* of existing macOS menu-bar apps that track AI token usage (macOS/Swift). **Do not read, clone, fetch, or browse the source code** of any such app, and do not reproduce its naming or file layout. If asked "how does [an existing usage monitor] do X", decline and reason from Windows/.NET documentation instead. Full policy: [ADR-0004](docs/adr/0004-clean-room-provenance.md).

Those macOS designs are not merely encumbered — they are *incorrect* for this platform.

### 3. v1 handles no credentials — keep it that way

**`OAuthUsageProvider` is deferred out of v1** ([ADR-0007](docs/adr/0007-plan-history-primary-provider.md)). The primary source is a local file Claude Desktop already maintains, so v1 needs no token, no network call, and no credential handling at all. Do not reintroduce auth to solve a problem a local file already solves.

If OAuth is ever built: never log the token, persist it, include it in exception messages, or write it to diagnostics. Memory only.

**Never write to `%APPDATA%\Claude\plan-usage-history.json`** — it belongs to another application. Read-only, always.

### 4. De-duplicate JSONL by `requestId`

Non-negotiable, and the single easiest way to ship a silently broken app. Assistant records are written multiple times as responses stream; the sample file had **28 records for 12 distinct `requestId`s** — a naive sum overcounts by ~2.3×. Group by `requestId`, keep the last occurrence. Details: [docs/findings/jsonl-schema.md](docs/findings/jsonl-schema.md).

**The id has two spellings.** Claude Code transcripts write `requestId`; **Cowork** audit logs write `request_id` on an otherwise identical record. Reading only one spelling ingests *nothing* from the other source — no error, just an empty tile. See rule 9.

### 5. No third-party tray library — and free the GDI handle

Tray integration uses the **first-party `System.Windows.Forms.NotifyIcon`** (`<UseWPF>` + `<UseWindowsForms>` together). Do not add H.NotifyIcon or any other tray package; the dependency was evaluated and deliberately dropped ([ADR-0005](docs/adr/0005-native-tray-integration.md)). WinForms is for `NotifyIcon` **only** — no WinForms controls, forms, or `Application.Run`.

`Bitmap.GetHicon()` allocates an unmanaged GDI handle that `Icon` does not own. **Call `DestroyIcon` on every icon refresh** or the process leaks a handle per update — a slow leak in an app designed to run for days.

**Icon design (measured, not assumed):** the tray icon is the O-view **brand mark** — a colour-coded **ring gauge with a centre pupil** (the "eye"), **no digits**. Colour carries urgency (green <50, amber 50–69, red ≥70; `OView.Core.UsageLevels`), the exact number lives in the tooltip. The history: digits alone were the spike's winner, but a ring *plus* digits starved each other at 16 px, so when product direction required a graph the digits were dropped and the ring kept (GitHub issue #1). The pupil (added 2026-07-22 to unify the tray icon with the exe icon) is **not** a second signal — it carries no number and takes the arc's colour, so it coexists with the ring where digits could not. Scale the geometry to the icon size — never hard-code, a fixed size clips at some DPI scales. See [findings/tray-icon-rendering.md](docs/findings/tray-icon-rendering.md) and [IconRenderer.cs](src/O-view.Tray/Tray/IconRenderer.cs).

### 6. Never fabricate a number

If data is unavailable, show a neutral icon and explain in the popup. If data is estimated (JSONL-derived), label it **"local estimate"**. A monitoring tool that confidently displays a wrong number is worse than one that admits uncertainty.

Five specific applications of this rule:

- **"Est. value" tiles are not money charged.** Within plan limits the marginal cost is £0; these figures price tokens at public API rates. Always prefix `Est.`
- **Partial history must state its coverage** — `3 of 31 days recorded`. A small 31-day number without that caveat reads as low usage rather than short history.
- **Days before install have no data, not zero data.** Never render them as zero-height bars in the graph.
- **An unpriced model yields a labelled partial, not a blank tile.** `CostEstimator` has no rate for a model Anthropic released after the table was last updated, so `PanelStatistics` sums what it *can* price and names the rest in `UnpricedModels` (`excludes claude-x (no published rate)`); it returns null only when **nothing** was priceable. Do **not** "restore" the older rule of nulling the whole total on any unpriced model — that blanked both Est. tiles for every user the moment one new model id appeared (it did, for `claude-opus-5`), with no explanation. And never invent a rate to fill the gap: look it up, or leave it labelled. `<synthetic>` is not a model — it is Claude Code's marker for locally generated messages, so it is **free (0)**, not unpriced.
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
| **Cowork** | `%APPDATA%\Claude\local-agent-mode-sessions\…\<session>\audit.jsonl` |
| Chat | **none — no local usage record exists** |

Two traps, both silent:

- Each Cowork sandbox contains a `.claude\projects` directory that is **always empty**. Its presence makes a projects-only scan look like it succeeded.
- That tree contains a **broken directory junction**. `Directory.GetFiles(..., AllDirectories)` aborts the entire walk on it and `DirectoryNotFoundException` derives from `IOException`, so the usual catch turns one bad folder into "no transcripts on this machine". Always enumerate per-directory — use `TranscriptFileScan`, don't hand-roll it again.

"Claude Desktop" is **not** the dividing line — Claude Code sessions hosted in Desktop write to the normal user-profile location. Cowork is the odd one out, and chat is the one that genuinely cannot be measured. Don't write UI copy that blames "Desktop".

## Prerequisites

**.NET 10 SDK `10.0.302` is installed and verified** (2026-07-20), including `Microsoft.WindowsDesktop.App 10.0.10` for WPF. Verified end to end: a `net10.0-windows` WPF project scaffolds and builds clean, and **`H.NotifyIcon.Wpf 2.4.1` resolves and builds** against .NET 10 — so the ADR-0001 tray dependency is confirmed viable, not assumed.

Available: `git`, `gh` (authenticated as `mlengmark`), `dotnet` 10.0.302. Not available: Node, Rust. The `python` on PATH is the non-functional Microsoft Store alias stub — do not use it for tooling scripts; use PowerShell or C#.

Older runtimes (3.1, 6.0) are also present on the machine. Ignore them; always target `net10.0-windows`.

## Intended structure

```
O-view.sln
├── src/
│   ├── O-view.Core/      # Providers, rollup store, window math — no UI, no Win32
│   └── O-view.Tray/      # WPF + WinForms NotifyIcon, icon rendering, popup
└── tests/
    └── O-view.Core.Tests/  # xUnit
```

The full UI contract is [docs/ui-spec.md](docs/ui-spec.md) — read it before building any panel.
Phased work breakdown with acceptance criteria: [docs/build-plan.md](docs/build-plan.md).

Keep `Core` free of UI and Win32 dependencies so the accounting logic stays testable without a desktop session.

### Provider design

```
IUsageProvider
 ├─ PlanHistoryProvider  (primary)   → session/weekly %, derived reset times
 ├─ OAuthUsageProvider   (deferred)  → post-v1 enhancement only
 ├─ JsonlUsageProvider   (fallback)  → local token counts, offline (Claude Code + Cowork)
 └─ CompositeUsageProvider           → resolution, caching, source labelling
```

Resolution: fresh plan-history → OAuth if it ever exists → JSONL (labelled estimate) → no data. See [ADR-0002](docs/adr/0002-usage-data-providers.md) and [ADR-0007](docs/adr/0007-plan-history-primary-provider.md).

**Reset times are derived, not reported:** detect a decrease in `fh` of ≥2 points, then `next = last drop + 5h`, re-anchoring on each new drop. Before any drop is observed the reset time is genuinely **unknown** — show it as unknown rather than guessing.

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

## Reference docs

- [docs/adr/](docs/adr/) — decisions and their rationale
- [docs/findings/jsonl-schema.md](docs/findings/jsonl-schema.md) — verified local data schema
