# ADR-0011: Weekly reset derivation — a measured 7-day window and its own durable log

- **Status:** Accepted
- **Date:** 2026-07-28
- **Deciders:** @mlengmark
- **Amends:** the weekly-reset rules in [ADR-0007](0007-plan-history-primary-provider.md) and the weekly row in [ui-spec.md](../ui-spec.md). The 5-hour derivation is untouched.
- **Resolves:** the remaining half of [issue #6](https://github.com/mlengmark/O-view/issues/6)

## Context

The panel's **Weekly** row has shown a percentage but never a reset time. It was built to
refuse one until it could satisfy two conditions, and on real data it never satisfied
either:

1. **The period had to be measured from two observed resets**, because the weekly window's
   length was recorded as disputed (7-day vs 72-hour) and CLAUDE.md rule 6 forbids guessing.
2. **Drops seen across a gap in sampling were rejected** as suspected "restart snaps" —
   only drops with neighbouring samples ≤ 15 minutes apart counted.

Eight days of `%APPDATA%\Claude\plan-usage-history.json`, read 2026-07-28, show why that
combination detects nothing:

| Observed | `sd` | Gap to previous sample |
|---|---|---|
| 2026-07-21 06:14:55Z | 9 → 0 | 10 h 18 m |
| 2026-07-28 06:28:57Z | 70 → 0 | 10 h 07 m |

Both weekly resets land at ~06:20 UTC. Claude Desktop is closed overnight, so **both are
gap-crossing and both were discarded** — leaving zero observations, hence never two, hence
never a period, hence never a reset time. The feature could not complete on this machine,
and the same will hold for any user who does not leave Desktop running through the night.

The same file also settles the period, and corrects two things previously recorded as
unknown:

- **The window is 7 days.** The two resets are **7 d 0 h 14 m** apart, and those 14 minutes
  sit entirely inside the sampling gaps that bracket them — the same argument that made
  5.00014 h "exactly 5 hours" for the session window.
- **The 72-hour alternative is disproved**, not merely unlikely. `sd` climbs 2 → 70
  monotonically across those seven days, and sampling was continuous through
  2026-07-24 06:00–12:00Z, where a 72-hour window would have reset. It did not.
- **Retention is far longer than recorded.** The finding said ~139 samples / 11.6 h; the
  file now holds **1,137 samples spanning 190 h (7.9 days)**. It was Desktop's uptime, not a
  cap.

Separately, the observed resets were being persisted in the **rollup store**. That store is
a derived cache and is designed to detect corruption and rebuild itself from empty
(issue #16) — it did so **four times in six days** on the dev machine. Weekly resets cannot
be rebuilt from anything: the source file retains days, the window is a week, and a
discarded observation costs a full week before another can be seen.

## Decision

### 1. The period is 7 days — measured, and checked rather than trusted

`WeeklyResetDetector.WindowLength = 7 days`, mirroring `ResetDetector`'s 5 hours. **One**
observed reset is therefore enough to predict the next, exactly as one `fh` drop is for the
session window.

The constant was measured on one machine and one plan, so it is not assumed to be universal:
when **two precise observations** disagree with a whole number of 7-day windows (beyond
their own uncertainty plus sampling jitter), the measured interval wins. Imprecise
observations never measure the period — a ten-hour bracket cannot distinguish 7 d from
7 d 10 h, and letting it try replaces a correct constant with noise.

### 2. A drop across a sampling gap is a real reset, recorded as an interval

The "restart snap" it was rejected for is not a mechanism the data supports: while Desktop
is closed it writes nothing, and the first sample after it reopens is a *fresh* fetch — so a
**lower** value there means quota was genuinely restored. What the gap costs is precision,
not trust.

So an observation is stored as the bracket `(previous sample, drop sample]` that provably
contains the reset, rather than as a single instant:

- Predictions step from the bracket's **upper** bound. The reset happens at or before the
  time shown, which is the safe direction for a quota display — it never promises fresh
  quota earlier than it arrives.
- The **most precise** observation anchors, not the most recent: the period is exact, so
  projecting a tight bracket forward by whole weeks beats inheriting yesterday's ten-hour
  slop.
- Re-observing a reset **merges** into the existing record, keeping the intersection of the
  two brackets. Both contain the reset, so their overlap does, and the answer can only
  sharpen.
- Observations carry their **org uuid** and are filtered by it. Windows are per-organization;
  an account that switches org must not have two unrelated sets of resets averaged together.

### 3. Observations live in their own file, not in the rollup store

`%LOCALAPPDATA%\O-view\weekly-resets.json`, written atomically (temp file + replace) and
parsed defensively. Rows the old store still holds are imported once on launch, as precise
observations — only the in-cadence detector could have written them.

Unrebuildable state does not belong inside something designed to wipe itself.

### 4. The UI names the state it is in

| State | Weekly row |
|---|---|
| Reset derived, precise observation | `Resets in 6d 3h · Tue 06:28` |
| Reset derived, gap-bracketed observation | `Resets in 6d 3h · ~Tue 06:28`, hover gives the bracket width |
| Plan data flowing, no reset seen yet | `Waiting for first reset…`, hover explains the wait |
| No plan data at all | hidden — the no-data banner above already explains it |

The blank was the worst of these: it is indistinguishable from a bug, and it is what
prompted issue #6 in the first place.

### 5. Discovery is the poll loop

`PlanHistoryProvider.GetSnapshot` re-scans the whole retained series on **every** poll and
folds what it finds into the log; recording an already-known reset is a no-op. There is no
separate schedule to get wrong, the reset is picked up on the first poll after it appears in
the file, and — because retention (≈8 days) exceeds the period (7 days) — any machine that
runs O-view at least once a week catches every reset.

A failure anywhere in this path degrades the weekly reset to unknown and **never** touches
the percentages, which do not depend on it.

## Consequences

**Positive**
- The weekly reset appears after **one** observed reset, not two — days instead of a
  fortnight, and on the dev machine it resolves from data already on disk.
- Users who close Claude Desktop overnight — probably most of them — are supported at all,
  which they previously were not.
- Precision is stated rather than implied: `~` and the hover bracket keep rule 6 intact while
  still showing a useful time.
- The one piece of unrebuildable state is out of the component that deletes itself on
  corruption.
- Prediction improves on its own as tighter observations accrue, with no re-anchoring logic
  in the caller.

**Negative**
- A reset caught overnight is only known to within ~10 hours. Honest, and marked, but the
  countdown is that coarse until a precise observation lands.
- 7 days is measured on **one** account and one plan. The two-precise-observation override
  is the guard, but it needs two precise observations to fire — a user whose plan really has
  a different period and who never catches a reset in-cadence will see a wrong time (marked
  approximate) rather than none.
- One more file in `%LOCALAPPDATA%\O-view`, and one more format to keep parsing defensively.
- A genuine downward correction from Anthropic — a re-computed `sd`, not a reset — would
  now be recorded as a reset where it was previously ignored. No such event appears in eight
  days of data; if one is ever seen, the residual value after the drop is the signal to add.

## Alternatives considered

| Option | Why rejected |
|---|---|
| **Keep requiring two resets before predicting** | Costs a fortnight after install for no benefit now the period is measured — and, with the gap rule, never completed at all. |
| **Keep rejecting gap-crossing drops, just fix the period** | Still detects nothing on the dev machine: both real resets are gap-crossing. The rule discards precisely the observations the feature needs. |
| **Assume 7 days with no measurement** | What rule 6 forbids. The constant is used *because* it was measured and the alternative disproved, and it is still overridable by measurement on the user's own machine. |
| **Take the bracket's midpoint as the reset time** | Invents a precision that does not exist, and can claim a reset has happened before it has. The upper bound is a fact about the data. |
| **Show the reset only when a precise observation exists** | Back to a blank row for anyone who closes Desktop at night — the failure this ADR exists to fix. |
| **Keep the log in the rollup store, and stop that store self-healing** | Trades a wiped weekly log for a permanently broken usage panel (issue #16). The store's self-heal is correct *for a cache*; the fix is to move what is not a cache. |
| **Read the reset from `/api/oauth/usage`, which almost certainly reports it directly** | Needs the encrypted OAuth token and reintroduces every liability [ADR-0007](0007-plan-history-primary-provider.md) removed, to obtain something derivable from a local file. Still the right answer if OAuth is ever built. |
