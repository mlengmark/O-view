# ADR-0019: A model's own limit chooses reported spend over the estimate, and its bar is read from the cached block

- **Status:** Accepted
- **Date:** 2026-11-06
- **Deciders:** @mlengmark
- **Does not change:** [ADR-0002](0002-usage-data-providers.md) and [ADR-0007](0007-plan-history-primary-provider.md), which rank sources for the *snapshot*, or [ADR-0018](0018-session-window-source.md), which places the off-plan comparison's window.
- **Evidence:** [findings/cached-usage-utilization.md](../findings/cached-usage-utilization.md#model-limits-and-spend); the code is `ModelLimit`, `ReportedSpend`, `PanelSections.ModelLimitMeters` and `PanelSections.OffPlan`.

## Context

The off-plan section used to be one figure: the API-rate value of usage on credit-billed models
(Fable). That is right while such usage falls outside the plan. An account with a weekly limit
on Fable is different: the same usage draws from that limit, so pricing it calls plan usage
off-plan. Claude Code's cache carries what is needed to tell the two apart, a
`weekly_scoped` row in `limits[]` naming a model, and a `spend` block holding what Claude says
was spent past the plan.

That raised two questions the earlier decisions did not answer.

## Decision

### 1. A model-scoped row, not the plan name, picks which figure the section shows

**If the cached block holds any model-scoped weekly row, the section shows reported spend. If
it holds none, it shows today's estimate. The two are never added and never shown together.**

- A row qualifies by its shape: group `weekly`, a named model, no surface. Nothing reads the
  plan tier, because the tier does not say which limits an account has, and an account that
  gains or loses one should change the panel without a code change.
- **A row whose reset has passed still counts.** That says the reading is old, not that the
  limit went away.
- A non-zero amount shows; a zero hides the section however heavily the model was used and
  whatever `spend.enabled` says.
- With no readable amount, the setting decides: `extra_usage.is_enabled` on shows `unknown`
  (money may have moved and nobody reported how much), off or not stated hides the section.
  "On" means that field alone, because an unreadable `spend` keeps no `enabled` of its own.
- The heading carries no day count and the label no `Est.`: `spend` states no period, so any
  span written beside it would be O-view's invention.

The banner, the value tile's relabel and the notification still read the estimate. They answer
a different question, whether local work is running past a meter that is not moving.

### 2. The model bar reads the cached block directly, beside "snapshots are chosen whole"

**`Weekly · <model>` is built from Claude Code's cached block and never from the snapshot that
won.** A snapshot is one source whole ([ADR-0002](0002-usage-data-providers.md)), and Desktop's
sampled history has no model limits. Had the bar followed the snapshot, it would vanish
whenever Desktop's reading won, which says nothing about the account. Merging the row into the
snapshot field by field is what that rule forbids. So a model limit and the reported spend are
not part of a snapshot, and the same block gives the same bar and the same figure whichever
source won.

The percentage passes through the rule the two meters' cached figures use
(`CachedUtilizationProvider.CurrentWindowPercent`): a passed reset or an aged zero reads
unknown with no fill, and the row stays.

## Unproven

- **Only one scoped row has been seen**, a Fable row on the developer's own cache on
  2026-11-06. Its `model.id` was null, so the display name is the only name there is, and a row
  without one is skipped rather than guessed.
- **Only the zero `spend` shape has been seen.** A non-zero amount is read as `amount_minor`
  over ten to the `exponent` from the field names alone. The sample states and the tests prove
  the panel's handling of one; no real account has.
- **The client may not read `limits[]`.** `weekly_all` counts zero in Claude Code's binary, which
  suggests it, though `limits` itself was not counted. If so, the shape could change without
  notice; the row would then stop qualifying and the account would fall back to no bar and the
  estimate, with `--diagnose` showing what the file held.
- Neither panel has been watched changing live with a real model limit on a real desktop.

If a real account contradicts any of this, supersede the ADR rather than patching it.

## Consequences

- An account with a model limit no longer sees its in-limit usage called off-plan, and an
  account without one sees exactly what it saw before.
- The section is absent while there is nothing to show, on every account.
- `--diagnose` prints what `limits[]` and `spend` held, or why they held nothing, so a report
  about a missing bar or figure says whether the file had them.
- The choice lives in `PanelSections` and nowhere else; neither head decides whether the
  section shows or which figure it carries.
