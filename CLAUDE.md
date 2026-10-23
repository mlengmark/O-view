# CLAUDE.md

Guidance for anyone, human or AI, working in this repository. Read it before writing code.

## What this is

A desktop notification-area app that shows Claude usage and the time until the next usage-limit reset, for Windows 11 and Linux ([ADR-0012](docs/adr/0012-linux-support.md)). What the app does and shows is in the [README](README.md); what has actually been seen working on each platform is in [docs/platform-status.md](docs/platform-status.md), and nothing else restates it. Do not describe Linux behaviour as verified unless that page says it was.

The ADRs in `docs/adr/` are decided, not drafts. Follow them. If you believe one is wrong, say so and propose superseding it rather than deviating quietly.

## Hard rules

### 1. Two platforms, and the layer decides what you may use

Windows 11 and Linux (Ubuntu 22.04+, Debian 12+, x64 and arm64). macOS is out of scope.

| Layer | Target | May use |
|---|---|---|
| `O-view.Core` | `net10.0` | The BCL and SQLite only. No UI, no Win32, no `System.Drawing`, no `Microsoft.Win32`. Must build and pass its tests on Linux. |
| `O-view.App` | `net10.0` | The same. Orchestration only; platform behaviour arrives through injected interfaces. |
| `O-view.Presentation` | `net10.0` | Everything a user reads or sees, shared by both heads: wording, number and date formats, a view model per panel section, tooltip and menu, and the sample states the goldens render. Core only, never WPF or Avalonia; a test enforces it. |
| `O-view.Tray`, the Windows head | `net10.0-windows` | Win32, WPF and WinForms `NotifyIcon`, directly. |
| `O-view.Linux`, the Linux head | `net10.0` | D-Bus, StatusNotifierItem, Avalonia and SkiaSharp ([ADR-0013](docs/adr/0013-linux-ui-toolkit.md)). |

The layering, with the panel's content built once per section in Presentation and each head only laying it out, is in [docs/architecture.md](docs/architecture.md). The test for any change: a new panel section is added once, in Presentation, and each head needs only layout to show it. Anything a Linux user and a Windows user would expect to behave identically belongs in App or Presentation, never in both heads.

Resolve platform differences by which implementation gets constructed, not with `#if` or `OperatingSystem.IsLinux()` in logic. The one deliberate runtime check is data-root discovery, in `ClaudeDataRoots`.

Watch for macOS assumptions, because the closest prior art is a Mac app and none of its answers are right on either target:

| Wrong (macOS) | Windows | Linux |
|---|---|---|
| A tray icon can show a text label | It cannot. `Shell_NotifyIcon` takes an `HICON`; text has to be drawn into a bitmap | It cannot either. StatusNotifierItem takes an icon name or an ARGB pixmap |
| `~/Library/Application Support` | `%APPDATA%` and `%LOCALAPPDATA%` | XDG: `~/.config` and `~/.local/share` |
| LaunchAgents | `HKCU\...\CurrentVersion\Run` | `~/.config/autostart/*.desktop` |
| A popover anchors itself to its item | Position it yourself from `Shell_NotifyIconGetRect` and the work area | SNI cannot report where the icon was drawn, so anchoring is impossible; the panel takes the work-area corner, and placing it is a request a compositor may refuse |
| The menu bar item is always visible | Windows 11 hides new tray icons in the overflow flyout | GNOME has no tray at all without an AppIndicator extension |

**Before every PR, run `powershell -File tools/check.ps1`.** It builds, runs every test including the smoke test against `tests/fixtures/claude-home`, renders both panels' sample states, and compares them pixel by pixel with `tests/golden`. A change that alters what a panel looks like updates the goldens with `-UpdateGolden` and shows them in the PR. The PR format is `.github/pull_request_template.md`; CI's `pr-format`, `merge-order` and `guard-ratchet` checks enforce it, and [docs/feature-checklist.md](docs/feature-checklist.md) lists what must never silently break.

### 2. Clean room

The project was inspired by the product concept of macOS menu-bar apps that track AI token usage. Do not read, clone or browse the source of any such app, and do not reproduce its naming or layout. Reason from first-party platform documentation instead: Microsoft's for Windows, freedesktop and XDG for Linux. Full policy: [ADR-0004](docs/adr/0004-clean-room-provenance.md).

### 3. No subscription credentials, ever

O-view handles no Claude subscription credential: no token, no cookie, no keychain item, no sign-in button ([ADR-0015](docs/adr/0015-no-credential-based-usage-sources.md)). The reason is policy: using a Free, Pro or Max credential in a third-party product is prohibited by Anthropic's consumer terms. That Claude Code's token location is now documented changes nothing.

Two source categories are permitted, and both keep the credential inside the vendor's own client:

| Permitted | Example |
|---|---|
| Read a file Claude's own apps write | `plan-usage-history.json`, `~/.claude.json`, transcripts |
| Invoke Claude's own client and read what it produces | `claude -p "/usage"` ([findings](docs/findings/cli-usage-refresh.md)) |

O-view sends nothing to Anthropic and holds no credential, but it does cause Claude Code to make a request, and a network trace will show it. `SECURITY.md` states this; keep it stated. The invocation was measured at zero tokens, and O-view checks after every run that it stayed free; if a charge is ever detected the feature stops itself. An argument Claude Code does not recognise reaches the model and costs real tokens, so the argument is exactly the slash command and nothing else.

Never write to Claude's files. `plan-usage-history.json` and `~/.claude.json` belong to other applications and are read-only, always.

### 4. De-duplicate transcripts by request id

Assistant records are written several times as a response streams, so a naive sum overcounts by about 2.3×. Group by request id and keep the last occurrence ([findings/jsonl-schema.md](docs/findings/jsonl-schema.md)). The id has two spellings: Claude Code writes `requestId`, Cowork audit logs write `request_id`. Reading only one ingests nothing from the other, with no error.

### 5. Tray integration differs by head

**Windows:** first-party `System.Windows.Forms.NotifyIcon`, no third-party tray package ([ADR-0005](docs/adr/0005-native-tray-integration.md)). WinForms is for `NotifyIcon` only. `Bitmap.GetHicon()` allocates a GDI handle `Icon` does not own, so call `DestroyIcon` on every refresh or the process leaks one handle per update. `WpfNativeLibraries.Pin()` loads WPF's native DLLs at startup and must stay: the single-file exe extracts them to `%TEMP%`, a tray app does not touch them until the first click, and temp cleaners delete them in between.

**Linux:** Avalonia, SkiaSharp and `Tmds.DBus.Protocol`; the zero-dependency rule is scoped to Windows ([ADR-0013](docs/adr/0013-linux-ui-toolkit.md)). Two measured facts: an Avalonia `TrayIcon` reports `IsVisible = true` whether or not a notification-area host exists, so `SniHostProbe` asks the bus and the app tells the user when there is none; and the probe runs before the toolkit starts, because blocking Avalonia's UI thread on a D-Bus round trip deadlocks the app. Everything the session bus raises arrives on Tmds.DBus's thread, and touching the toolkit from there is a segmentation fault, not an exception. Route every such callback through `BusCallback` with `Post`, never a blocking invoke.

**The icon** is the brand mark: a colour-coded ring gauge with a centre pupil and no digits. Green below 50%, amber from 50%, red from 70% (`UsageLevels`); the exact number lives in the tooltip. Scale the geometry to the icon size; never hard-code one ([findings](docs/findings/tray-icon-rendering.md)).

### 6. Never fabricate a number

If data is unavailable, show the neutral icon and explain in the panel. If data is estimated, label it "local estimate". This is enforced in the types: a session percentage, a reset time, a token count and a modelled USD figure are each a status-paired type (`UsagePercent`, `UsageInstant`, `TokenCount`, `EstimatedUsd`) carrying `Real`, `Estimated` or `Unavailable`, never a bare nullable, and `CoreModelsHoldNoBareNullablesTests` fails if one is reintroduced.

- "Est." figures are not money charged; always prefix `Est.`
- Partial history states its coverage: `3 of 31 days recorded`.
- Days before install have no data, not zero data; never draw them as zero-height bars.
- An unpriced model yields a labelled partial, never a blank tile, and never an invented rate. `<synthetic>` is not a model; `TranscriptReader` drops it at parse time, and that is the only treatment.
- Never assert something about the user's machine that O-view has not observed. State what was checked and what was found.

### 7. Ingestion must be idempotent

The rollup store ([ADR-0006](docs/adr/0006-local-rollup-store.md)) is re-fed from transcripts on every poll. Upsert by natural key or track a per-file watermark; never blind-insert. With rule 4 this is one of two silent double-counting bugs the design is exposed to, and both have explicit tests.

### 8. Read the fields that actually hold data

Account info comes from `~/.claude.json` under `oauthAccount`. The tier is `organizationType`; `seatTier` and `userRateLimitTier` are empty strings and would render a blank badge.

### 9. Tokens come from two places, and two surfaces write nothing local

| Surface | Transcript |
|---|---|
| Claude Code, CLI or hosted in Desktop | `~/.claude/projects/**/*.jsonl` |
| Claude Code sub-agents | one directory below the session file, under `subagents/` |
| Cowork, older builds | `<claude-data-root>/local-agent-mode-sessions/.../*.jsonl` |
| Cowork, current builds running locally | the same files as Claude Code |
| Cowork running in a cloud container | nothing reachable |
| Chat | nothing |

Which surface wrote a transcript is decided by Cowork's own session register, through `CoworkSessionIndex`, never from the path. Sub-agent transcripts are found only because `TranscriptFileScan` walks recursively; do not narrow that scan. `<claude-data-root>` is not always `%APPDATA%\Claude`: a packaged Desktop is redirected into its package store, and `ClaudeDataRoots` is the one place that is known. Each Cowork sandbox contains a `.claude/projects` tree with a broken directory junction in it; `Directory.GetFiles` aborts on it, so always enumerate per directory, which `TranscriptFileScan` does.

A Cowork session in a cloud container leaves no local record while the plan meters still move. When a user reports that the token tiles have stopped, that is the first explanation to rule in, and no wider scan fixes it.

## Provider design

```
IUsageProvider
 ├─ PlanHistoryProvider        Claude Desktop's sampled series: session and weekly %, derived session reset
 ├─ CachedUtilizationProvider  Claude Code's cache: the same two %, plus exact reset instants
 ├─ JsonlUsageProvider         local token counts from transcripts (Claude Code and Cowork)
 └─ CompositeUsageProvider     resolution and source labelling

 ClaudeCliRefresher            not a provider: asks Claude Code to refresh its cache by running /usage
```

- Within a tier the most accurate reading wins: the snapshot carrying more meters, then the more recently captured one. Do not reintroduce a fixed precedence between Desktop and Claude Code.
- Snapshots are chosen whole, never merged field by field.
- Do not write Claude Desktop's sampling interval into code as a constant. It was 5 minutes, then 15, and it is undocumented. Derive it from the samples.
- The weekly reset is a reported constant on a static grid ([ADR-0014](docs/adr/0014-weekly-reset-is-a-reported-constant.md)): take `seven_day.resets_at`, persist it as an anchor, project it forward by whole weeks, and never show it with a `~`. The 5-hour window rolls from first use, stays derived from drops in the sampled series, and is still bracketed. Never project a passed `five_hour.resets_at` forward.
- A cached percentage whose own window has rolled over is not a reading; report unknown rather than the stale figure.
- Before anything is known, a reset time is unknown. Show it as unknown and say what the user can do about it.

## Prerequisites

The .NET 10 SDK. On Linux, build the projects rather than the solution: `O-view.Tray` is WPF and will not build there. Python is not used for tooling in this repository; scripts are PowerShell or C#, and the CI gate scripts run under Python on the runner only.

The Linux head is verified by unit tests, by the offscreen render hooks (`--samples`, `--panel-samples`, `--probe`, `--diagnose`, which need no display and no bus), by CI's `linux-runtime` job under a virtual display with a real session bus, and by container installs in the packaging workflow. When adding a Linux code path a desktop could break, ask what a bug report against it would need to contain, and make sure `--diagnose` and `--probe` can carry it in one round trip.

## Structure

```
O-view.slnx
├── src/
│   ├── O-view.Core/            Providers, the rollup store, pricing, window maths. No UI, no Win32.
│   ├── O-view.Presentation/    Wording, formatting and a view model per panel section; the sample states.
│   ├── O-view.App/             The shell: UsageEngine and its pieces, ShellHost, the shared rendering rules
│   │                           (PanelFit, PanelMetrics, PanelPlacementMemory, TrayIconGeometry, PanelPalette).
│   ├── O-view.Tray/            The Windows head: WPF, NotifyIcon, the registry, the updater.
│   └── O-view.Linux/           The Linux head: Avalonia, SNI over D-Bus, notifications, XDG autostart.
├── tests/
│   ├── O-view.Core.Tests/, O-view.Presentation.Tests/, O-view.App.Tests/, O-view.Linux.Tests/
│   ├── O-view.TestSupport/     Fakes and temp directories shared by the test projects.
│   ├── fixtures/claude-home/   The smoke test's hand-written Claude home.
│   └── golden/                 The rendered sample states both heads are compared with.
├── tools/check.ps1             The gate: build, test, render, compare.
└── packaging/linux/build.sh    The .deb and tarball, the same script CI runs.
```

Test counts are not recorded here; run the suite for a number.

## Conventions

- Nullable reference types on; warnings are errors in `Core`.
- Parse all external data defensively; assume every field can be absent.
- Times are stored and computed in UTC and converted to local only at the display edge.
- Tests use fixtures derived from real session files, with identifying content scrubbed.
- Code comments explain why. No issue keys, ADR numbers or phase names in code; GitHub issue numbers are fine where a comment explains a fix.
- No em dashes anywhere in the repository; use a comma, a colon, a full stop or parentheses.
- Commit messages carry no co-author trailer.
- Versions are compared through `ReleaseVersion`, never as text. The tag rules are in the README.

## Reference

- [docs/adr/](docs/adr/): decisions and their rationale.
- [docs/findings/jsonl-schema.md](docs/findings/jsonl-schema.md): the verified local data schema.
- [docs/reference/pricing.md](docs/reference/pricing.md): how tokens are priced. Point at it rather than restating a rate.
- [docs/cli.md](docs/cli.md): every command-line flag.
