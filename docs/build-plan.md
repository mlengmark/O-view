# Build plan

Session-sized phases. Each ends in something **runnable and verifiable** — not just compiling. Do not start a phase before its predecessor's acceptance criteria pass.

Read [CLAUDE.md](../CLAUDE.md) and [docs/adr/README.md](adr/README.md) before phase 1.

> **Phases 1–6 describe v1: a Windows application, and all are complete.** They are kept as
> written, including the Windows-only assumptions they were built under, because they record
> what was decided at the time. **[Phase 7](#phase-7--linux-v060-milestone-added-2026-07-29)
> adds Linux** and supersedes those assumptions — read it before treating anything above as
> current, and see the amended rules at the foot of this document.

---

## Phase 1 — Scaffold + `PlanHistoryProvider`

The primary data source. No UI.

**Build**
- `O-view.sln` with `src/O-view.Core`, `src/O-view.Tray`, `tests/O-view.Core.Tests` (xUnit), all `net10.0-windows`
- `IUsageProvider`, `UsageSnapshot`, `DataSource` enum (`Live` / `Stale` / `Estimate` / `None`)
- `PlanHistoryProvider` — parse `%APPDATA%\Claude\plan-usage-history.json` ([schema](findings/plan-usage-history.md))
- `ResetDetector` — decrease in `fh` ≥2 points anchors `next = drop + 5h`; re-anchor on each new drop
- Temporary console harness to print a snapshot

**Acceptance**
- `dotnet test` green
- Harness prints **real values from the real file** — session %, weekly %, next reset, staleness
- Reset time reports **unknown** when no drop has been observed; never a guess
- Missing or malformed file returns `DataSource.None`, does not throw

---

## Phase 2 — `JsonlUsageProvider` + rollup store

Token counts and history.

**Build**
- `JsonlUsageProvider` — scan all `~/.claude/projects/**/*.jsonl` ([schema](findings/jsonl-schema.md))
- `RollupStore` — SQLite at `%LOCALAPPDATA%\O-view\usage.db`, one row per (UTC date × model) ([ADR-0006](adr/0006-local-rollup-store.md))
- `CompositeUsageProvider` — resolution order and source labelling ([ADR-0007](adr/0007-plan-history-primary-provider.md))
- Cost estimator against a public API price table

**Acceptance — the two silent-failure tests are mandatory**
- **De-duplication:** fixture with duplicate `requestId`s sums each request once. Without this, totals overcount ~2.3×.
- **Idempotent ingest:** running ingestion twice over the same transcripts leaves totals unchanged.
- Truncated final line and malformed lines are skipped, not fatal
- Files open with `FileShare.ReadWrite` — Claude Code writes while we read
- Windows path mangling resolves (`C:\Users\X` → `C--Users-X`)

Both failures are silent and produce confident, wrong numbers. Test them explicitly.

---

## Phase 3 — Tray shell

First visible output.

**Build**
- `ITrayHost` over first-party `System.Windows.Forms.NotifyIcon` ([ADR-0005](adr/0005-native-tray-integration.md)) — no third-party tray package
- `IconRenderer` — brand mark: colour-coded ring gauge + centre pupil, no digits, colour by threshold ([findings](findings/tray-icon-rendering.md))
- Single-instance mutex; `TaskbarCreated` re-registration; tooltip ≤127 chars
- Polling scheduler, default 60 s (local file reads are cheap)

**Acceptance**
- Icon appears in the tray showing your **real** session %
- `DestroyIcon` called on every refresh — verify handle count is flat in Task Manager over ~100 refreshes
- Icon survives restarting `explorer.exe`
- Second launch exits without a duplicate icon
- Legible at 100% and 150% display scaling

---

## Phase 4 — Popup panel

**Build** to [ui-spec.md](ui-spec.md): header with account info and source label, two quota bars with reset text, 2×2 stat tiles, usage graph, credits placeholder.

**Acceptance**
- Positions correctly with the taskbar on each screen edge, and on a secondary monitor
- Dismisses on click-outside and `Esc`
- Tier renders from `organizationType` — **not** `seatTier`, which is empty and would show blank
- Partial history shows coverage (`3 of 31 days recorded`); pre-install days render as an empty region, never zero bars
- Readable in light and dark themes

---

## Phase 6 — Off-plan detection *(added after v0.1.0)*

Not in the original plan. Added because v0.1.0 shipped a tray that read a comfortable green 6% while ~€86 of credit usage went unreported — see [credit-usage-divergence.md](findings/credit-usage-divergence.md).

**Build**
- `DivergenceDetector` (Core, pure) — window-scoped comparison of deduplicated output tokens against plan-meter movement, calibrated against the integer-percent rounding floor
- `RollupStore.GetUsageSince` — window-grain queries over the existing request ledger
- `PlanHistoryProvider.GetCurrentWindow` — meter series since the last reset, so a window never spans one
- Panel: warning banner, tile relabelling, off-plan spend section; edge-triggered notification

**Acceptance**
- Detector correctly flags the real frozen-meter case and stays silent on the real tracking case, both as unit tests using measured values
- No false positive against live data while the meter is tracking normally
- Both panel states verified visually (simulation hook for the off-plan rendering, since it cannot be produced on demand)

---

## Phase 5 — Ship

Run-at-startup (`HKCU\...\Run`), settings, threshold notifications, `/security-review`, single-file publish, release workflow.

**Acceptance:** clean machine runs the published exe with no .NET install; SmartScreen behaviour documented honestly in the README.

---

## Phase 7 — Linux *(v0.6.0 milestone, added 2026-07-29)*

Phases 1–6 above describe **v1, a Windows application**, and are complete. Phase 7 is a
different shape: rather than one session-sized slice, it is the [v0.6.0
milestone](https://github.com/mlengmark/O-view/milestone/1) — issues #67–#84, ordered so each
lands on top of the last. The decisions are [ADR-0012](adr/0012-linux-support.md) (scope) and
[ADR-0013](adr/0013-linux-ui-toolkit.md) (toolkit); the milestone issues carry the per-issue
acceptance criteria and are not restated here.

Its shape, in four movements:

1. **Make the shared layers portable** — retarget `Core` to `net10.0`, add the `ubuntu-latest`
   CI leg, extract `O-view.App` so orchestration is written once and neither head owns logic
   the other needs.
2. **Build the head** — Avalonia + SkiaSharp, SNI over D-Bus, freedesktop notifications, XDG
   autostart, portal theme.
3. **Package and release** — `.deb` and tarball for x64 and arm64, container install tests, one
   unified release carrying every platform's assets.
4. **Document what shipped** — this document, the README's support matrix, CLAUDE.md.

**Acceptance, and the part that is not yet met.** The code-side criteria pass: `Core`, `App`
and the Linux head build and test on `ubuntu-latest`; the `.deb` installs, runs and purges
cleanly in Ubuntu 22.04, Ubuntu 24.04 and Debian 12 containers, plus arm64 under emulation;
the tarball runs on Fedora; a Linux build never offers itself a Windows installer.

**But no part of this has run on a physical Linux desktop.** Containers are headless: they
prove the package is sound, not that the icon appears, the panel is legible, or the theme
follows. Those rows in the README's support matrix are marked *unverified on hardware*
deliberately, and **they stay that way until a real user reports otherwise** — writing them as
ticks would be rule 4 below applied to ourselves. That report is the last acceptance criterion
for the milestone, and it is why the tag is cut before it, not after.

---

## Rules for every phase

1. **Two platforms, and the layer decides.** Windows 11 and Linux; macOS is out of scope. Nothing platform-specific in `Core` or `App` — resolve differences by which implementation is constructed, not by `#if`. Watch for macOS patterns leaking in: the nearest prior art is a Mac app, and it is wrong for *both* targets. (Phases 1–6 ran under an earlier, Windows-only version of this rule; [ADR-0012](adr/0012-linux-support.md) supersedes it.)
2. **Clean-room.** Never read the source of any existing AI usage-monitor app, especially when stuck.
3. **No new dependencies** without an ADR. `Core` and `App`: `Microsoft.Data.Sqlite` and xUnit, and that is all. The Linux head is the one place ADR-0005's zero-dependency guarantee does not reach — Avalonia, SkiaSharp and `Tmds.DBus.Protocol`, per ADR-0013, because no first-party option exists.
4. **Never fabricate a number** — or a support claim. Unknown is a valid, honest state.
5. **Commit at the end of each phase** with the acceptance evidence in the message.
