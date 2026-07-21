# UI specification

**Agreed:** 2026-07-20 · Supersedes the informal layout sketch in the README.

Two surfaces: the always-visible **tray icon**, and the **popup panel** shown when it is clicked.

---

## 1. Tray icon

Per [ADR-0003](adr/0003-windows-tray-constraints.md) and the [legibility spike](findings/tray-icon-rendering.md), revised 2026-07-21 to a **ring gauge** ([GitHub issue #1](https://github.com/mlengmark/O-view/issues/1)):

- **A circular arc, proportional to session %, no digits.** Ring-only sidesteps the
  spike's finding (which rejected ring *plus* digits, where they competed for space)
  by removing the digits — the exact number lives in the tooltip instead.
- Arc colour — the shared `UsageLevels` bands ([issue #2](https://github.com/mlengmark/O-view/issues/2)):
  **green < 50% · amber 50–69% · red ≥ 70%**. Same classifier drives the popup bars.
- No data / estimate: faint empty ring — never a fabricated fill
- Tooltip (≤127 chars): `5h: 47% · resets 16:32 · 7d: 61%`

The icon tracks the **session** window, since that is the limit users hit first.
A threshold notification fires once when session usage first reaches the critical
band (default 70%, matching red; user-adjustable).

---

## 2. Popup panel

Roughly 400 px wide. **Docks to the work-area corner adjacent to the taskbar** —
the same placement model as the system flyouts (volume, network, calendar), so it
always opens in the same dedicated place regardless of exact click position. The
cursor selects only the monitor; the taskbar edge is derived from the work-area
inset (handles all four dock positions and auto-hide).

### Header

| Position | Content | Source |
|---|---|---|
| Top left | **O-view** title | static |
| Top left, beneath | `Updated HH:mm` + data-source label | runtime |
| Top right | Display name, email, tier badge | `~/.claude.json` → `oauthAccount` |

**Data-source label is mandatory** (CLAUDE.md rule 6): `live` · `as of HH:mm` · `local estimate`.

> ⚠️ **Tier comes from `organizationType`** (e.g. `claude_pro`). On the dev account, `seatTier` and `userRateLimitTier` are both **empty strings** — the obvious-looking fields are wrong and produce a blank badge.

Account data is read from local config, so the header needs no token and no network.

### Usage bars

Both bars show **percentage of quota consumed** — consistent metric, with time-to-reset as adjacent plain text.

| Row | Bar | Text |
|---|---|---|
| **Current session** | % of 5-hour rolling limit used | `Resets in 2h 14m · 16:32` |
| **Weekly** | % of 7-day limit used | `Resets in 3d 6h · Thu 09:00` |

*Design note:* the original spec had the session bar showing **remaining time** and the weekly bar showing **remaining tokens**. Rejected during review: a time bar cannot warn about quota exhaustion — you could sit at 95% used with 4 hours "remaining" and the bar would still look healthy, which defeats the purpose of the tool. Time is still shown, as text.

Percentages come from the OAuth provider. On JSONL fallback they are estimates and must be labelled as such.

### Statistics tiles

2 × 2 grid:

| Tile | Definition |
|---|---|
| Tokens today | Summed from rollup store, UTC day |
| Est. value today | Tokens priced at public API rates |
| Tokens · 31 days | Rollup store, trailing 31 days |
| Est. value · 31 days | As above, priced |

> **"Spend" means estimated API-equivalent value, not money charged.** Within plan limits the marginal cost is £0. These tiles answer "what would this have cost on the API" and **must be labelled `Est.`** — presenting them as actual spend would be a fabricated number.

Any tile whose window exceeds recorded history shows coverage: `3 of 31 days recorded`.

### Usage graph

Daily token totals across the trailing 31 days, from the rollup store ([ADR-0006](adr/0006-local-rollup-store.md)).

Days before install have **no data, not zero data**. Render them as an explicit empty region with an explanatory label — never as zero-height bars, which would misread as idle days.

### Off-plan usage (was: Credits)

**Implemented 2026-07-21** after [credit-usage-divergence.md](findings/credit-usage-divergence.md) established that plan percentages can be accurate and misleading at once.

When the session window shows substantial activity but a flat plan meter, the panel:

1. **Shows an amber banner** above the quota bars — the bars are correct but no longer the whole story, so the correction must appear before them, not after.
2. **Relabels the value tile** from `Est. value today` to `Est. spend today` with an `incl. off-plan usage` note. The "not money charged" framing is only true for plan usage; off-plan work bills at API rates, so the label flips with the reality.
3. **Reports estimated spend for the window**, with the caveats stated inline: published API rates, deduplicated locally, an upper bound because bundles discount up to 30%, and O-view cannot read the actual balance.

A notification fires once per onset (edge-triggered, re-armed when it clears) — this is the case the plan bars structurally cannot show.

When the plan limit is reached (≥99%) the wording changes: continued work bills beyond the plan by definition rather than by inference.

**The tray icon is unchanged** — session % remains the headline per the product decision. The divergence signal lives in the panel and notifications.

Exact credit *balances* remain deferred. The account carries `hasExtraUsageEnabled = true` and `billingType = stripe_subscription`, and [extra usage](https://support.claude.com/en/articles/12429409-manage-extra-usage-for-paid-claude-plans) and [usage bundles](https://support.claude.com/en/articles/14246112-buy-usage-bundles) are real products — but **no verified source for a credit balance has been found**, locally or via API.

Planned once a source exists: free credits, remaining credits (% bar), and reset date. Until then the section shows a short explanatory note rather than empty or invented figures.

`Limit Reset Credits` from the original spec could not be mapped to a documented concept and needs clarification.

---

## Data source summary

| UI element | Source | Status |
|---|---|---|
| Name, email, tier | `~/.claude.json` | ✅ verified present |
| Session % (`fh`) | `plan-usage-history.json` | ✅ **verified — no token needed** |
| Weekly % (`sd`) | `plan-usage-history.json` | ✅ verified |
| Reset countdowns | Derived from `fh` drops | ✅ verified exact (5.00014 h cadence) |
| Tokens today / 31d | Rollup store ← JSONL | ✅ verified; needs ADR-0006 |
| Est. value | Rollup store + price table | ✅ computable |
| Usage graph | Rollup store | ✅ sparse until history builds |
| Credits | **unknown** | ❌ spike required |

Every element except credits is served from **local files with no network call and no credential**. See [ADR-0007](adr/0007-plan-history-primary-provider.md).

Degradation path: if Claude Desktop is not installed or its data is stale, the session and weekly bars fall back to JSONL estimates labelled `local estimate`, and reset times show as unknown. The panel degrades; it does not break.
