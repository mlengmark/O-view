# Claude Code's cached usage figures

**Status:** verified on a real machine, 2026-08-24.
**Source:** `~/.claude.json` → `cachedUsageUtilization` (Claude Code 2.1.241).

This is where the percentages behind `/status` → Usage are written to disk. It gives O-view
session and weekly utilisation **and exact reset instants** for anyone who has run Claude Code —
CLI or hosted in Claude Desktop — with no token, no network and no credential handling.

## Why it was worth finding

Before this, the two plan bars came only from Claude Desktop's `plan-usage-history.json`. A
Claude Code user with no Desktop got token counts and two permanently empty gauges, which is the
user-facing half of the "Claude CLI users breaking O-view" report.

The obvious alternative — derive a percentage from local token counts — was measured and
rejected. Against 379 rising `fh` intervals on a real machine:

| Basis | p10–p90 spread | full spread |
|---|---|---|
| tokens per percentage point | 15.5× | 314.5× |
| USD per percentage point (per sample) | 7.4× | 131.5× |
| USD per percentage point (per complete window) | **2.6×** | 5.9× |

Cost tracks the meter about twice as well as raw tokens, and aggregating to whole windows
removes most of the remaining noise — but 2.6× across the middle 80% still puts a true 50%
anywhere between 31% and 81%. That is a fabricated number under rule 6, so no amount of tuning
made the derivation shippable. These figures need no deriving.

## Shape

```jsonc
"cachedUsageUtilization": {
  "fetchedAtMs": 1787528943196,
  "accountUuid": "…",
  "utilization": {
    "five_hour": {
      "utilization": 91,
      "resets_at": "2026-08-24T00:00:00.046735+00:00",
      "limit_dollars": null, "used_dollars": null, "remaining_dollars": null
    },
    "seven_day": { "utilization": 79, "resets_at": "2026-08-24T21:00:00.046756+00:00", … },

    // Present on every real file, null on a plan with no separate meter for them.
    "seven_day_oauth_apps": null, "seven_day_opus": null, "seven_day_sonnet": null,
    "seven_day_cowork": null, "seven_day_omelette": null,
    "tangelo": null, "iguana_necktie": null, "omelette_promotional": null,
    "nimbus_quill": { "utilization": 0, "resets_at": null, … },
    "cinder_cove": null, "amber_ladder": null,
    "extra_usage": { "is_enabled": false, "monthly_limit": null, … },
    "limits": …, "spend": …, "member_dashboard_available": …
  }
}
```

Verified against a user's `/status` screen: it displayed *Current session 80% used, Resets 2am*
and *Current week 78% used, Resets Aug 24, 11pm*; the file's `resets_at` values converted to that
user's zone (Europe/Copenhagen) give exactly 02:00 and 23:00.

Cross-checked against Desktop's own file on the same machine: `plan-usage-history.json` read
`fh:81, sd:78`, and this cache — written 11 minutes later — read `91` and `79`. Same scale, and
the cache was the fresher of the two.

## Traps

- **It is a cache, not a sampler.** `fetchedAtMs` moves when Claude Code talks to the API, so
  there is no cadence to measure and no interval to reason from. Only one refresh gap was ever
  observed here (10.1 minutes), which is an anecdote, not a distribution. Do not write a
  freshness constant that claims otherwise.
- **A percentage outlives its window.** Leave Claude Code closed across a boundary and the file
  still reports the old window's figure. Each bar carries its own `resets_at`, so compare
  against it and report unknown once it has passed — otherwise O-view confidently shows "91%"
  for a window that reset to nothing. This was watched happening live: a read at 00:00 UTC gave
  `91%` resetting at 02:00 local, and a read two minutes later gave `0%` resetting at 07:00.
- **Never step a passed `resets_at` forward.** For the five-hour window that reintroduces the
  grid bug of issue #180 — the window starts on first use, not on a clock. For the weekly window
  the arithmetic is sound but the answer would be an inference carrying a reported value's zero
  uncertainty.
- **`accountUuid` is not `organizationUuid`.** They are different identifiers, and both appear on
  the same machine — the plan-history file keys on the org. Do not match one against the other.
- **Read-only, and mind the neighbours.** The file belongs to Claude Code and sits beside
  `.credentials.json`. Rule 3 covers it exactly as it covers Claude Desktop's file.
- **It follows `CLAUDE_CONFIG_DIR`**, so locate it through `ClaudeAccount.Candidates()` rather
  than hard-coding the profile path (the failure of issues #44, #58 and #189).

## A second block in the same file: promo notices

`cachedGrowthBookFeatures.tengu_rate_limit_promo_notices` is what Claude Code renders beside its
own usage bars when limits are temporarily boosted. Measured 2026-08-31:

```json
"tengu_rate_limit_promo_notices": [
  { "bar": "seven_day",
    "text": "+50% weekly limits promo through Aug 31 · clau.de/cc-50-promo",
    "variant": "claude" }
]
```

- **`bar` shares this document's key space.** `seven_day` is the weekly meter, so a notice
  attaches to a gauge by name rather than by position. Model-scoped forms (`seven_day_opus`) are
  the obvious extension and **none has ever been observed populated** — same status as the sibling
  utilization fields above.
- **`text` is prose, and is displayed verbatim.** It also carries two figures worth lifting: an
  end date and a magnitude. Both are extracted conservatively (`PromoText`) and both are allowed
  to fail, because the message shows either way.
- **`variant` is opaque.** Only `claude` observed. Do not branch on it.

**It keeps its own clock, and that is the trap.** Freshness comes from the top-level
`cachedGrowthBookFeaturesAt`, *not* from `fetchedAtMs` — the flag cache refreshes when Claude Code
**starts**, the usage block only when `/usage` runs. Measured side by side that morning:
`cachedGrowthBookFeaturesAt` 08:24:24Z against `fetchedAtMs` 08:19:30Z. Close that day, but driven
by different triggers, so a notice can be current beside a percentage that is days old. Labelling
either from the other's timestamp is a fabricated claim about age.

**Only one candidate carried it**: `~/.claude.json` had both keys, `~/.claude/.claude.json` had
neither — the same stub-versus-populated trap as the block above, resolved the same way.

**This is a feature-flag cache, not an entitlement.** Whether the payload is targeted at this
account is unproven; the evaluation happens server-side. So the panel relays it — attributed to
Claude Code, stamped with the read time — and never states in O-view's own voice that the user's
limits are boosted (rule 6). Nothing rescales a percentage: the meters already report utilisation
against whatever limit is in force.

**Claude Desktop's file has no equivalent.** `plan-usage-history.json` was searched for `boost`,
`promo`, `multiplier` and `higher`: no hits. A Desktop-only machine gets no notice.

## What was ruled out on the way

Recorded so nobody re-runs it: the percentages are **not** in the transcripts, `sessions/`,
`telemetry/`, `stats-cache.json`, or the `clientDataCacheSlots` inside `.claude.json`. All 11
transcript record types were enumerated and none carries a meter. `system`/`api_error` records do
have a `rateLimits` field, but it is null except on an HTTP 429 — it is a ground-truth marker for
having *hit* the cap, not a gauge, and it never fired on the machine examined (19× HTTP 529, 3×
HTTP 401, no 429). OpenTelemetry does not help either: none of its metrics report quota or reset,
and `OTEL_LOG_RAW_API_BODIES` captures Messages API bodies only — headers are explicitly not
captured, which is where the rate-limit values live.

The block is a **top-level** key of `.claude.json`. An earlier sweep of that file checked its
cache slots and concluded the data was absent; the conclusion was drawn over the whole file from
evidence covering only part of it.
