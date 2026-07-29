# ADR-0012: Linux joins Windows as a supported target

- **Status:** Accepted
- **Date:** 2026-07-29
- **Deciders:** @mlengmark
- **Supersedes:** the target scope of [ADR-0003](0003-windows-tray-constraints.md) — and only that

## Context

[ADR-0003](0003-windows-tray-constraints.md) chose Windows exclusively, listed "macOS or Linux builds" under *Explicitly out of scope*, and rejected a cross-platform framework because it "adds abstraction cost for platforms with no requirement".

That reasoning was sound, and its premise has since expired.

### What changed

**Claude Desktop for Linux entered official beta on 2026-06-30** — Ubuntu 22.04+ and Debian 12+, on x86_64 **and** arm64, distributed through Anthropic's own apt repository. It ships the same Chat, Cowork and Claude Code experience as the Windows and macOS builds.

O-view reads two things. Both now exist on Linux:

| Source | Windows | Linux |
|---|---|---|
| Claude Desktop's plan meter | `%APPDATA%\Claude\plan-usage-history.json` | `~/.config/Claude/plan-usage-history.json` |
| Claude Code transcripts | `%USERPROFILE%\.claude\projects\**\*.jsonl` | `~/.claude/projects/**/*.jsonl` |
| Cowork audit logs | `<claude-data-root>\local-agent-mode-sessions\…` | same, under the Linux data root |

So the platform with "no requirement" now has one. The scope boundary is out of date; the reasoning that produced it is not.

### What the port actually costs — measured, not estimated

Three findings, established before this ADR was written, because the decision depends on them.

**1. `O-view.Core` is already platform-neutral.** Its only non-`OView` dependencies are `System.Text.Json`, `System.Globalization`, `System.Text` and `Microsoft.Data.Sqlite`. There is no `Microsoft.Win32`, no `System.Drawing`, no `DllImport`. Retargeting `net10.0-windows` → `net10.0` and building gives:

```
Build succeeded.  0 Warning(s)  0 Error(s)      # with TreatWarningsAsErrors still on
Passed! - Failed: 0, Passed: 278, Total: 278
```

The Windows-flavoured TFM was a leftover, not a constraint. `SQLitePCLRaw.bundle_e_sqlite3` ships `linux-x64` and `linux-arm64` natives, so the rollup store travels too.

**2. The existing path composition is already correct on Linux.** On Unix .NET resolves `UserProfile` → `$HOME`, `ApplicationData` → `$XDG_CONFIG_HOME ?? ~/.config`, `LocalApplicationData` → `$XDG_DATA_HOME ?? ~/.local/share`. Applied to today's code that yields `~/.config/Claude/plan-usage-history.json`, `~/.claude/projects` and `~/.local/share/O-view/` — the right answers, by accident rather than design, but right. Confirm against a real install before relying on it (rule 9 exists because "documented" and "on the user's disk" have diverged here before).

**3. The cost is concentrated in the UI.** WPF cannot cross. The Linux notification area is StatusNotifierItem over D-Bus, and **GNOME — which Ubuntu ships — has no tray support without a third-party extension.** That is the genuine unknown, and it is a product problem as much as a technical one.

## Decision

**Support Windows 11 and Linux.** Linux means Ubuntu 22.04+ / Debian 12+ on x64 and arm64 — deliberately the same matrix Claude Desktop for Linux supports, because a user who cannot run Claude Desktop has nothing for O-view to read.

**macOS remains out of scope.** Nothing here reopens it.

### What this supersedes in ADR-0003, and what it does not

**Superseded:** the Windows-exclusive target, the "macOS or Linux builds" out-of-scope bullet, and the framing of cross-platform work as drift.

**Still in force, unchanged:** everything else, and specifically ADR-0003's central analysis. `Shell_NotifyIcon` accepts an `HICON` only; roughly two glyphs are legible at 16 px; information tiers across icon → tooltip → panel; colour is never the sole signal. That analysis was correct when written and is still correct — it simply describes Windows, which remains a target. Its platform-behaviour list (items 1–10) likewise still governs the Windows head.

The Avalonia rejection in ADR-0003's *Alternatives considered* is **not reversed here.** Half of it has expired (there is now a requirement); half has not (tray behaviour on Linux really is where a cross-platform abstraction is weakest). It is revisited by a time-boxed spike whose evidence feeds ADR-0013 — decided by measurement, not by assertion in this document.

### The layering rule that replaces "no abstractions"

ADR-0003 forbade abstraction because there was one platform. With two, the rule becomes *where* code lives, not *whether* it abstracts:

| Layer | Target | May use |
|---|---|---|
| `O-view.Core` | `net10.0` | BCL and SQLite only. No UI, no Win32, no `System.Drawing`, no `Microsoft.Win32`. **Must build and pass its tests on Linux.** |
| `O-view.App` *(planned)* | `net10.0` | The same. Orchestration only; platform behaviour arrives through injected interfaces. |
| Windows head | `net10.0-windows` | Win32, WPF, WinForms `NotifyIcon` — **directly, with no abstraction tax.** |
| Linux head *(planned)* | `net10.0` | D-Bus, StatusNotifierItem, the toolkit chosen by ADR-0013. |

Platform differences are resolved by **which implementation gets constructed**, not by `#if` or `OperatingSystem.IsLinux()` scattered through logic. Where a runtime platform check genuinely is the simplest correct answer — data-root discovery is the real instance — it lives in exactly one named place, and that place is named in an ADR.

CI must build and test on both `windows-latest` and `ubuntu-latest`. A portability rule that nothing enforces is a rule that lasts one pull request.

## Alternatives considered

| Option | Why rejected |
|---|---|
| **Stay Windows-only** | Defensible until 2026-06-30; no longer. Claude Desktop, Claude Code and Cowork all run on Linux, so O-view's reason to exist applies there. Declining would be a choice to serve fewer users for no engineering reason — Core needed no changes at all. |
| **A separate `o-view-linux` repository** | The shared layer *is* the product: every provider, the ingestion rules, the reset derivation, 278 tests. Splitting means duplicating it or versioning it as a package across two repos, and every ordinary change becomes a coordinated two-repo release. v0.5.11 was a consolidation release cleaning up duplication that had already produced user-visible wrong numbers; a second repository is the largest available version of that mistake. |
| **Rewrite the Windows head in a cross-platform toolkit too** | One UI codebase, at the cost of rewriting a shipped, tuned product — including the frame-by-frame-measured flyout curves — to serve a platform that does not exist yet. Risk lands on the users who already have something working. Left open for ADR-0013 to consider on its own evidence. |
| **macOS as well** | No requirement, a third tray model to learn, and a case-insensitive filesystem needing its own answer. Out of scope, as before. |
| **Ship Linux without a tray** (a CLI or a notification-only build) | A different product. O-view is an at-a-glance monitor; the glance is the feature. |

## Consequences

**Positive**

- The accounting logic gains a second platform's worth of users for a two-line target-framework change.
- Forcing Core to build on Linux gives its platform-neutrality an enforcement mechanism it never had — the rule was written in CLAUDE.md from the start and was, in fact, already being followed.
- Extracting the orchestration so two heads can share it makes the app's behaviour unit-testable for the first time; `App.xaml.cs` has no test coverage today.

**Negative**

- **The Windows head is a shipped product on in-place auto-update.** A regression does not wait to be downloaded — it is pushed. Every change touching Windows code paths carries more risk than its diff suggests, and the release pipeline rebuild is the sharpest instance.
- The panel UI will exist twice unless ADR-0013 concludes otherwise. Duplication is this codebase's recurring failure mode, and this is a large deliberate helping of it.
- **On stock Ubuntu GNOME there is no system tray.** O-view can be installed, running and correct, and still invisible. ADR-0013 must decide what the app says in that situation; silently absent is not an acceptable answer (rule 6).
- Linux positioning cannot reproduce the docked flyout: SNI cannot report where its icon was drawn, and Wayland clients generally cannot position their own surfaces. The two platforms will not look identical, and pretending otherwise would produce a panel that lands in the wrong place every time.
- Auto-update does not port. Self-replacing binaries are wrong on a package-managed system, so Linux updates go through apt — see the amendment to [ADR-0009](0009-auto-update.md).
- **[ADR-0005](0005-native-tray-integration.md)'s "zero third-party runtime dependencies" will probably not survive the Linux head.** That guarantee was affordable because Windows has a first-party tray (`System.Windows.Forms.NotifyIcon`) and a first-party rasteriser. Linux has neither: a UI toolkit, a D-Bus binding and a cross-platform rasteriser are all third-party. ADR-0013 has to say plainly what is being given up, and whether the Windows head keeps the guarantee even if the Linux head cannot. It is a real cost, not a technicality — it is why H.NotifyIcon was dropped.

## Follow-on decisions

| Decision | Where |
|---|---|
| Which Linux UI toolkit, and what happens with no SNI host | ADR-0013, from a spike |
| Whether [ADR-0005](0005-native-tray-integration.md)'s zero-third-party-dependency guarantee still holds, and on which platform | ADR-0013 |
| How Linux builds update themselves | Amendment to [ADR-0009](0009-auto-update.md) |
| Unified vs platform-partial releases | Recorded with the release workflow |
