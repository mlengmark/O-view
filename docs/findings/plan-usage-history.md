# Finding: `plan-usage-history.json` — local utilisation time series

**Observed:** 2026-07-20 · Windows 11 Pro 26200
**Resolves:** the open question *"Where does Claude Code Desktop store its OAuth token on Windows?"* — by making it unnecessary for v1.

## Location

```
%APPDATA%\Claude\plan-usage-history.json
```

Written by the **Claude Desktop** application. Present and populated on the dev machine.

## Schema

```json
{
  "version": 2,
  "samples": [
    { "t": 1784535700086,
      "org": "00000000-0000-0000-0000-000000000000",
      "u": { "fh": 5, "sd": 3 } }
  ]
}
```

| Field | Meaning |
|---|---|
| `t` | Sample time, Unix epoch **milliseconds** |
| `org` | Organization UUID — matches `oauthAccount.organizationUuid` in `~/.claude.json` |
| `u.fh` | **Five-hour window utilisation, integer percent 0–100** |
| `u.sd` | **Seven-day window utilisation, integer percent 0–100** |

`fh` and `sd` correspond to the `five_hour.utilization` and `seven_day.utilization` values the OAuth usage endpoint returns. **Claude Desktop is already polling that endpoint and caching the result for us.**

## Why this matters

This is the authoritative session and weekly percentage — the two headline numbers in [ui-spec.md](../ui-spec.md) — available with:

- **no OAuth token**
- **no network call**
- **no rate limiting** (the documented `429` problem disappears entirely; Desktop absorbs it)
- **no credential handling**, which removes a whole class of security risk from v1

## Measured characteristics

| Property | Observed 2026-07-20 | Re-observed 2026-07-28 |
|---|---|---|
| Sample interval | **300.02 s median** (5 minutes), min 298.4 s | unchanged |
| Samples in file | 139, spanning 11.6 h | **1,137, spanning 190.4 h (7.9 days)** |
| File size | 11,899 bytes | 98,214 bytes |
| Value type | Integer percent — no fractional precision | unchanged |

> **Retention is Desktop's uptime, not a 139-sample cap.** The first reading was taken after
> a short run and was mistaken for the file's limit. It is not: the file now holds nearly
> eight days. That matters because retention **exceeds the 7-day window**, which is what
> makes weekly-reset discovery reliable — any machine running O-view once a week sees every
> reset. (Whether there is a cap further out is still unmeasured; the reset log persists
> observations regardless, so it does not need to be.)

## Reset times are derivable — and exact

The 5-hour window reset appears as a sharp drop in `fh`. Two were observed:

```
13:21:51Z   16% → 1%
18:21:51Z   31% → 0%
gap: 5.00014 hours
```

**Exactly 5 hours apart, to within sampling jitter.** So once a single drop has been observed, the reset cadence is anchored and future resets are predictable:

```
next reset = last observed drop + 5h
18:21:51Z + 5h = 23:21:51Z
```

This delivers the "time until next reset" requirement without the OAuth endpoint.

### Detection rules

- Trigger on a **decrease** in `fh` beyond a small threshold (≥2 points guards against noise). Do not trigger on increases.
- A drop need not reach 0 — the 13:21 reset went to 1% because new usage began immediately in the fresh window.
- **Before any drop has been observed, the reset time is genuinely unknown.** Show it as unknown rather than guessing; per the "never fabricate a number" rule, a wrong countdown is worse than an absent one.
- Anchor drift is possible if the user is idle across a boundary. Re-anchor on every newly observed drop rather than extrapolating indefinitely from the first.

## The weekly window — measured 2026-07-28

The same technique works on `sd`, but the weekly reset needed its own measurement because
the window length was recorded as disputed and because the resets are rare. Full decision:
[ADR-0011](../adr/0011-weekly-reset-derivation.md).

### Both observed resets, and the period

```
2026-07-21 06:14:55Z   sd  9% → 0%    (previous sample 10 h 18 m earlier)
2026-07-28 06:28:57Z   sd 70% → 0%    (previous sample 10 h 07 m earlier)
apart: 7 d 0 h 14 m
```

**The window is 7 days.** The 14 minutes sit entirely inside the sampling gaps that bracket
the two drops — the same argument that made the session window's 5.00014 h "exactly 5
hours". So one observed drop is enough to predict the next reset, as it is for `fh`.

### The 72-hour alternative is disproved, not merely unlikely

`sd` climbed **2 → 70 monotonically** across those seven days, with only the two drops above.
Sampling ran continuously through 2026-07-24 06:00–12:00Z — where a 72-hour window anchored
on the 07-21 reset would have reset — and `sd` went 35 → 40 through it without a dip. A
window that does not reset when the hypothesis says it must is not that window.

Daily `sd` range, for the whole span:

| Day (UTC) | samples | `sd` min → max |
|---|---|---|
| 07-20 | 140 | 2 → 9 |
| 07-21 | 169 | 0 → 19 *(reset)* |
| 07-22 | 133 | 19 → 29 |
| 07-23 | 152 | 29 → 35 |
| 07-24 | 136 | 35 → 43 *(72 h hypothesis predicts a reset here — none)* |
| 07-25 | 135 | 43 → 52 |
| 07-26 | 120 | 52 → 65 |
| 07-27 | 148 | 65 → 70 |
| 07-28 | 6 | 0 → 1 *(reset)* |

### Detection rules — where they differ from `fh`

- **A drop across a gap in sampling is a real reset, and must be counted.** Both resets
  above land at ~06:20 UTC with Claude Desktop closed overnight. An earlier rule rejected
  gap-crossing drops as suspected "restart snaps" and therefore detected *nothing at all*.
  The mechanism it guarded against is not one the data supports: while Desktop is closed it
  writes nothing, so the first sample after it reopens is a fresh fetch, and a **lower**
  value there means quota was genuinely restored.
- **Record the bracket, not an instant.** What is known is that the reset happened in
  `(previous sample, drop sample]`. Predict from the upper bound — the reset has definitely
  happened by then, which is the safe direction for a quota display — and mark the time
  approximate when the bracket is wider than a few sampling intervals.
- **Anchor on the most precise observation**, not the most recent: the period is exact, so a
  tight bracket from weeks ago projects forward better than yesterday's ten-hour one.
- **Persist observations.** Retention currently exceeds the period, but a machine that is off
  for a fortnight would still miss one, and a missed weekly reset costs a week.

## Limitations — read before relying on this

1. **Requires Claude Desktop, installed and running.** A terminal-only Claude Code user will not have this file. O-view must degrade to the JSONL provider, not fail.
2. **Goes stale when Desktop is closed.** The file is only as fresh as the last sample. Always compare the newest `t` against now and surface the staleness label already required by ADR-0002.
3. **Undocumented and unversioned in practice.** `version: 2` implies the shape has already changed once. Parse defensively; treat every field as nullable.
4. **Retention is finite, and tied to Desktop running.** The 11.6 h first observed was the app's uptime, not a cap — 190 h was seen later. But the file only grows while Desktop runs, so a machine left off can still lose history. Persist anything that matters: the [rollup store](../adr/0006-local-rollup-store.md) for token history, and [`weekly-resets.json`](../adr/0011-weekly-reset-derivation.md) for observed weekly resets, which are unrebuildable once they scroll out.
5. **Integer precision only.** Fine for a 2-digit tray icon, but no decimals are available.
6. **Multi-org accounts** would produce interleaved `org` values. Filter by `oauthAccount.organizationUuid`; do not assume a single org.
7. **Read-only, always.** O-view must never write to this file — it belongs to another application.

## Still worth doing later

The OAuth provider retains value beyond this file: it may expose credit balances, exact reset timestamps rather than derived ones, and works without Desktop. It moves from *critical path* to *enhancement*.
