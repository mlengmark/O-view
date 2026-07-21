# Finding: usage can bypass the plan window entirely (credit billing)

**Observed:** 2026-07-21 · Windows 11 Pro · account `claude_pro`, `hasExtraUsageEnabled = true`
**Confirmed against real billing.** Partially answers the ADR-index open question *"Is there any source for a credit balance?"*

## The symptom

The tray icon sat at a steady green **6%** for over two hours while the machine was under heavy continuous load. `plan-usage-history.json` was updating normally (fresh sample every ~300 s, file mtime seconds old) — `fh` genuinely *was* 6%. O-view read it correctly.

## The measurement

Correlating `fh` movement against per-hour transcript activity, deduplicated by `requestId`:

| Hour (UTC) | Model | Output tokens | `fh` movement |
|---|---|---|---|
| 07-20 13 | **Opus 4.8** | 67,966 | **1% → 16%** (+15 pts) |
| 07-21 07 | **Fable 5** | 69,091 | **6% → 6%** (+0 pts) |

Near-identical volumes. Opus moved the plan meter 15 points; Fable moved it zero. Every hour that moved `fh` contained Opus usage; the pure-Fable hour produced no movement at all.

Same-day totals at published API rates:

```
claude-fable-5    141 requests   ~76M tokens   $92.75
claude-opus-4-8    10 requests   ~4.9M tokens   $7.71
                                        TOTAL  $100.46
```

**Verified against the account's billing page: €86 of extra usage — a close match.** The inference is confirmed, not merely plausible.

## What this means

**Usage billed to extra-usage credits does not advance the plan's 5-hour window.** Credits are the documented "continue past your plan limits" mechanism, billed at API rates; consumption there is a separate meter. Premium-tier model usage on this account went to credits rather than the plan allowance.

Consequences for O-view:

1. **The headline number can be true and misleading at once.** `fh` correctly reported the plan window while ~€86 was spent outside it. For a monitoring tool, reassuring-and-wrong is the worst failure mode — and it is precisely the case [CLAUDE.md](../../CLAUDE.md) rule 6 exists to prevent, applied everywhere *except* here.
2. **The "Est. value" caption is inverted for this case.** [ui-spec.md](../ui-spec.md) captions those tiles *"not money charged — within plan limits the marginal cost is £0."* For credit-billed usage it **is** money charged. The tile showed ~$93 that day while captioned as hypothetical.
3. **Credits stop being an optional panel section.** Deferred as "pending — no verified data source", it is the number that actually mattered.

## The divergence is computable locally

No new data source is required to *detect* this. O-view already holds both signals:

> Transcript activity is high **and** `fh` is flat ⇒ that usage is not drawing from the plan window.

Over the current session window (anchored on the last observed reset so it never spans one), compare deduplicated output tokens against the change in `fh`. Sustained throughput with a flat meter indicates off-plan billing.

An exact credit *balance* still needs the deferred OAuth work. Detection and spend estimation do not.

### 31-day spend view (GitHub issue #3, 2026-07-21)

The live detector is session-scoped by necessity — it compares token flow against the 5-hour meter, which is the only window where both signals exist. For a **31-day off-plan spend** total, two facts rule out a lookback classification:

- **No per-request billing-tier field.** `service_tier` is uniformly `"standard"` across all transcripts, including the Fable requests known to have billed to credits. Nothing in a record says on-plan vs off-plan.
- **The plan meter has short retention** (~12 h of samples), so past requests can't be correlated against past meter movement.

So the 31-day figure is a **per-model estimate**, not a per-request fact: the API-rate value of usage on models that bill as extra usage (`CreditBilledModels` — Fable, verified; Mythos by parity). This is honest about being inferred: the UI names the models it counts, carries the `N of 31 days recorded` coverage caveat, and states it is an upper bound. Its limits are the model set's limits — it misses plan-tier usage (Opus) that goes off-plan once the plan cap is hit (the live detector catches that), and it assumes the model→billing mapping holds on this plan.

### Calibration (spike, 2026-07-21)

The rounding-floor question is answered empirically. Walking every consecutive sample pair where `fh` rose, and summing deduplicated output tokens in each interval:

```
20 rise events, all Opus 4.8
  tokens per percentage point:  min 305 · median 2,523 · max 5,793
```

**Zero rise events on Fable 5** — across ~174K output tokens it never moved the meter once, which is the finding restated from the other direction.

Chosen thresholds, biased deliberately toward silence (a false "you're on credits" alarm would destroy trust faster than a missed one):

| Parameter | Value | Rationale |
|---|---|---|
| Min output tokens | **50,000** | ~10× the observed worst case (5,793/pt) — implies ≥8 points expected even pessimistically, ~20 at the median. Below this, a flat meter proves nothing. |
| Tolerated rise | **≤1 point** | Absorbs rounding at window edges. Two points is treated as real movement. |
| Limit-reached | **≥99%** | A pinned meter means the allowance is spent; further work bills elsewhere by definition, so volume is irrelevant there. |

Calibration comes from one account and one model, so the floor is set high rather than tuned tight. If a future account shows movement at much coarser granularity, the threshold — not the logic — is what needs revisiting.

## Product decision (2026-07-21, @mlengmark)

The tray icon **continues to prioritise session %** — that remains the headline. The divergence signal therefore belongs in the popup and, where warranted, in notifications, rather than replacing the icon's number.

## Caveats

- One account, one day, two models. Whether the split is model-tier-driven, allowance-driven, or configuration-driven is **not** established — only that the plan window was bypassed.
- Published API rates were used for the estimate. Usage bundles discount up to 30%, so estimated spend is an upper bound on actual charges.
