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

| Property | Observed |
|---|---|
| Sample interval | **300.02 s median** (5 minutes), min 298.4 s |
| Samples in file | 139, spanning 11.6 h |
| File size | 11,899 bytes |
| Value type | Integer percent — no fractional precision |

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

## Limitations — read before relying on this

1. **Requires Claude Desktop, installed and running.** A terminal-only Claude Code user will not have this file. O-view must degrade to the JSONL provider, not fail.
2. **Goes stale when Desktop is closed.** The file is only as fresh as the last sample. Always compare the newest `t` against now and surface the staleness label already required by ADR-0002.
3. **Undocumented and unversioned in practice.** `version: 2` implies the shape has already changed once. Parse defensively; treat every field as nullable.
4. **Short retention.** 139 samples ≈ 11.6 h. Whether that is a hard cap or simply the app's uptime is undetermined — the two are indistinguishable from a single observation. Either way, the [rollup store](../adr/0006-local-rollup-store.md) must persist samples for history.
5. **Integer precision only.** Fine for a 2-digit tray icon, but no decimals are available.
6. **Multi-org accounts** would produce interleaved `org` values. Filter by `oauthAccount.organizationUuid`; do not assume a single org.
7. **Read-only, always.** O-view must never write to this file — it belongs to another application.

## Still worth doing later

The OAuth provider retains value beyond this file: it may expose credit balances, exact reset timestamps rather than derived ones, and works without Desktop. It moves from *critical path* to *enhancement*.
