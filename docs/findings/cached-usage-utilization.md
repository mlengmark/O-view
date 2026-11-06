# Claude Code's cached usage figures

**Status:** verified on a real machine, 2026-08-24.
**Source:** `~/.claude.json` → `cachedUsageUtilization` (Claude Code 2.1.241).

This is where the percentages behind `/status` → Usage are written to disk. It gives O-view
session and weekly utilisation **and exact reset instants** for anyone who has run Claude Code,
CLI or hosted in Claude Desktop, with no token, no network and no credential handling.

## Why it was worth finding

Before this, the two plan bars came only from Claude Desktop's `plan-usage-history.json`. A
Claude Code user with no Desktop got token counts and two permanently empty gauges, which is the
user-facing half of the "Claude CLI users breaking O-view" report.

The obvious alternative, derive a percentage from local token counts, was measured and
rejected. Against 379 rising `fh` intervals on a real machine:

| Basis | p10–p90 spread | full spread |
|---|---|---|
| tokens per percentage point | 15.5× | 314.5× |
| USD per percentage point (per sample) | 7.4× | 131.5× |
| USD per percentage point (per complete window) | **2.6×** | 5.9× |

Cost tracks the meter about twice as well as raw tokens, and aggregating to whole windows
removes most of the remaining noise, but 2.6× across the middle 80% still puts a true 50%
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
`fh:81, sd:78`, and this cache, written 11 minutes later, read `91` and `79`. Same scale, and
the cache was the fresher of the two.

## Traps

- **It is a cache, not a sampler.** `fetchedAtMs` moves when Claude Code talks to the API, so
  there is no cadence to measure and no interval to reason from. Only one refresh gap was ever
  observed here (10.1 minutes), which is an anecdote, not a distribution. Do not write a
  freshness constant that claims otherwise.
- **A percentage outlives its window.** Leave Claude Code closed across a boundary and the file
  still reports the old window's figure. Each bar carries its own `resets_at`, so compare
  against it and report unknown once it has passed, otherwise O-view confidently shows "91%"
  for a window that reset to nothing. This was watched happening live: a read at 00:00 UTC gave
  `91%` resetting at 02:00 local, and a read two minutes later gave `0%` resetting at 07:00.
- **Never step a passed `resets_at` forward.** For the five-hour window that reintroduces the
  grid bug of issue #180, the window starts on first use, not on a clock. For the weekly window
  the arithmetic is sound but the answer would be an inference carrying a reported value's zero
  uncertainty.
- **A *future* `five_hour.resets_at` is a window boundary, and places the off-plan window.**
  Within one window ahead, it minus five hours is the start, exactly. The panel's session
  reset already shows it, exact, while it is ahead of now. See [ADR-0018](../adr/0018-session-window-source.md).
- **`accountUuid` is not `organizationUuid`.** They are different identifiers, and both appear on
  the same machine, the plan-history file keys on the org. Do not match one against the other.
- **Read-only, and mind the neighbours.** The file belongs to Claude Code and sits beside
  `.credentials.json`. Rule 3 covers it exactly as it covers Claude Desktop's file.
- **It follows `CLAUDE_CONFIG_DIR`**, so locate it through `ClaudeAccount.Candidates()` rather
  than hard-coding the profile path (the failure of issues #44, #58 and #189).

## `extra_usage`: whether the account can bill beyond the plan

Read since issue #259, and the one field in this block that is not a meter.

```jsonc
"extra_usage": {
  "is_enabled": false,        // the resolved answer, the only thing the state comes from
  "user_disabled": true,      // the person turned it off, rather than being barred from it
  "spend_limit_reached": false,
  "credits_ever_enabled": true,
  "disabled_reason": null,    // a string where an account is barred for some other reason
  // the money half: null on an account with extra usage off, which has spent nothing
  "monthly_limit": null, "used_credits": null, "utilization": null,
  "currency": null, "decimal_places": null, "daily": null, "weekly": null
}
```

It exists to stop the panel asserting a charge it cannot see. The off-plan banner used to read
*"Plan limit reached, usage is billing beyond your plan"* at everyone whose 5-hour window ran
out, which for an account with extra usage switched off is not a hedge but a false statement
about their money. The three states are three wordings, and `Unknown` is not a rounding of the
other two, see `ExtraUsageStatus`.

Three things about it, and the second is the one that misread it the first time:

- **`is_enabled` is the resolved answer; the rest are the reason behind it.** An account barred
  by policy reads `is_enabled:false` with `user_disabled:false`, so taking `user_disabled` as the
  answer would call that one enabled. `spend_limit_reached` looks like a second off switch for an
  enabled account whose cap is spent, and may well be, but nothing has been observed with it
  true, so branching on it would be reasoning from a field name (rule 6).
- **Mostly-null is not empty.** Eight of its twelve fields are null and it was recorded in
  [cli-usage-refresh.md](cli-usage-refresh.md) §4 as carrying nothing even when fresh. It carries
  four booleans, and the nulls are what *off* looks like, nothing is spent, so there is no
  currency and no limit to report.
- **`is_enabled` is one of the fields Claude Code itself consumes** (15 occurrences in its
  binary, against 0 for `user_disabled` and `spend_limit_reached`). That is why the state rests
  on it alone and the other three are carried for diagnostics. See cli-usage-refresh.md §4 for
  the full count and what it implies about the fields nothing in the vendor's client reads.

**Not gated on freshness, unlike the percentages.** A percentage stops describing anything once
its window rolls over; an account setting does not roll over, it is simply older or newer. So it
is relayed with its `fetchedAtMs` stamped beside it, *"reported by Claude Code, read 09:07"*,
carrying the date once the reading is no longer from the reader's own today, rather than the
panel deciding on their behalf that an hour-old answer is no answer. Same treatment, same reason,
as the promo notice below.

## Model limits and spend

Two more parts of `utilization`, read since the model limit bar and the reported-spend figure
([ADR-0019](../adr/0019-model-limit-and-reported-spend.md)). The scoped row was first seen on
2026-11-06, on an account with a weekly limit on Fable; `spend` had been seen before, at zero
([cli-usage-refresh.md](cli-usage-refresh.md) §4).

```jsonc
"limits": [
  { "kind": "session",       "group": "session", "percent": 7,  "severity": "normal",
    "resets_at": "…", "scope": null, "is_active": false },
  { "kind": "weekly_all",    "group": "weekly",  "percent": 13, "severity": "normal",
    "resets_at": "…", "scope": null, "is_active": true },
  { "kind": "weekly_scoped", "group": "weekly",  "percent": 10, "severity": "normal",
    "resets_at": "…", "is_active": false,
    "scope": { "model": { "id": null, "display_name": "Fable" }, "surface": null } }
],
"spend": {
  "used": { "amount_minor": 0, "currency": "USD", "exponent": 2 },
  "enabled": false, "limit": null, "percent": 0, "severity": "normal",
  "disabled_reason": null, "cap": null, "balance": null, "auto_reload": null,
  "disclaimer": "…", "can_purchase_credits": false, "can_toggle": false
}
```

- **A model's own limit is a `weekly_scoped` row whose `scope.model.display_name` names the
  model.** The two account-wide rows (`session`, `weekly_all`) repeat the meters and are skipped.
  A row qualifies by its shape alone: group `weekly`, a named model, no surface. A `surface`
  would make it a limit on that model within one product, which is not the model's limit.
- **`scope.model.id` was null**, so the display name is the only name there is. A row without
  one is skipped, never guessed. `percent` is a number that may not be whole and is rounded as a
  meter's is.
- **A passed reset keeps the row.** It says the reading is old, not that the limit went away.
  The percentage goes through the same rule as the two meters, so a passed reset or an aged zero
  reads unknown.
- **`spend` states no period anywhere.** Nothing derived from it may claim one. It is why the
  panel's reported figure has no day count.
- **Only the zero shape has been observed.** `used` was `0 USD` with exponent 2 and `enabled`
  false. A non-zero amount is read from the field names, `amount_minor` over ten to the
  `exponent`, and nothing has confirmed that. A negative amount or an exponent beyond what a
  `decimal` carries reads as unreadable, not as a number.
- **Only one scoped row has been seen.** Whether other models, other plans or a surface-scoped
  row look the same is not established.
- **A broken `limits` or `spend` costs only itself.** The meters come from the same block, so
  each part is read on its own and never throws; `--diagnose` says which of absent, unreadable
  or read it found ([cli.md](../cli.md)).

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
  the obvious extension and **none has ever been observed populated**, same status as the sibling
  utilization fields above.
- **`text` is prose, and is displayed verbatim.** It also carries two figures worth lifting: an
  end date and a magnitude. Both are extracted conservatively (`PromoText`) and both are allowed
  to fail, because the message shows either way.
- **`variant` is opaque.** Only `claude` observed. Do not branch on it.

**It keeps its own clock, and that is the trap.** Freshness comes from the top-level
`cachedGrowthBookFeaturesAt`, *not* from `fetchedAtMs`, the flag cache refreshes when Claude Code
**starts**, the usage block only when `/usage` runs. Measured side by side that morning:
`cachedGrowthBookFeaturesAt` 08:24:24Z against `fetchedAtMs` 08:19:30Z. Close that day, but driven
by different triggers, so a notice can be current beside a percentage that is days old. Labelling
either from the other's timestamp is a fabricated claim about age.

**Only one candidate carried it**: `~/.claude.json` had both keys, `~/.claude/.claude.json` had
neither, the same stub-versus-populated trap as the block above, resolved the same way.

**This is a feature-flag cache, not an entitlement.** Whether the payload is targeted at this
account is unproven; the evaluation happens server-side. So the panel relays it, attributed to
Claude Code, stamped with the read time, and never states in O-view's own voice that the user's
limits are boosted (rule 6). Nothing rescales a percentage: the meters already report utilisation
against whatever limit is in force.

**Claude Desktop's file has no equivalent.** `plan-usage-history.json` was searched for `boost`,
`promo`, `multiplier` and `higher`: no hits. A Desktop-only machine gets no notice.

## What was ruled out on the way

Recorded so nobody re-runs it: the percentages are **not** in the transcripts, `sessions/`,
`telemetry/`, `stats-cache.json`, or the `clientDataCacheSlots` inside `.claude.json`. All 11
transcript record types were enumerated and none carries a meter. `system`/`api_error` records do
have a `rateLimits` field, but it is null except on an HTTP 429, it is a ground-truth marker for
having *hit* the cap, not a gauge, and it never fired on the machine examined (19× HTTP 529, 3×
HTTP 401, no 429). OpenTelemetry does not help either: none of its metrics report quota or reset,
and `OTEL_LOG_RAW_API_BODIES` captures Messages API bodies only, headers are explicitly not
captured, which is where the rate-limit values live.

The block is a **top-level** key of `.claude.json`. An earlier sweep of that file checked its
cache slots and concluded the data was absent; the conclusion was drawn over the whole file from
evidence covering only part of it.
