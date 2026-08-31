# Architecture Decision Records

Records of significant architectural decisions for O-view: the context, the choice, the alternatives, and the consequences.

These are **decided, not drafts.** To change one, add a new ADR that supersedes it rather than editing history.

| # | Title | Status | Summary |
|---|---|---|---|
| [0001](0001-tech-stack.md) | Technology stack — .NET 10 + WPF | Accepted *(partly superseded by 0005)* | C# / .NET 10 LTS / WPF. Rejected Rust+Tauri, Python, Electron, WinUI 3. |
| [0002](0002-usage-data-providers.md) | Dual usage-data providers with graceful fallback | Accepted *(precedence superseded by 0007)* | Dual providers with fallback and mandatory data-source labelling still stand. The original OAuth-primary choice does not: `PlanHistoryProvider` is primary and `OAuthUsageProvider` was never built. |
| [0003](0003-windows-tray-constraints.md) | Windows-only target and notification-area design constraints | Accepted *(icon design revised; target scope superseded by 0012)* | The tray icon cannot show text — information tiers across icon, tooltip, popup. Still governs the Windows head; "Windows only" no longer holds. |
| [0004](0004-clean-room-provenance.md) | Clean-room provenance policy | Accepted | No third-party usage-monitor source is read or copied. Implementation derives from platform docs and local observation. |
| [0005](0005-native-tray-integration.md) | Native tray integration — drop H.NotifyIcon | Accepted | Use first-party `System.Windows.Forms.NotifyIcon`. **Zero third-party runtime dependencies.** |
| [0006](0006-local-rollup-store.md) | Local rollup store for usage history | Accepted *(30-day retention premise contradicted by measurement, 2026-08-28)* | SQLite daily aggregates. Claude Code deletes transcripts at 30 days, so 31-day figures need our own store. |
| [0007](0007-plan-history-primary-provider.md) | `PlanHistoryProvider` becomes the primary usage source | Accepted *(sampling interval amended 2026-08-28: variable, measured at 15 min)* | Claude Desktop caches session/weekly % locally. **No token, no network, no rate limits in v1.** |
| [0008](0008-installer-distribution.md) | Per-user installer for distribution and relaunch | Accepted | Inno Setup per-user installer: Start Menu entry, optional autostart, clean uninstall. Resolves #7; MSIX rejected (needs signing). |
| [0009](0009-auto-update.md) | In-app auto-update via GitHub release + existing installer | Accepted *(relaunch amended by 0010; Linux + release model amended; outbound-call precedent extended by 0016)* | Check `releases/latest`, download `O-view-Setup.exe`, silent in-place upgrade + relaunch. Resolves #18; no new dependency; Squirrel/Velopack/MSIX rejected. |
| [0010](0010-post-update-relaunch.md) | Relaunch through Explorer after a silent update | Accepted | An installer-parented instance could not read `plan-usage-history.json` and never recovered; `explorer.exe` re-parents to the shell. Amends 0009. Mechanism unproven — trigger and cure reproduced. |
| [0011](0011-weekly-reset-derivation.md) | Weekly reset — a measured 7-day window and its own durable log | **Superseded in full by [0014](0014-weekly-reset-is-a-reported-constant.md)** | The **7-day window length** stands, measured; 72 h disproved. The **derivation does not**: the reset is a reported constant, and the detector, observation log and `weekly-resets.json` are deleted. Do not build on this ADR. |
| [0012](0012-linux-support.md) | Linux joins Windows as a supported target | Accepted | Claude Desktop for Linux shipped, so the files O-view reads exist there. Core needed **no code change** (278/278 tests pass on `net10.0`); the cost is the UI head. Supersedes 0003's target scope only. macOS still out. |
| [0013](0013-linux-ui-toolkit.md) | Avalonia for a Linux head, alongside WPF | Accepted | Windows head untouched. **A tray icon reports success even with no SNI host**, so O-view must probe the bus itself and say what it observed. Zero-third-party (0005) is scoped to Windows: Linux costs 25 assemblies. |
| [0014](0014-weekly-reset-is-a-reported-constant.md) | The weekly reset is a reported constant, not something to derive | Accepted *(supersedes 0011 in full)* | `cachedUsageUtilization.seven_day.resets_at` is exact and the weekly reset is a **static grid** — five observations over five weeks, all matching. **Persist it as an anchor and project forward, including from the past**; the derivation was 11.5 h late and on the wrong day. The five-hour window rolls from first use, is not a grid, and stays derived. |
| [0015](0015-no-credential-based-usage-sources.md) | No credential-based usage sources | Accepted | O-view never handles a Claude subscription credential. Permitted: read files the vendor's client writes, and **invoke the vendor's own client** (`claude -p "/usage"`, measured at zero cost). `OAuthUsageProvider` is deleted, not deferred. Restates rule 3's basis — the token is now documented, so the prohibition rests on policy. |
| [0016](0016-published-reference-data-is-fetchable.md) | Published reference data is fetchable on the release feed's terms | Accepted *(amends 0009)* | A weekly unauthenticated GET of Anthropic's public pricing page is the same category of call as the release check — no credential, no user data. **It returns a difference list and never installs a rate**, and a failure is reported as "did not check" rather than as agreement. Rule 3 is untouched. The rate table was wrong twice in ways that defeat each other's checks: one a collapsed multiplier, the other a row that was wrong the day it was written. |

## Reference

Structure written down once, so the code can point at it rather than restating parts of it:

- [reference/pricing.md](../reference/pricing.md) — how tokens are priced: the five published columns per model, the `speed` and `inference_geo` modifiers, fail-to-unknown, and the three ways the table is kept right

## Findings

Empirical results from spikes, cited by the ADRs above:

- [jsonl-schema.md](../findings/jsonl-schema.md) — local transcript format; the `requestId` de-duplication requirement; the three pricing modifiers on `usage`
- [tray-icon-rendering.md](../findings/tray-icon-rendering.md) — icon legibility measurements; how the icon became the ring-gauge brand mark (ring + pupil)
- [plan-usage-history.md](../findings/plan-usage-history.md) — Claude Desktop's cached utilisation series; how reset times are derived
- [credit-usage-divergence.md](../findings/credit-usage-divergence.md) — **credit-billed usage bypasses the plan window**; the headline % can be true and misleading at once
- [linux-tray-spike.md](../findings/linux-tray-spike.md) — an Avalonia tray icon reports `IsVisible = true` **with no host to display it**; a session-bus probe is what actually tells the difference
- [api-usage-availability.md](../findings/api-usage-availability.md) — **there is no public API for consumer plan usage**, and the credential that would reach it is prohibited from third-party use; the Admin API is unavailable to individual accounts and reports a different pool
- [cli-usage-refresh.md](../findings/cli-usage-refresh.md) — Claude Code refreshes its usage cache **only on `/usage`**, not on startup; invoking it costs **zero tokens**, and an unrecognised prompt through the same entry point costs ~50K. Also: sampling measured at 15 min, transcripts surviving 42 days

## Open questions

Tracked here until resolved by a spike, then folded into an ADR:

| Question | Blocks | Contingency |
|---|---|---|
| Is there any source for a **credit balance**? No local file carries one; the API is untested. *(Partially answered — see Resolved: credit **spend** is estimable locally and divergence is detectable; only the exact **balance** still needs the API.)* | Credits section of the popup | Section shows an explanatory note, not invented figures ([ui-spec](../ui-spec.md)) |
| Does the calibration hold on other accounts, plans, and models? Thresholds derive from one account and one model. | Detector accuracy elsewhere | Floor set ~10× worst observed case; tune the threshold, not the logic |
| What does "Limit Reset Credits" refer to? Could not be mapped to a documented concept. | Credits section | Needs clarification from @mlengmark |
| Does icon legibility hold on a real taskbar at 125/150/175% scaling and in high-contrast themes? | Final icon polish | Ring/pupil geometry scales with the icon size; verify on-device |
| **Which inherited token/environment property makes `File.Exists` fail** for an installer-parented instance? Correlation and cure are reproduced; the mechanism is not. | Confidence in [ADR-0010](0010-post-update-relaunch.md) | Explorer-parented launch avoids the trigger. If the symptom recurs on an Explorer-launched instance, supersede rather than patch |
| On a machine where `plan-usage-history.json` **does not exist at all**, does Claude Desktop write it elsewhere, or not at all for that account/version? | Whether O-view can support that user | Needs a profile-wide search + Desktop version from an affected machine; the banner and `--diagnose` now capture the evidence |
| Do real SNI hosts **draw** the gauge legibly, **recolour** it, survive a **shell restart**, and deliver **clicks**? And can the panel be positioned at all under **Wayland**? | [#77](https://github.com/mlengmark/O-view/issues/77) and [#78](https://github.com/mlengmark/O-view/issues/78) | Needs a real GNOME/Plasma session — a headless runner cannot answer it ([spike](../findings/linux-tray-spike.md)). Colour is already never the sole signal, so recolouring should degrade rather than break |
| Do the Linux paths in [ADR-0012](0012-linux-support.md) match a real install, and does Claude Desktop for Linux write `plan-usage-history.json` at all? | Whether the primary provider works on Linux at all | Documented .NET behaviour says yes; confirm on a real Ubuntu/Debian machine before relying on it ([#70](https://github.com/mlengmark/O-view/issues/70)) |

### Resolved

| Question | Outcome |
|---|---|
| ~~Are 2 digits legible at 16×16 px?~~ | Moot — digits were dropped for a ring-gauge brand mark (ring + pupil), with the number in the tooltip, so the digit-legibility question no longer governs the design. → [tray-icon-rendering.md](../findings/tray-icon-rendering.md), revises [ADR-0003](0003-windows-tray-constraints.md) |
| ~~Is a third-party tray library required?~~ | **No** — `System.Windows.Forms.NotifyIcon` is first-party and sufficient. → [ADR-0005](0005-native-tray-integration.md) |
| ~~Where does Claude Code Desktop store its OAuth token on Windows?~~ | **Answered, and no longer load-bearing.** Anthropic [documents it](https://code.claude.com/docs/en/authentication): `%USERPROFILE%\.claude\.credentials.json`. Difficulty was the original reason for deferral; the prohibition now rests on policy. → [ADR-0015](0015-no-credential-based-usage-sources.md) |
| ~~What is the exact response shape of `/api/oauth/usage`?~~ | **Moot — prohibited.** Reaching it requires replaying a subscription credential, which the Consumer Terms forbid in a third-party product. Answering the question would not make the provider permissible. → [ADR-0015](0015-no-credential-based-usage-sources.md), [findings](../findings/api-usage-availability.md) |
| ~~Can reset times be obtained without the OAuth endpoint?~~ | **Yes** — `fh` drops mark resets, measured exactly 5.00014 h apart; anchor and extrapolate. |
| ~~Is `plan-usage-history.json` capped at ~139 samples, or was that just Desktop's uptime?~~ | **Uptime.** The same file held **1,137 samples over 190 h (7.9 days)** on 2026-07-28. Retention exceeding the 7-day window is what makes weekly-reset discovery reliable. → [ADR-0011](0011-weekly-reset-derivation.md) |
| ~~Is the weekly window 7 days or 72 hours?~~ | **7 days**, measured from two resets 7 d 0 h 14 m apart. 72 h is disproved by the same file: `sd` rose monotonically through a continuously sampled 2026-07-24, where a 72-hour window would have reset. → [ADR-0011](0011-weekly-reset-derivation.md) |
| ~~Does the plan window capture all usage?~~ | **No — and this is the project's most consequential finding.** Credit-billed usage bypasses the 5-hour window entirely: the icon read a green 6% while ~€86 was spent off-plan (billing-confirmed). Divergence is detectable from data already on disk. → [credit-usage-divergence.md](../findings/credit-usage-divergence.md) |
| ~~Which Linux UI toolkit?~~ | **Avalonia, for a Linux head alongside WPF.** The spike verified the load-bearing case — a live-rendered icon, replaced repeatedly on a timer — and registration over D-Bus. → [ADR-0013](0013-linux-ui-toolkit.md) |
| ~~What does O-view do on a GNOME desktop with no AppIndicator extension?~~ | **It must ask the bus, not the toolkit.** Measured: an Avalonia tray icon reports `IsVisible = true` with no host present, and the app's output is identical either way — so trusting it means being silently invisible on stock Ubuntu. A probe for `org.kde.StatusNotifierWatcher` returns False/True correctly. → [findings](../findings/linux-tray-spike.md), [ADR-0013](0013-linux-ui-toolkit.md) |
| ~~How to tune divergence detection against the integer-percent rounding floor?~~ | **Calibrated from 20 observed rise events:** median 2,523 output tokens per point, worst case 5,793. Floor set at 50,000 tokens with ≤1 point rise — ~10× worst case, biased toward silence. → [findings](../findings/credit-usage-divergence.md#calibration-spike-2026-07-21) |

## Format

Each record carries: Status · Date · Deciders · Context · Decision · Alternatives considered · Consequences (positive **and** negative).
