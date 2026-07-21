# Build plan

Five session-sized phases. Each ends in something **runnable and verifiable** — not just compiling. Do not start a phase before its predecessor's acceptance criteria pass.

Read [CLAUDE.md](../CLAUDE.md) and [docs/adr/README.md](adr/README.md) before phase 1.

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
- `IconRenderer` — 2 digits, no ring, auto-fitted font, colour by threshold ([findings](findings/tray-icon-rendering.md))
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

## Rules for every phase

1. **Windows-only.** No cross-platform abstractions. Watch for macOS patterns leaking in — the nearest prior art is a Mac app.
2. **Clean-room.** Never read CodexBar source, especially when stuck.
3. **No new dependencies** without an ADR. Current set: `Microsoft.Data.Sqlite`, xUnit. That is all.
4. **Never fabricate a number.** Unknown is a valid, honest state.
5. **Commit at the end of each phase** with the acceptance evidence in the message.
