# Reference: how O-view prices tokens

**Rates as of:** 2026-08-31 · **Source:** <https://platform.claude.com/docs/en/about-claude/pricing>

The one place the rate structure is written down in prose. `ModelCatalog` holds the table,
`CostEstimator` applies it, and both point here rather than restating parts of it — two copies
of a rate is how the cache-write comment in `CostEstimator` stayed accurate about itself while
being wrong about the data ([#255](https://github.com/mlengmark/O-view/issues/255)).

**Est. figures are not money charged.** Within plan limits the marginal cost is £0/$0; these
price tokens at published API rates so a user can see what their usage would be worth. The UI
never drops the `Est.` prefix (CLAUDE.md rule 6).

## Five published columns per model, and none of them derived

Anthropic publishes all five. O-view stores all five.

| Column | `ModelRates` field | Transcript field |
|---|---|---|
| Base input tokens | `InputPerMTok` | `usage.input_tokens` |
| 5-minute cache writes | `CacheWrite5mPerMTok` | `usage.cache_creation.ephemeral_5m_input_tokens` |
| 1-hour cache writes | `CacheWrite1hPerMTok` | `usage.cache_creation.ephemeral_1h_input_tokens` |
| Cache hits & refreshes | `CacheReadPerMTok` | `usage.cache_read_input_tokens` |
| Output tokens | `OutputPerMTok` | `usage.output_tokens` |

The published page also states the multipliers those cache columns are derived *from* — 1.25×
base input for a 5-minute write, 2× for a 1-hour write, 0.1× for a hit. **Those are how the
prices are derived upstream, not something to re-derive downstream.** `CostEstimator` held one
`CacheWriteMultiplier = 1.25m` constant standing in for two different published prices, and the
transcripts on the development machine were almost entirely 1-hour: cache-write value understated
by 37.5% of its true amount for as long as the constant existed. Multipliers are quoted here for
readers; they appear nowhere in the code.

### The table in force

| Model | Input | 5m write | 1h write | Cache hit | Output |
|---|---|---|---|---|---|
| Fable 5, Mythos 5 | $10 | $12.50 | $20 | $1 | $50 |
| Opus 5, 4.8, 4.7, 4.6, 4.5 | $5 | $6.25 | $10 | $0.50 | $25 |
| Sonnet 5 | $2 | $2.50 | $4 | $0.20 | $10 |
| Sonnet 4.6, 4.5, 4 | $3 | $3.75 | $6 | $0.30 | $15 |
| Haiku 4.5 | $1 | $1.25 | $2 | $0.10 | $5 |

Per million tokens, USD. Sonnet 4.6, 4.5 and 4 share one catalogue row matched by the
`claude-sonnet-4` prefix — correct only because all three share a price, and load-bearing: if one
diverges it needs its own row, because longest-prefix matching will keep resolving all three
there ([#256](https://github.com/mlengmark/O-view/issues/256)).

## Modifiers

Two published modifiers change what a request costs, and the transcript records both on `usage`.

| Modifier | Values | Effect |
|---|---|---|
| `speed` | `standard`, `fast` | Fast mode is **its own rate row**, published only for Opus 5 and Opus 4.8 at $10 / $50 |
| `inference_geo` | `global`, `us`, `not_available` | `us` applies **1.1× to every category** |

Measured on this machine 2026-08-31: `speed` is `standard` on 15,817 of 15,817 Claude Code
assistant records; `inference_geo` is `not_available` on 15,851 of 15,851, and on 295 of 296
Cowork audit records (the remaining one carries JSON `null`). **Inactive is not absent** — the
fields are there, they are read, and a value that ever changes is priced or refused.

**Fast mode is a rate row rather than a multiplier** for the same reason the cache columns are:
Anthropic publishes fast-mode input and output prices outright, and states that the prompt-caching
multipliers apply on top of them. `inference_geo` genuinely *is* published as a multiplier, so it
is stored as one.

### Fail to unknown, never to a default

A price O-view cannot look up yields `null`, and the panel names it — the same path an
unrecognised model already takes through `PanelStatistics.UnpricedModels`. Three cases:

- **an unrecognised model** — a Claude released after this table was written;
- **an unrecognised modifier value** — anything `speed` or `inference_geo` carries that is not in
  the table above;
- **fast mode on a model with no published fast row** — Sonnet, Haiku, Opus 4.7, 4.6 and 4.5.
  Everything except Opus 5 and Opus 4.8, in other words: whether a model *accepts* the flag is
  not the test, because a model can accept it and still have no published price to charge.

Falling back to standard rates in any of these would put a confident *cheaper* number on screen.
That is the failure this whole design is against.

## Cache writes with no recorded TTL

`TokenSplit` carries a third cache-write bucket, `CacheWriteTtlUnrecorded`. It is a migration
artefact, not a rate.

Rows ingested before O-view read `usage.cache_creation` carry a write total with no attribution,
and it cannot be recovered from the store — the transcripts that would answer for the oldest rows
are the ones Claude Code has already deleted. Adding the TTL columns therefore rewinds every
transcript watermark, so the next poll re-reads what is still on disk and replaces those rows with
attributed ones. What is out of that reach stays in this bucket, is priced at the **5-minute**
rate — the cheaper of the two, so it understates rather than overstates — and the panel states the
assumption in its caveat. Wiping the store instead would have discarded every day whose transcript
is gone, which is the history [ADR-0006](../adr/0006-local-rollup-store.md) exists to keep.

## Keeping the table right

The rates have been wrong twice, in two different ways, and each way defeats the check that
catches the other.

**A date, as data.** `ModelCatalog.AsOf` is a `DateOnly`, not a comment. Past 90 days the Est.
tiles carry `rates: bundled, as of <date>`. Move it only when every row has actually been
re-checked, in the same commit as any correction — a date advanced on its own says the table was
verified when it was not.

**A weekly drift check that compares values.** `RateCardFeed` fetches the published table and
returns a *difference list*, never a rate card (ADR-0016). Age alone would never have caught the
Sonnet 5 row, which recorded a scheduled price increase that was later cancelled — it was wrong on
the day it was written. A parse failure returns `null`: an honest "did not check", never a silent
pass.

**Calibration against a reported figure.** The strongest check, and the one that found #256.
Claude Code's own `/usage` summary prints token counts *and* a dollar total, so the rates are the
only unknown:

```
Sonnet 5   449.8K in · 895.2K out · 190.6M cache read · 3.4M cache write → $56.50
  at the published $2/$10 table:  $56.47   (−0.05%)
  at the cancelled $3/$15 table:  $84.71   (+49.9%)
```

`CostEstimator.RelativeError(rates, tokens, reportedUsd)` is that comparison. It takes a whole
rate row rather than solving for one column, so a wrong cache column and a wrong TTL mix are both
visible where solving hides the first. It catches every class of estimator error — a wrong rate, a
wrong column, an unread modifier, a de-duplication fault — where the drift check catches only
rates.

**It is run by hand, and that is a real limitation.** No file on this machine carries a reported
dollar figure: `cachedUsageUtilization.utilization.*.used_dollars` is null and `spend.used` is
zero on the account measured, and parsing another application's terminal output to get one is the
fragility [cli-usage-refresh.md](../findings/cli-usage-refresh.md) warns about. The diagnostics
bundle prints the rate card in force and names this procedure; it does not print a number O-view
did not compute.

**Against real billing, once.** [credit-usage-divergence.md](../findings/credit-usage-divergence.md)
compared $100.46 of estimate against €86 of actual billing on 2026-07-21. That is the only check
these rates have ever had against money that was really charged, it cannot be automated, and it is
worth repeating when an invoice is to hand.

## Rejected: asking Claude Code for the rates

`ClaudeCliRefresher` exists and makes this look cheap. It is the wrong tool twice over. A model
asked for a price returns a plausible number with no provenance, which is precisely what rule 6
prohibits — and it is not free: an unrecognised argument through that entry point was measured at
49,094 cache-write + 97,456 cache-read + 470 output, roughly $0.37–0.55 per call at Opus 5 rates,
spent against the user's own allowance by a feature built around costing zero.
