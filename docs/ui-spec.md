# UI specification

**Agreed:** 2026-07-20 · Supersedes the informal layout sketch in the README.

Two surfaces: the always-visible **tray icon**, and the **popup panel** shown when it is clicked.

---

## 1. Tray icon

Per [ADR-0003](adr/0003-windows-tray-constraints.md) and the [legibility spike](findings/tray-icon-rendering.md):

- **Two digits, no ring** — the session utilisation percentage (e.g. `47`)
- Digit colour: green < 60% · amber 60–84% · red ≥ 85%
- At 100%: full-ring `!` symbol (three digits do not fit legibly at 16 px)
- Tooltip (≤127 chars): `5h: 47% · resets 16:32 · 7d: 61%`

The icon tracks the **session** window, since that is the limit users hit first.

---

## 2. Popup panel

Roughly 400 px wide, opening anchored to the tray icon.

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

### Credits

Deferred pending a spike. The account carries `hasExtraUsageEnabled = true` and `billingType = stripe_subscription`, and [extra usage](https://support.claude.com/en/articles/12429409-manage-extra-usage-for-paid-claude-plans) and [usage bundles](https://support.claude.com/en/articles/14246112-buy-usage-bundles) are real products — but **no verified source for a credit balance has been found**, locally or via API.

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
