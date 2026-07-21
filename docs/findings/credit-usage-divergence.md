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

Sketch: over a recent interval, compare deduplicated token throughput against the change in `fh`. Sustained throughput with a flat meter indicates off-plan billing. Tuning (interval length, thresholds, treatment of the sub-1% rounding floor) needs its own spike — integer percentages mean small genuine movements are invisible, so the detector must not mistake rounding for divergence.

An exact credit *balance* still needs the deferred OAuth work. Detection and spend estimation do not.

## Product decision (2026-07-21, @mlengmark)

The tray icon **continues to prioritise session %** — that remains the headline. The divergence signal therefore belongs in the popup and, where warranted, in notifications, rather than replacing the icon's number.

## Caveats

- One account, one day, two models. Whether the split is model-tier-driven, allowance-driven, or configuration-driven is **not** established — only that the plan window was bypassed.
- Published API rates were used for the estimate. Usage bundles discount up to 30%, so estimated spend is an upper bound on actual charges.
