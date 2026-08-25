# ADR-0014: The weekly reset is a reported constant, not something to derive

- **Status:** Accepted
- **Date:** 2026-08-25
- **Deciders:** @mlengmark
- **Supersedes:** [ADR-0011](0011-weekly-reset-derivation.md) in full. The 5-hour derivation it left untouched stays untouched here too — see *Why the session window is different*.
- **Evidence:** [findings/cached-usage-utilization.md](../findings/cached-usage-utilization.md), and the bracket analysis below.

## Context

ADR-0011 built a derivation because there was nothing to read. At the time the only weekly
signal O-view had was a drop in `sd` inside Claude Desktop's sampled series, so the reset
time had to be *inferred* from where that drop was bracketed. Everything that decision
argued was correct for the world it was taken in.

**That world ended on 2026-08-24**, when `~/.claude.json` → `cachedUsageUtilization` was
found to carry `utilization.seven_day.resets_at`: an exact, offset-qualified instant,
reported by Claude rather than inferred by us. O-view began reading it immediately, but only
as a *refinement* — `CachedUtilizationProvider.FutureReset` returns it **only while it is
still in the future**, and hands back `null` once it has passed, at which point the
derivation fills the gap. The reasoning was that stepping a passed instant forward would
"dress an inference in a reported value's zero uncertainty".

For the five-hour window that reasoning holds. For the weekly window it does not, and the
data says so plainly.

### The weekly reset is a static weekly grid

Measured 2026-08-25 on the development machine. The exact value read from
`cachedUsageUtilization` was **2026-08-24 20:59:59 UTC, a Monday**. Projecting that instant
backwards in whole weeks lands inside *every* bracket the derivation had independently
observed over five weeks:

| Observed bracket (derived) | Width | Grid point from the exact value | |
|---|---:|---|---|
| (07-20 19:56, 07-21 06:14] | 618 min | 07-20 20:59:59 | inside |
| (07-27 20:22, 07-28 06:28] | 607 min | 07-27 20:59:59 | inside |
| (08-03 20:16, 08-10 15:07] | 9,771 min | 08-03 20:59:59 | inside |
| (08-10 15:54, 08-17 06:17] | 9,503 min | 08-10 20:59:59 | inside |
| (08-17 19:10, 08-18 12:56] | 1,066 min | 08-17 20:59:59 | inside |

Five observations, five matches, across five weeks. The reset is account-bound and fixed:
same weekday, same time of day, every week.

### What the derivation was costing

On the same machine, at the same moment:

| Source | Next weekly reset |
|---|---|
| Exact `resets_at`, projected forward | **2026-08-31 20:59:59 UTC (Monday)** |
| What O-view displayed | 2026-09-01 06:28:57 UTC (Tuesday) |

**11.5 hours late, and the wrong day** — while the exact answer sat unread in a local file,
discarded for being in the past.

The derivation was also producing false positives. `WeeklyResetDetector.FindResets` flags any
`sd` decrease of ≥ 2, which on small values catches Claude Desktop's cold-start `fh=0, sd=0`
sample. Two of the five observations above — 2026-08-17 and 2026-08-18 — are 30 hours apart
and cannot both be weekly resets. And `PredictNextReset` anchors on the *most precise*
observation, which on this machine meant a month-old ±10 h bracket, because
`PreciseBracket` is 15 minutes and **no observation ever qualified**.

Finally, the structural problem ADR-0011 could not solve: weekly resets land while the
machine is asleep. Every observation above crosses a sampling gap. A detector whose input
only arrives when the user happens to be online at the boundary will, for most users, never
fire at all.

## Decision

**The weekly reset has exactly two determinations, and derivation is not one of them.**

### 1. A discovered constant, when Claude Code has ever reported one

Take `cachedUsageUtilization.utilization.seven_day.resets_at` whenever it is present,
**persist it as an anchor**, and project it forward by whole weeks — including when the
stored instant is in the past. It carries **zero uncertainty** and renders without the `~`.

Persisting is the substance of this decision, not an implementation detail. The cache goes
stale — measured at 43 hours on the development machine while the file itself was being
rewritten every few minutes — so a value read only while fresh is a value usually absent.
Read once, stored, it is correct forever after.

### 2. The user's entered value, when there is no discovered one

Unchanged from [issue #186](https://github.com/mlengmark/O-view/issues/186). Anthropic shows
the reset time in the account's usage view, so a user can always read it off directly.

### 3. Otherwise: unknown, and say how to fix it

No guess. The panel states that the weekly reset is not known and points at the entry
dialog. This is CLAUDE.md rule 6 applied to the case ADR-0011 was trying to avoid — and the
honest answer is now *actionable*, which it was not before.

**A discovered constant outranks an entered one**, because it comes from the source rather
than from transcription. But it must never silently replace one: when the two disagree, the
user is told, on the existing `ManualWeeklyResetConflict` path. A wrong entry that is quietly
overridden leaves the user believing the number they typed.

## Why the session window is different, and stays derived

The five-hour window **rolls from first use**; it is not a grid ([issue
#180](https://github.com/mlengmark/O-view/issues/180)). Projecting a passed `five_hour.resets_at`
forward would describe a window that never existed — which is the bug #180 removed. Nothing
here changes it: the session reset keeps its bracket, its narrowing by local activity
([#185](https://github.com/mlengmark/O-view/issues/185)), and its `~`.

The asymmetry is the whole point. One window is a grid and one is not, and applying a single
rule to both is what caused this.

## Consequences

- `WeeklyResetDetector`, `IWeeklyResetLog`, `WeeklyResetLog` and `WeeklyResetObservation` are
  **deleted**, along with the legacy import path in `RollupStore`. The 7-day window length
  and the graph's week gridlines survive in a small `WeeklyWindow` helper, now stepped from
  the anchor.
- `weekly-resets.json` — a log of observations — is replaced by an anchor file. ADR-0011 put
  that file outside the rebuildable rollup store because an observation was unrepeatable and
  cost a week to re-acquire. **That justification is gone**: the anchor is re-readable from
  `~/.claude.json` any time Claude Code refreshes it, so losing the file costs nothing but a
  wait.
- Weekly reset uncertainty is now always zero, so the `~` marker becomes unreachable on the
  weekly row. The plumbing is left in place in this change and removed separately, to keep
  the behavioural change reviewable on its own.
- Users who have neither source see *unknown* where they previously saw a derived time. That
  is a deliberate trade: the derived time was wrong by 11.5 hours and wore no uncertainty the
  user could act on, and the replacement tells them exactly what to do about it.
