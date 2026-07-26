# UI specification

**Agreed:** 2026-07-20 · Supersedes the informal layout sketch in the README.

Three surfaces: the always-visible **tray icon**, the **popup panel** shown when it is left-clicked, and the **tray menu** flyout shown when it is right-clicked.

---

## 1. Tray icon

Per [ADR-0003](adr/0003-windows-tray-constraints.md) and the [legibility spike](findings/tray-icon-rendering.md), revised 2026-07-21 to a **ring gauge** ([GitHub issue #1](https://github.com/mlengmark/O-view/issues/1)), then unified with the exe icon as the **brand mark** (ring + centre pupil) on 2026-07-22:

- **A circular arc, proportional to session %, plus a filled centre pupil — the brand
  "eye" — and no digits.** Ring-only sidesteps the spike's finding (which rejected ring
  *plus* digits, where they competed for space) by removing the digits — the exact
  number lives in the tooltip instead. The pupil is brand, not a second signal: it
  carries no number and takes the arc's colour, so it coexists with the ring.
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
| **Weekly** | % of 7-day limit used | reset line only once derived (see below) |

*Design note:* the original spec had the session bar showing **remaining time** and the weekly bar showing **remaining tokens**. Rejected during review: a time bar cannot warn about quota exhaustion — you could sit at 95% used with 4 hours "remaining" and the bar would still look healthy, which defeats the purpose of the tool. Time is still shown, as text.

**Weekly reset ([GitHub issue #6](https://github.com/mlengmark/O-view/issues/6)):** derived from `sd` drops the same way the 5-hour reset is derived from `fh` drops — but two things differ. The weekly period is undocumented (disputed 7-day vs 72-hour), so it is **measured from two observed resets**, never assumed; and weekly resets are rare while plan-history retention is ~1.5 days, so observed resets are **persisted** (`weekly_resets` table). Until two clean resets have accrued, the reset is genuinely unknown and **no line is shown** — an honest blank replaces the earlier "Reset time unknown", which read as broken. Restart-snap drops (across a Desktop-closed gap) are rejected, not counted. The API almost certainly holds this directly, but reading it needs the encrypted OAuth token (deferred — ADR-0007).

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

Daily token totals across the trailing 31 days, from the rollup store ([ADR-0006](adr/0006-local-rollup-store.md)). Enhanced per [issues #4 and #5](https://github.com/mlengmark/O-view/issues/4):

- **One bar per day**, height by absolute daily tokens.
- **Colour: light → dark blue by intensity within each calendar week** (issue #5) — each Mon–Sun week is its own gradient scale, so the busiest day of a week is darkest. The absolute weekly token limit is unknown, so this is *relative* intensity, not a fraction of a limit.
- **Dotted vertical gridlines at Monday boundaries** (issue #5), giving weekly context. The plan's true weekly reset isn't derivable, so calendar weeks (Mon–Sun) are the anchor — a clean visual reference, not a claim about the plan boundary.
- **Vertical date labels under every column** (issue #4), small but legible, and **centred on their own bar** ([issue #31](https://github.com/mlengmark/O-view/issues/31)). Bar and label are placed from a single column-centre anchor rather than each carrying its own offset. A rotated `TextBlock` renders one line height to the *left* of its `Canvas.Left` (a `RenderTransform` doesn't move the layout box), so the anchor is derived from `RotateTransform.TransformBounds` — measured, not a constant, since line height moves with font, DPI and OS text scaling. The former constant left labels 2.3 px adrift, a fifth of a column at 31 days.
- **Hover tooltip** per bar: date and exact token count (issue #5).

Days before install are **blank columns** (no bar) with their date still labelled — with the date axis, an empty column reads as "no data" on its own, so the earlier "before O-view install" caption was **removed to save space** (issue #4). They are never rendered as zero-height bars, which would misread as idle days.

### Off-plan usage (was: Credits)

**Implemented 2026-07-21** after [credit-usage-divergence.md](findings/credit-usage-divergence.md) established that plan percentages can be accurate and misleading at once.

When the session window shows substantial activity but a flat plan meter, the panel:

The panel carries the signal in **two independent registers**:

**Live (current session window)** — the real-time divergence detector:

1. **An amber banner** above the quota bars — the bars are correct but no longer the whole story, so the correction must appear before them, not after.
2. **Relabels the value tile** from `Est. value today` to `Est. spend today` with an `incl. off-plan usage` note. The "not money charged" framing is only true for plan usage; off-plan work bills at API rates, so the label flips with the reality.

A notification fires once per onset (edge-triggered, re-armed when it clears). When the plan limit is reached (≥99%) the wording changes: continued work bills beyond the plan by definition rather than by inference.

**Standing (last 31 days)** — the `Off-plan usage · last 31 days` section ([GitHub issue #3](https://github.com/mlengmark/O-view/issues/3)):

- Shows **estimated credit spend over 31 days**, the API-rate value of usage on credit-billed models ([`CreditBilledModels`](../src/O-view.Core/Models/CreditBilledModels.cs) — currently Fable, the one case verified against billing).
- **Why not a lookback classifier:** there is no per-request billing-tier field (`service_tier` is uniformly `"standard"`, even on requests known to have billed to credits), and the plan meter's short retention makes retroactive divergence impossible. So the 31-day figure is a per-model estimate, not a per-request fact — hence the explicit "models billed as extra usage (Fable)" caption.
- Carries the same coverage caveat as the other 31-day tiles (`N of 31 days recorded`) and, since [issue #32](https://github.com/mlengmark/O-view/issues/32), a **two-clause** caption: published API rates, balance unreadable — check billing for exact. The mechanics that used to be spelled out here (deduplicated locally; an upper bound, as bundles discount up to 30%) were cut for space — they described *how* the estimate is built, whereas the two clauses kept are the ones that stop the figure being read as money charged, which is what rule 6 actually requires.

The two registers are independent by design: the 31-day figure shows even when the current session is on-plan, and the live banner shows even before any credit spend has accrued.

**The tray icon is unchanged** — session % remains the headline per the product decision. The off-plan signal lives in the panel and notifications.

Exact credit *balances* remain deferred (no local or API source found). What the section shows is estimated **spend**, not remaining balance.

`Limit Reset Credits` from the original spec could not be mapped to a documented concept and needs clarification.

---

## 3. Tray menu

The right-click surface. Rebuilt as a **docked flyout window** ([GitHub issue #33](https://github.com/mlengmark/O-view/issues/33)), replacing a WPF `ContextMenu` placed at `PlacementMode.MousePoint`.

**Why the cursor placement had to go.** The menu opened wherever the pointer was when the tray icon was hit — and the tray icon sits *inside* the taskbar, so that is reliably the one place a menu cannot fully fit. It clipped into the taskbar and off the screen edge, leaving items unclickable, and every item added made it worse.

**Placement.** The flyout ignores the cursor for positioning and docks to the work-area corner adjacent to the taskbar, through the same [`PopupPositioner`](../src/O-view.Tray/Popup/PopupPositioner.cs) the detail panel uses — one placement model for both surfaces, matching the system flyouts (volume, network, calendar). The work area **excludes the taskbar by definition**, so clearing it is structural, not a margin that happens to be large enough. The cursor still selects which monitor.

**Scaling.** A `StackPanel` under `SizeToContent="Height"`, with `ActualHeight` re-measured on every open. Adding a row needs no change to the placement code, and a taller menu still docks above the taskbar.

**Design.** Roughly 272 px wide, on the panel's own palette ([`PanelTheme`](../src/O-view.Tray/Popup/PanelTheme.cs) — shared with the detail panel so the two surfaces cannot drift apart), rounded 10 px card, 34 px rows with a 6 px rounded hover fill, and a compact header carrying the brand mark and version. The header exists because the flyout appears in a fixed corner rather than under the cursor: detached placement without a label reads as a stray window.

| Row | Kind | Notes |
|---|---|---|
| Run at startup | toggle | Re-read from `HKCU\...\Run` on every open |
| Notify at *N*% session usage | toggle | *N* from settings |
| Copy diagnostics | action | Support bundle to clipboard |
| Check for updates… | action | Directly above Exit ([issue #18](https://github.com/mlengmark/O-view/issues/18)) |
| Exit O-view | action | |

**Toggles leave the flyout open** so the tick confirms the change; **actions close it first**, so a balloon or a modal never appears behind a topmost window.

A tick shows the state as it **actually stands after the write**, not the state requested — a registry write can fail, and a tick claiming otherwise would be a fabricated fact about the machine (rule 6). Checkmarks are drawn as vector `Path` geometry rather than Segoe Fluent Icons glyphs, which are not present on every supported build and would render as tofu.

Rows are `Button`s, so Tab/Space/Enter work; Up/Down is wired explicitly to match the menu behaviour it replaces.

**Dismissal is Esc or an outside click, and taking the foreground is not optional.** A tray-resident app owns no activated window, so the flyout must foreground its own HWND on open or it never receives the deactivation that closes it ([issue #11](https://github.com/mlengmark/O-view/issues/11)). `SetForegroundWindow` alone is **not sufficient**: Windows grants it only to a process that already holds the foreground or received the last input event, and a tray app frequently holds neither. Losing that race is not cosmetic — the flyout is shown but never activated, so it never fires `Deactivated` and **stays on screen with no way to dismiss it**, which is strictly worse than the clipping this replaced. That was reproduced on a live desktop, then fixed with an `AttachThreadInput` fallback: share an input queue with the current foreground thread for the duration of the call, and the grant succeeds.

**Verification hooks.** `--menu-samples <dir>` renders the flyout in both themes for visual review; `--show-menu` (with optional `--menu-pin`, `--menu-theme`) opens the real thing on the real desktop, which is the only way to check the docked placement against a live taskbar. Like `--diagnose`, `--menu-samples` is handled **before** the single-instance mutex, so it runs on a machine already running O-view.

Measured on a 2560×1440 display, taskbar bottom-docked at `y=1392`: the flyout lands at `2276,1159–2548,1380` — 272×221, inside the work area, 12 px clear of the taskbar and 12 px from the screen edge.

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
