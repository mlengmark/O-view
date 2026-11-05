# ADR-0018: Claude Code's reported reset places the session window, not the snapshot ranking

- **Status:** Accepted
- **Date:** 2026-11-05
- **Deciders:** @mlengmark
- **Resolves:** [#275](https://github.com/mlengmark/O-view/issues/275), the follow-up to #274.
- **Does not change:** [ADR-0002](0002-usage-data-providers.md) and [ADR-0007](0007-plan-history-primary-provider.md), which rank sources for the *snapshot*. [ADR-0014](0014-weekly-reset-is-a-reported-constant.md) left the five-hour window derived.
- **Evidence:** [findings/cached-usage-utilization.md](../findings/cached-usage-utilization.md); the code is `SessionWindowPlacement`.

## Context

The off-plan check compares output tokens against the change in the session meter *over one
five-hour window*, so everything it concludes rests on where that window began. Plan history
can only bracket the start between two samples, and has nothing while Claude Desktop is
closed. #274 was that silence: Desktop was off for two days, resumed on `fh=0`, and the
window was anchored on a drop two days old while Claude Code was reporting, correctly, a reset
at 19:40Z, a window that began at 14:40Z.

Two sources know where the window is. Which one to believe is **not** the question ADR-0002
answers. That ranking picks one *snapshot*, whole, by meters carried and recency. Here the two
sources are not alternatives for the same figure: one is exact and often absent, the other is
a bracket and usually present.

## Decision

**The window's start is Claude Code's `five_hour.resets_at` minus one window, when that reset
is one a running window could have. Otherwise plan history places it, as before.**

A reset is usable only while it is **ahead of now and no more than five hours ahead**. Both
refusals are deliberate:

- One already past describes a window that has ended. Stepping it forward rebuilds the clock
  grid that issue #180 removed.
- One more than five hours ahead belongs to no window that could be running, since the window
  rolls from first use, and would put the start in the future.

Neither is repaired. Both are ignored, and the plan-history answer stands.

Three rules keep the choice honest:

1. **The series never spans a reset.** Whatever start was chosen, samples are trimmed to those
   after the last drop inside the window. A start taken from Claude Code can land just after a
   boundary and keep one sample from the window before it, which the detector would read as a
   flat meter.
2. **A window located more than one window ago is no location.** Desktop closed on a non-zero
   reading leaves the search pointing at a window that ended days ago. It is still where the
   fallback lands, clamped, but it is not an inference about the window running now.
3. **A reset is read apart from its percentage.** An aged zero is discarded as a reading, but
   its reset is kept, which is exactly #274's morning.

### Disagreement is a signal, with a narrow definition

When plan history *located* a window (a drop, or a rise from zero) and the reported start is
outside its bracket, the sources contradict each other. That usually means Desktop and Claude
Code are signed into different accounts. O-view logs it once when it starts and once when it
ends, and `--diagnose` shows both starts and which source placed the window. Nothing on screen
changes.

What is **not** a disagreement:

- **A fallback.** The last drop, the series start and the five-hour bound are what plan history
  says when it has no answer. A series that opens on `fh=0` after an overnight gap always
  differs from the reported start, and logging that would fire every morning on a one-account
  machine.
- **A first use too small to move the meter.** Claude's percentage is a whole number, so the
  rise from zero is seen late. Zeros between the reported start and the bracket agree; any
  other reading disagrees. Nothing here depends on Desktop's sampling interval.
- **A rollover.** A reported start after the bracket, and at least one window past its earliest
  edge, is the next window, which Claude Code can report before Desktop's next sample. There is
  nothing to compare.

## Why this differs from the snapshot ranking

The snapshot ranking is by tier, never a fixed order between Desktop and Claude Code, because
either can hold the fresher reading, and a missing source leaves the other shown whole. For
the window start there is no such trade: an exact boundary beats a bracket whenever it exists
and is plausible, and when it is not, nothing is lost by using the bracket.

## What does not change

This phase did not change the panel's session reset. It shows Claude Code's reported reset,
exact and without a `~`, while one is ahead of now, and otherwise the time derived from drops
in the sampled series, with a `~` across a sampling gap.

## Unproven

**Nobody has run this on a real machine with Claude Desktop and Claude Code on two accounts.**
The disagreement is exercised with fixture files, and no test on a single-account machine can
show it. The #274 timeline is reconstructed in tests, not replayed against live files. If a
real two-account machine behaves differently, supersede this ADR rather than patching it.

## Consequences

- The off-plan check is right when Desktop was closed as the window began, the case #274
  reported, instead of merely bounded.
- A Desktop-only user, with no Claude Code cache, is unchanged: the placement falls through to
  the plan-history chain.
- A user with no plan history at all still gets a window, starting at the reported start with an
  empty series, so a single reported reading cannot produce an off-plan verdict.
- The choice lives in `SessionWindowPlacement` and nowhere else, so a source added or
  demoted later is one change.
