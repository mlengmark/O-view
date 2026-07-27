# Architecture Decision Records

Records of significant architectural decisions for O-view: the context, the choice, the alternatives, and the consequences.

These are **decided, not drafts.** To change one, add a new ADR that supersedes it rather than editing history.

| # | Title | Status | Summary |
|---|---|---|---|
| [0001](0001-tech-stack.md) | Technology stack — .NET 10 + WPF | Accepted *(partly superseded by 0005)* | C# / .NET 10 LTS / WPF. Rejected Rust+Tauri, Python, Electron, WinUI 3. |
| [0002](0002-usage-data-providers.md) | Dual usage-data providers with graceful fallback | Accepted *(precedence superseded by 0007)* | Dual providers with fallback and mandatory data-source labelling still stand. The original OAuth-primary choice does not: `PlanHistoryProvider` is primary and `OAuthUsageProvider` was never built. |
| [0003](0003-windows-tray-constraints.md) | Windows-only target and notification-area design constraints | Accepted *(icon design revised)* | Windows 11 only. The tray icon cannot show text — information tiers across icon, tooltip, popup. |
| [0004](0004-clean-room-provenance.md) | Clean-room provenance policy | Accepted | No third-party usage-monitor source is read or copied. Implementation derives from platform docs and local observation. |
| [0005](0005-native-tray-integration.md) | Native tray integration — drop H.NotifyIcon | Accepted | Use first-party `System.Windows.Forms.NotifyIcon`. **Zero third-party runtime dependencies.** |
| [0006](0006-local-rollup-store.md) | Local rollup store for usage history | Accepted | SQLite daily aggregates. Claude Code deletes transcripts at 30 days, so 31-day figures need our own store. |
| [0007](0007-plan-history-primary-provider.md) | `PlanHistoryProvider` becomes the primary usage source | Accepted | Claude Desktop caches session/weekly % locally. **No token, no network, no rate limits in v1.** |
| [0008](0008-installer-distribution.md) | Per-user installer for distribution and relaunch | Accepted | Inno Setup per-user installer: Start Menu entry, optional autostart, clean uninstall. Resolves #7; MSIX rejected (needs signing). |
| [0009](0009-auto-update.md) | In-app auto-update via GitHub release + existing installer | Accepted *(relaunch amended by 0010)* | Check `releases/latest`, download `O-view-Setup.exe`, silent in-place upgrade + relaunch. Resolves #18; no new dependency; Squirrel/Velopack/MSIX rejected. |
| [0010](0010-post-update-relaunch.md) | Relaunch through Explorer after a silent update | Accepted | An installer-parented instance could not read `plan-usage-history.json` and never recovered; `explorer.exe` re-parents to the shell. Amends 0009. Mechanism unproven — trigger and cure reproduced. |

## Findings

Empirical results from spikes, cited by the ADRs above:

- [jsonl-schema.md](../findings/jsonl-schema.md) — local transcript format; the `requestId` de-duplication requirement
- [tray-icon-rendering.md](../findings/tray-icon-rendering.md) — icon legibility measurements; how the icon became the ring-gauge brand mark (ring + pupil)
- [plan-usage-history.md](../findings/plan-usage-history.md) — Claude Desktop's cached utilisation series; how reset times are derived
- [credit-usage-divergence.md](../findings/credit-usage-divergence.md) — **credit-billed usage bypasses the plan window**; the headline % can be true and misleading at once

## Open questions

Tracked here until resolved by a spike, then folded into an ADR:

| Question | Blocks | Contingency |
|---|---|---|
| Is `plan-usage-history.json` capped at ~139 samples, or was that just Desktop's uptime? | Nothing — rollup store persists samples either way | Observe over a longer run |
| What is the exact response shape of `/api/oauth/usage`? | `OAuthUsageProvider` — **deferred out of v1** | Defensive nullable parsing regardless |
| Is there any source for a **credit balance**? No local file carries one; the API is untested. *(Partially answered — see Resolved: credit **spend** is estimable locally and divergence is detectable; only the exact **balance** still needs the API.)* | Credits section of the popup | Section shows an explanatory note, not invented figures ([ui-spec](../ui-spec.md)) |
| Does the calibration hold on other accounts, plans, and models? Thresholds derive from one account and one model. | Detector accuracy elsewhere | Floor set ~10× worst observed case; tune the threshold, not the logic |
| What does "Limit Reset Credits" refer to? Could not be mapped to a documented concept. | Credits section | Needs clarification from @mlengmark |
| Does icon legibility hold on a real taskbar at 125/150/175% scaling and in high-contrast themes? | Final icon polish | Ring/pupil geometry scales with the icon size; verify on-device |
| **Which inherited token/environment property makes `File.Exists` fail** for an installer-parented instance? Correlation and cure are reproduced; the mechanism is not. | Confidence in [ADR-0010](0010-post-update-relaunch.md) | Explorer-parented launch avoids the trigger. If the symptom recurs on an Explorer-launched instance, supersede rather than patch |
| On a machine where `plan-usage-history.json` **does not exist at all**, does Claude Desktop write it elsewhere, or not at all for that account/version? | Whether O-view can support that user | Needs a profile-wide search + Desktop version from an affected machine; the banner and `--diagnose` now capture the evidence |

### Resolved

| Question | Outcome |
|---|---|
| ~~Are 2 digits legible at 16×16 px?~~ | Moot — digits were dropped for a ring-gauge brand mark (ring + pupil), with the number in the tooltip, so the digit-legibility question no longer governs the design. → [tray-icon-rendering.md](../findings/tray-icon-rendering.md), revises [ADR-0003](0003-windows-tray-constraints.md) |
| ~~Is a third-party tray library required?~~ | **No** — `System.Windows.Forms.NotifyIcon` is first-party and sufficient. → [ADR-0005](0005-native-tray-integration.md) |
| ~~Where does Claude Code Desktop store its OAuth token on Windows?~~ | **Moot for v1.** Claude Desktop caches session/weekly % to `%APPDATA%\Claude\plan-usage-history.json`, so no token is needed. → [ADR-0007](0007-plan-history-primary-provider.md), [findings](../findings/plan-usage-history.md) |
| ~~Can reset times be obtained without the OAuth endpoint?~~ | **Yes** — `fh` drops mark resets, measured exactly 5.00014 h apart; anchor and extrapolate. |
| ~~Does the plan window capture all usage?~~ | **No — and this is the project's most consequential finding.** Credit-billed usage bypasses the 5-hour window entirely: the icon read a green 6% while ~€86 was spent off-plan (billing-confirmed). Divergence is detectable from data already on disk. → [credit-usage-divergence.md](../findings/credit-usage-divergence.md) |
| ~~How to tune divergence detection against the integer-percent rounding floor?~~ | **Calibrated from 20 observed rise events:** median 2,523 output tokens per point, worst case 5,793. Floor set at 50,000 tokens with ≤1 point rise — ~10× worst case, biased toward silence. → [findings](../findings/credit-usage-divergence.md#calibration-spike-2026-07-21) |

## Format

Each record carries: Status · Date · Deciders · Context · Decision · Alternatives considered · Consequences (positive **and** negative).
