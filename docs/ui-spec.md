# UI specification

**Agreed:** 2026-07-20 · Supersedes the informal layout sketch in the README.

Three surfaces: the always-visible **tray icon**, the **popup panel** shown when it is left-clicked, and the **tray menu** flyout shown when it is right-clicked — plus the app's **dialogs** and its **setup wizard**, which carry the same brand.

---

## 0. What binds on which platform

**Added 2026-08-03, when the Linux head landed.** This document was written for a Windows-only
app and most of it still describes the Windows head specifically. Two kinds of statement are
mixed together throughout, and they are not equally binding:

| | Binds where | Lives in |
|---|---|---|
| **The shared contract** — *what* a surface says and when. Icon geometry and colour bands, the tooltip's wording and its degraded forms, panel copy, the reset-line phrasing, the countdown format, tile labels, which states are "unknown" rather than zero | **Both heads.** A user should not be told a different thing on a different OS | `O-view.App` — `TrayIconGeometry`, `PanelPalette`, `PanelText`. Changing the wording here means changing it in one place, and 21 exact-string tests pin it |
| **Windows presentation** — *where* a surface appears and how it moves. Docked placement, `PopupPositioner`, work-area insets, the 230 ms rise, foreground/`AttachThreadInput` handling, `Shell_NotifyIcon` behaviour, Segoe glyph avoidance | **The Windows head only** | `O-view.Tray` |

Read every section below as Windows presentation unless it is describing content. Where Linux
**cannot** meet a section, it is noted inline rather than left to be inferred — the three real
divergences are the panel's placement (§2), the menu (§3) and motion (§4).

The Linux head's own constraints are [ADR-0013](adr/0013-linux-ui-toolkit.md) and the [tray
spike](findings/linux-tray-spike.md). Nothing in this document has been verified on Linux
hardware; see the README's support matrix.

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
- Tooltip (≤127 chars): `5h: 47% · resets 16:32 · 7d: 61% · resets ~Tue 06:28`. The weekly reset joins on the same terms as the session one — shown once derived, absent while unknown, `~` when the observation behind it was bracketed by a gap in Desktop's sampling ([ADR-0011](adr/0011-weekly-reset-derivation.md)).

The icon tracks the **session** window, since that is the limit users hit first.
A threshold notification fires once when session usage first reaches the critical
band (default 70%, matching red; user-adjustable).

> **This section is the shared contract**, and the two heads render it from the same
> `TrayIconGeometry` — a divergence here would mean the same usage looked like a different
> number on a different OS. What differs is only the rasteriser (GDI+ on Windows, Skia on
> Linux) and the **sizes asked for**: Windows requests 16/20/24 px, while SNI hosts commonly
> want 22–24 px and 48 px on HiDPI. That last part matters — the original legibility
> measurements cover 16/20/24 and **do not carry to 48 px untested**, which is what
> `o-view --samples <dir>` exists to make judgeable from images rather than from prose.

---

## 2. Popup panel

Roughly 400 px wide. **Docks to the work-area corner adjacent to the taskbar** —
the same placement model as the system flyouts (volume, network, calendar), so it
always opens in the same dedicated place regardless of exact click position. The
cursor selects only the monitor; the taskbar edge is derived from the work-area
inset (handles all four dock positions and auto-hide).

> **Linux: the placement above does not apply; the content below does.** SNI gives an
> application no way to learn where its own icon was drawn — there is no
> `Shell_NotifyIconGetRect` equivalent — and a Wayland client generally cannot position its
> own surface at all. Approximating the docking would put the panel in the wrong place most
> of the time, which reads as broken, so the Linux panel is a **plain centred window**
> ([ADR-0013](adr/0013-linux-ui-toolkit.md)). Everything from *Header* down — the copy, the
> bands, the tiles, the graph's rules about absent days — is the shared contract and holds
> identically.

### Header

| Position | Content | Source |
|---|---|---|
| Top left | **O-view** title | static |
| Top left, beneath | `As of HH:mm` — when the reading was taken | runtime |
| Top right | Display name, email, tier badge | `~/.claude.json` → `oauthAccount` |

**The line states when the figures were captured, never when the panel was drawn**
(`PanelText.Freshness`): `As of now` · `As of HH:mm` · `Local estimate · as of HH:mm` ·
`Reading time unknown` · `No data`. It read `Updated 11:34 · live` until issue #192 — a
repaint clock beside a claim of liveness, when O-view only ever holds the last poll's
result and the source samples on its own schedule. `As of now` is claimed only within the
capture's own clock minute; after that the reading is a past log and carries its own time.

**Estimates stay labelled** (CLAUDE.md rule 6, ADR-0002): the capture time joins `Local
estimate`, it does not replace it. Live and Stale share one wording deliberately — the
capture time states the age more precisely than the tier split did.

> ⚠️ **Tier comes from `organizationType`** (e.g. `claude_pro`). On the dev account, `seatTier` and `userRateLimitTier` are both **empty strings** — the obvious-looking fields are wrong and produce a blank badge.

Account data is read from local config, so the header needs no token and no network.

### Usage bars

Both bars show **percentage of quota consumed** — consistent metric, with time-to-reset as adjacent plain text.

| Row | Bar | Text |
|---|---|---|
| **Current session** | % of 5-hour rolling limit used | `Resets in 2h 14m · 16:32` |
| **Weekly** | % of 7-day limit used | `Resets in 6d 3h · Tue 06:28` — same shape, plus a weekday (see below) |

*Design note:* the original spec had the session bar showing **remaining time** and the weekly bar showing **remaining tokens**. Rejected during review: a time bar cannot warn about quota exhaustion — you could sit at 95% used with 4 hours "remaining" and the bar would still look healthy, which defeats the purpose of the tool. Time is still shown, as text.

**Weekly reset ([GitHub issue #6](https://github.com/mlengmark/O-view/issues/6), [ADR-0011](adr/0011-weekly-reset-derivation.md)):** derived from `sd` drops the same way the 5-hour reset is derived from `fh` drops, and now presented the same way. The window is **7 days** — measured from two resets 7 d 0 h 14 m apart, with the 72-hour alternative disproved by the same file — so **one** observed reset is enough to predict the next, as one `fh` drop is. Observed resets are still **persisted**, in [`%LOCALAPPDATA%\O-view\weekly-resets.json`](adr/0011-weekly-reset-derivation.md): a reset that scrolls out of the source file is gone for good, and that state must not sit in the rollup store, which wipes itself on corruption.

The row has four states, and naming them is the point of the feature:

| State | Line |
|---|---|
| Derived from a precise observation | `Resets in 6d 3h · Tue 06:28` |
| Derived from a gap-bracketed observation | `Resets in 6d 3h · ~Tue 06:28` — hover gives the bracket width |
| Plan data flowing, no reset seen yet | `Waiting for first reset…` — hover explains what is being watched for |
| No plan data at all | hidden; the no-data banner above already explains it |

> **The `~` is load-bearing.** Weekly resets land in the small hours, and Claude Desktop stops sampling when it is closed — so a reset is typically only bracketed to within ~10 hours. Printing a bare `Tue 06:28` from that would be a fabricated minute (rule 6). The tilde and the hover state the precision; the time itself is the bracket's **upper** bound, so the panel never promises fresh quota before it arrives. Predictions anchor on the most precise observation on record, not the most recent, so the figure sharpens by itself as better observations accrue.

> **What the blank row cost.** Until now this line was simply hidden whenever the reset was underived — which, because gap-crossing drops were being discarded, was *always*. A permanently missing line is indistinguishable from a bug, and that is what issue #6 reported.

Discovery runs on the ordinary poll loop: every refresh re-scans the retained series and folds any drops into the log, so there is no separate schedule and re-recording a known reset is a no-op. The API almost certainly reports the reset directly, but reading it needs the encrypted OAuth token (deferred — ADR-0007).

Percentages come from the OAuth provider. On JSONL fallback they are estimates and must be labelled as such.

### Statistics tiles

2 × 2 grid:

| Tile | Definition |
|---|---|
| Tokens today (UTC) | Summed from rollup store, UTC day |
| Est. value today (UTC) | Tokens priced at public API rates |
| Tokens · 31 days | Rollup store, trailing 31 days |
| Est. value · 31 days | As above, priced |

> **The two "today" tiles name their day** ([issue #210](https://github.com/mlengmark/O-view/issues/210)). The window is a UTC day, and for anyone not on UTC that is not what "today" means — at UTC+2 the tile spends the first two hours of every local day showing yesterday's usage, and west of UTC it runs the other way for longer. The number is right; the unqualified label was not, and a correct number under a wrong name is the same failure as a wrong one. `(UTC)` alone still leaves the reader working out which hours are covered, so the boundary is stated in their own clock too — hovered on Windows, standing text on Linux, which carries no hover.
>
> This is a stopgap. Bucketing by **local** day is the fix and comes with its own removal of `(UTC)`.

> **"Spend" means estimated API-equivalent value, not money charged.** Within plan limits the marginal cost is £0. These tiles answer "what would this have cost on the API" and **must be labelled `Est.`** — presenting them as actual spend would be a fabricated number.

Any tile whose window exceeds recorded history shows coverage: `3 of 31 days recorded`.

#### Per-model breakdown ([GitHub issue #37](https://github.com/mlengmark/O-view/issues/37))

Every tile is clickable and flips in place between its total and a **stacked bar split by model**. Clicking again flips back, as often as you like.

**The hover card is styled, not system chrome.** A default WPF tooltip is a pale rectangle with a hard border and nothing to do with the rest of the app, so the template is replaced outright: the panel's rounded 6px card, its palette, its type. It sits on its own `TooltipBg` / `TooltipBorder` step — brighter than the panel in light, lighter than the tile in dark — so it reads as floating *above* the tile rather than as part of it, with a soft drop shadow for the same reason.

> **Every tooltip in the app is this card.** The template and the timing live in [`HoverCard.xaml`](../src/O-view.Tray/Popup/HoverCard.xaml) / [`HoverCard.cs`](../src/O-view.Tray/Popup/HoverCard.cs), merged into both the panel and the tile. They used to be declared *inside* `StatTile`, which meant only the tiles had them — the usage graph's bars kept raw WPF chrome, so the panel showed two unrelated tooltip designs depending on where the pointer landed. A bare `ToolTip = "some string"` anywhere in this codebase is that bug returning; build the card instead.
>
> Two shapes cover everything: **`Figure`** leads with the number and names it in a muted line beneath — the reading order for *what am I pointing at, and how much* — with an optional colour swatch where the card floats clear of a coloured mark and would otherwise lose its tie to it. **`Text`** carries a sentence, for caveats and explanations. A third shape should be resisted.
>
> `--popup-samples` renders the graph's cards standalone as `graph-hover-cards-<theme>.png`, for the same reason `--tile-samples` does: a `ToolTip` cannot be given a parent, so it appears in no screenshot of the panel, and hover is the only way to reach one in the running app.

The card carries two lines only: a **colour swatch beside the figure**, and the **raw model id** beneath (or `N more models` for the folded bucket, so "Other" is never an unexplained slice). There is deliberately no friendly-name heading — it restated the line below it and made a card holding two facts look heavy.

The swatch is the load-bearing part and stays: a card floating clear of the bar otherwise loses its connection to the exact colour being pointed at.

> The id line is **always** rendered. It used to be suppressed when it matched the friendly name — which is precisely the case for an **unrecognised** model, where `ModelDisplayName` returns the raw id unchanged. Dropping the heading without this would have left exactly those cards showing a number and nothing identifying it.

A `ToolTip` cannot be given a parent — it throws — so it appears in no screenshot of the panel and cannot be laid out inside one. `--tile-samples` therefore renders the cards standalone to `hover-cards-<theme>.png`, with the palette written into each card's own resources so its `DynamicResource` lookups resolve with no tree above them to walk.

**Where the figures live.** On hover, nowhere else. The bar is a thin, unlabelled mark and the legend carries model **names only**; pointing at a segment — or at its legend entry — reveals that model and its exact figure. Two earlier attempts are worth not repeating: figures printed beside the names in the legend wrapped it to two lines, and figures printed *inside* the segments made the bar look cluttered and forced it 6px taller to hold the text.

**Hover timing.** Applied to **each** element that carries a tooltip, in `StatTile.ApplyHoverTiming`. Setting these once on the control and relying on property inheritance looks right and silently does not work — the bar segments resolved to framework values instead. That is invisible in a screenshot, which is why `--tile-samples` writes a `hover-timing.txt` reporting what actually resolves on a segment.

| Property | Value | Measured unset | Why |
|---|---|---|---|
| `InitialShowDelay` | **400 ms** | 1000 ms | The Windows convention; 1 s reads as sluggish for a deliberate point-at. The delay still exists to require *lingering* — the pointer crosses this bar on its way elsewhere, and much shorter makes tooltips flash during ordinary movement. |
| `BetweenShowDelay` | **3000 ms** | 100 ms | The one that matters most here. Within this window of the last tooltip, the next shows with **no** delay, so sliding along the segments reads as one continuous reveal instead of re-waiting per colour. |
| `ShowDuration` | **20 s** | `int.MaxValue` | Set for **determinism, not extension** — note the measured baseline was effectively unlimited, so this is a deliberate cap, though the documented WPF default is 5 s. 20 s far outlasts reading a two-field tooltip, so WCAG 1.4.13 (Content on Hover or Focus) holds in practice while the behaviour is the same on a machine whose default really is 5 s. |

**Not tunable:** WPF dismisses a tooltip the instant the pointer leaves the element, and that grace period is not exposed. For this bar that is the wanted behaviour anyway — leaving the bar should dismiss — and the case that actually needed smoothing was moving *between* segments, which `BetweenShowDelay` covers.

- **No I/O on click.** The rollup store's ledger is already at `(UTC date × model)` grain, so the split was being discarded at the last step, not missing. It arrives on the `PanelStatistics` the panel opened with, and the flip is a re-render.
- **The tile never changes size.** Both views live in one `Grid` and the inactive one is `Hidden`, not `Collapsed` — and the breakdown is built during `Populate`, not on first click, because a `Hidden` element only reserves the space its *content* needs. Built lazily, the tile visibly grew the first time it was opened.
- **The label is dropped in the breakdown view**, as the issue allows, to buy room; the total stays, so the tile still answers its own question.
- **Affordance:** a faint chart glyph, brightening on hover, plus a hover/pressed fill. A tile with nothing to break down is disabled and shows no glyph — an affordance that leads nowhere is worse than none.

**Colour follows the model, never its rank.** One slot order is computed for the whole panel — [`ModelBreakdown.ColourOrder`](../src/O-view.Core/Models/ModelBreakdown.cs), ranked by tokens over the 31-day window because that is the superset every tile draws from — and all four tiles colour by it. A model therefore wears the same colour everywhere, including on the value tiles where the *ordering* differs.

> This was got wrong first time round. Colour was assigned by a segment's position within its own tile, so Opus 5 came out **blue** on the "today" tile (where it was the only model, hence first) and **orange** on the 31-day tile (where Opus 4.8 outranked it) — the same model in two colours on one panel, which makes the tiles unreadable against each other. The fold into "Other" is driven by the same shared order, so a model that is "Other" on one tile is "Other" on all of them.

**Colour is validated, not chosen.** The palette is the data-viz categorical order, re-checked against the *tile* surfaces with the six-check validator on the **all-pairs** list (segment order follows the data, so any two colours can end up adjacent):

| | Light `#EFEFEF` | Dark `#2B2B2B` |
|---|---|---|
| CVD separation (target ≥ 8) | **9.2** | **9.4** |
| Normal-vision floor (≥ 15) | **17.3** | **16.5** |

Two consequences are load-bearing and should not be "tidied up" later:

- **Never add a fourth chromatic slot.** The next hue in the validated order is yellow, which fails the all-pairs floors beside the orange slot. That is *why* a fourth model folds into "Other" rather than getting its own colour.
- **The cap tiers at three.** Up to three models each keep a colour; four or more collapse to **two named models plus a neutral "Other"**. No grey exists that is both inside the dark lightness band and separable from the third (aqua) slot for deuteranopes — the sweep bottoms out at ΔE 3.0 — because grey has no hue to separate on and sits at the same lightness. Dropping to two named models is the price of showing an honest remainder at all.

On the light surface two series sit below 3:1 against the tile. That is a documented **relief** case, not a dismissable warning, which is why the legend is mandatory rather than decorative and every segment carries a tooltip with exact figures. Legend text wears text tokens; the swatch beside it carries identity, never the text colour.

> **Known limitation:** per-model figures are reachable by hover only, so they are not available to keyboard or touch. This is a deliberate trade for a clean bar, taken twice over after printed figures were tried in the legend and then in the segments. What is *not* behind the pointer: the legend names every model, the tile's total shows in both views, and every rule-6 caveat — coverage, unpriced — is rendered text. Only the per-model split needs a pointer.

**Rule 6 in the breakdown.** An unpriced model has an *unknown* value, not a zero, so it cannot be placed on the value chart — it is excluded and the tile says `excl. N unpriced`, with the tooltip naming them. A folded "Other" that contains any unpriced model reports its own value as unknown rather than quietly summing the priced remainder. An unrecognised model id renders **as-is** — inferring a friendly name from the pattern would be a fabricated fact.

`<synthetic>` appears in neither split. This paragraph used to say it rendered as `Local` — "real tokens, genuinely zero value, so it appears in the token split" — which was never reachable: `TranscriptReader` has dropped those records since the commit that introduced the rollup store, so not one has ever been ingested (verified against a real store). Measured on real transcripts they also carry all-zero usage in every token field, so even if they were stored the breakdown would drop them as zero-measure segments. See [issue #57](https://github.com/mlengmark/O-view/issues/57).

`--tile-samples <dir>` renders the tiles across 1, 2, 3, 5-model and unpriced cases, in both themes and both views. Handled before the single-instance mutex, like `--diagnose`.

### Usage graph

Daily token totals across the trailing 31 days, from the rollup store ([ADR-0006](adr/0006-local-rollup-store.md)). Enhanced per [issues #4 and #5](https://github.com/mlengmark/O-view/issues/4):

- **One bar per day**, height by absolute daily tokens.
- **Colour: light → dark blue by intensity within each week** (issue #5) — each week is its own gradient scale, so the busiest day of a week is darkest. The absolute weekly token limit is unknown, so this is *relative* intensity, not a fraction of a limit.
- **Dotted vertical gridlines at the weekly limit reset**, giving weekly context, with the reset time on hover. They are drawn **last, so they sit above the bars**, and wear the panel's note colour (`WarnText` — the same amber as the coverage and caveat lines). Both were corrections: a Canvas paints in child order, so lines added first were overdrawn by every bar they crossed and survived only in the gaps — a reset line vanishing exactly where usage is heaviest is missing the moment it exists to mark. The amber follows from the same reasoning: this is an annotation *over* the data, not another axis decoration, and one pixel of muted grey on blue was not carrying that.

> **These follow the plan's own week, not the calendar's.** They were originally drawn at Monday boundaries for a stated reason: the plan's true weekly reset was not derivable, so Mon–Sun was an honest visual reference rather than a claim about the plan. [ADR-0011](adr/0011-weekly-reset-derivation.md) removed that constraint, so the gridlines — and the colour bands, which use the same boundaries so the two can never disagree — now sit on the real reset. Past boundaries are **derived** by stepping the cadence back from the predicted next reset, not looked up, because the log only holds resets O-view was running for while the graph covers 31 days.
>
> A reset happens at a time of day, not at midnight, so a gridline sits at its **true fractional position inside the day column** it falls in. Snapping it to the nearest column edge would assert a midnight boundary the data does not have. Until a reset has been observed the line falls back to Monday midnight and says so on hover — the honest blank, not a silent substitution. The two are visibly distinguishable, which is the point: a Monday line lands exactly on a column edge, a plan line lands inside one.
>
> A day that *contains* a boundary belongs to neither band — its tokens split across two weeks and the rollups are daily, so the split is not recoverable. It is shaded with whichever band holds the majority of the day. That approximation affects colour only, never a stated figure.

`--popup-samples <dir>` renders the whole panel offscreen in both themes, across the states that change what it asserts about the weekly window (derived boundaries vs. awaiting the first reset). Handled before the single-instance mutex, like `--menu-samples`. It exists because the alternative — opening the real panel — needs the mutex and a free display, and **silently fails when anything is running fullscreen**, which is exactly when the gridlines needed checking.
- **Vertical date labels under every column** (issue #4), small but legible, and **centred on their own bar** ([issue #31](https://github.com/mlengmark/O-view/issues/31)). Bar and label are placed from a single column-centre anchor rather than each carrying its own offset. A rotated `TextBlock` renders one line height to the *left* of its `Canvas.Left` (a `RenderTransform` doesn't move the layout box), so the anchor is derived from `RotateTransform.TransformBounds` — measured, not a constant, since line height moves with font, DPI and OS text scaling. The former constant left labels 2.3 px adrift, a fifth of a column at 31 days.
- **Hover card** per bar — exact token count, with the full date beneath — and one per gridline giving the reset time. Both use the panel's shared card (see *Per-model breakdown* above), not system chrome.

Days before install are **blank columns** (no bar) with their date still labelled — with the date axis, an empty column reads as "no data" on its own, so the earlier "before O-view install" caption was **removed to save space** (issue #4). They are never rendered as zero-height bars, which would misread as idle days.

### Off-plan usage (was: Credits)

**Implemented 2026-07-21** after [credit-usage-divergence.md](findings/credit-usage-divergence.md) established that plan percentages can be accurate and misleading at once.

When the session window shows substantial activity but a flat plan meter, the panel:

The panel carries the signal in **two independent registers**:

**Live (current session window)** — the real-time divergence detector:

1. **An amber banner** above the quota bars — the bars are correct but no longer the whole story, so the correction must appear before them, not after.
2. **Relabels the value tile** from `Est. value today (UTC)` to `Est. spend today (UTC)` with an `incl. off-plan usage` note. The "not money charged" framing is only true for plan usage; off-plan work bills at API rates, so the label flips with the reality.

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

> **This entire section is Windows-only.** On Linux the menu belongs to the SNI host, not to
> O-view: the host renders it in its own style, at a position O-view cannot know, and the
> custom flyout below has no equivalent.
>
> The Linux menu carries **Run at startup** and **Exit**. The startup row is a checkable
> `NativeMenuItem` and follows the same rule as its Windows counterpart — the tick shows the
> state as it stands *after* the write, never the state requested, because writing the
> autostart file can fail (rule 6). It is re-read from disk whenever the panel opens, since
> the file is the source of truth and the desktop's own settings may have changed it.
>
> The notification, diagnostics and update rows are **not** on the Linux menu. Each has a
> command-line equivalent instead (`--diagnose`, `--probe`, `--startup-status`), which is
> also the only route that works on GNOME without an AppIndicator extension, where there is
> no menu at all. The README's matrix records this as a ⚠️ rather than a tick.

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

## 4. Motion

> **Windows-only, and for a stated reason.** The motion below is *fitted to Windows' own
> Quick Settings flyout* — it exists to make O-view's surfaces indistinguishable from the
> shell's. Copying that curve onto a centred window on a Linux desktop would imitate nothing;
> it would just be an animation borrowed from another platform's shell. The Linux panel opens
> without it.

Both docked surfaces — the detail panel and the tray menu — **rise out of the docked edge**: a clip reveals the surface from that edge while the content slides the last 20 px into place, over **230 ms** opening and 150 ms closing. They previously appeared and vanished in a single frame, which reads as a glitch rather than as a window opening.

**The motion is copied from the platform, not invented.** Windows' own Quick Settings flyout was recorded frame by frame on this machine: its top edge rises while the bottom stays pinned to the taskbar, travelling the full height in ~230 ms with almost no fade. It is a *geometric reveal* — not a zoom, not a dissolve.

The easing is **fitted** to that trace by searching cubic-Bezier control points against measured progress, rather than picked by name:

| Curve | RMSE | @7 ms | @57 ms | @115 ms |
|---|---|---|---|---|
| **`KeySpline(0.02,0.16 0.20,0.96)`** | **0.0033** | 0.14 | 0.60 | 0.84 |
| quartic ease-out | 0.0633 | 0.14 | 0.69 | 0.91 |
| `cubic-bezier(0,0,0,1)` | 0.0638 | 0.23 | 0.69 | 0.89 |
| cubic ease-out | 0.1603 | 0.05 | 0.37 | 0.68 |
| *measured Windows* | — | *0.15* | *0.60* | *0.84* |

The third row was the first attempt here, on the assumption that Windows uses `cubic-bezier(0,0,0,1)`. Measured against the real thing it is twenty times further out and visibly too abrupt — it passes 70% before the platform reaches 15%, which reads as a pop rather than a rise. **No named easing was close enough to trust.**

A **scale** transform was tried first and rejected: smooth, but it reads as a zoom rather than as a panel rising out of the taskbar. A plain slide was rejected too — the content fills the window, so the part sliding in from beyond the edge is clipped by the window bounds; the clip is what makes the slide work.

The reveal direction comes from [`PopupPositioner`](../src/O-view.Tray/Popup/PopupPositioner.cs), the only thing that knows which edge was docked to, so a top-docked taskbar reveals downward.

**Verified by re-measuring O-view the same way**: the reveal reaches 0.60 at 33–41 ms and 0.83 at ~87 ms against the reference's 57 ms and 115 ms. That gap sits inside the measurement's own precision — ~8 ms of sampling granularity plus screen-capture latency, and a start instant known only to within one frame — so it is a match to the limit of what this method can resolve, not an exact one.

Closing defers `Hide()` to the end of the animation, since hiding first would make the animation invisible. A close in flight is cancelled if the surface is re-opened, so clicking the tray icon again mid-fade brings it straight back rather than letting it finish disappearing. Menu **actions** run after the animation completes, which preserves the rule that a balloon or modal never appears behind a still-visible topmost flyout.

`--popup-pin` and `--menu-pin` skip the animation entirely: a verification still needs the finished state, not a frame from the middle of a fade.

### The disclosure fold

"Why so large?" **folds**, on the same curve as the entrance: the explanation's height animates
from zero and its content is clipped to it, so the text slides out from behind the fold edge
while the panel grows *upward* out of the docked corner. It used to appear whole — the body,
the `SizeToContent` window's height and the re-dock all changing in one frame, three
discontinuities in the one surface on screen that had been tuned against a trace of the
platform.

Durations are **scaled** from the entrance's, not chosen: **191 ms** open and **125 ms** close,
the traced 230 : 150 at 0.83. The fold covers a twelfth of the entrance's distance, and giving
60 px the time 700 px needs reads as hesitation. There is deliberately **no fade** — the same
reasoning that made the entrance a geometric reveal rather than a dissolve — and the chevron
rotates 180° over the same curve instead of swapping glyph.

**Two failures here are invisible to any offscreen render**, and both were found by
[`--fold-check`](../src/O-view.Tray/Diagnostics/SampleRenderer.cs), which drives the real panel
and grabs the pixels the compositor actually presented:

- **The window must never be laid out at the far end of the fold.** Applying the open state and
  laying it out — the obvious way to measure where the fold ends — resizes a window docked by
  its top-left, and that HWND is presented before the re-dock catches up: one frame of
  full-height panel 72 px down, its bottom inside the taskbar. The end point is measured off
  the explanation alone instead, and the body is pinned at the fold's starting height before
  any layout runs, so every size the window takes is one the fold docks it for.
- **Dock against the content's `DesiredSize`, never the window's `ActualHeight`.** A
  `SizeToContent` window's own size trails the layout pass that produced it, and the fold
  changes that height sixty times a second; docking against the trailing number left a collapse
  settled 13 px above its edge.

Measured over the fold, per rendered frame: the body steps 0 → 22.6 → 35.4 → 47.3 → … → 72.0 px
opening and mirrors it closing, with the docked edge holding to within half a pixel throughout.

### Clicking the icon toggles

The tray icon **opens and closes** the surface, as every taskbar flyout does. Left-click toggles the panel, right-click toggles the menu.

This needs more than an `IsVisible` check. The click itself dismisses the surface by taking focus from it, so by the time the click handler runs the surface is already closing and looks closed — which is why it used to reopen immediately and could only ever open. Each surface therefore records **when it last began closing from lost focus**, and a click arriving within 400 ms is treated as the second half of the toggle. The window is wide enough to cover the deactivate-then-click ordering plus the close transition, and short enough that a deliberate click a moment later still opens.

---

## 5. Dialogs

O-view asks questions on **its own window**, not a `MessageBox`. The system box was the one surface that gave the app away — raw Win32 chrome, a stock blue "i" glyph, Yes/No buttons, and no room for a mark, so nothing on it said which application was asking.

[`DialogWindow`](../src/O-view.Tray/Popup/DialogWindow.xaml) carries the same mark, palette and type as the menu and the panel: rounded card, brand mark beside the title, message, an optional muted detail line.

- **The two answers have different weight** — an accent-filled primary and an outlined secondary — rather than two identical buttons the user must read both of to tell apart.
- **The primary button names the action** (`Update now`, `Open page`) instead of answering "Yes" to a question that has to be re-read to be sure of.
- Esc cancels; Enter takes the primary action.
- It takes the foreground through the same [`ForegroundWindow`](../src/O-view.Tray/Tray/ForegroundWindow.cs) helper the tray menu uses. A dialog that opens *behind* another window is worse than a flyout doing so: it is modal, so the app appears to have frozen.

**Accent colour is measured, not picked.** The mark's own `#D9603A` gives white label text only 3.69:1, short of the 4.5 a 12 px label needs, so the fill is stepped darker. Each step has to clear white text *and* stay distinguishable from whichever panel it sits on:

| | White label | Light panel | Dark panel |
|---|---|---|---|
| `AccentBg` `#BE4E29` | 4.87:1 | 4.63:1 | 3.34:1 |
| `AccentHover` `#B84A27` | 5.19:1 | 4.93:1 | 3.14:1 |

Do not darken the hover further for emphasis: the next step (`#B44726`) lands on exactly 3.00:1 against the dark panel, and past it the button stops reading as a button in dark mode.

`--dialog-samples <dir>` renders both variants in both themes. A modal cannot be screenshotted by the run that would take the screenshot, so it has to be rendered.

---

## 6. Setup

The installer is branded too ([O-view.iss](../installer/O-view.iss)): a terracotta wizard panel carrying the mark and wordmark, the mark in the header of every other page, the O-view icon on `Setup.exe` itself, and welcome/finish copy in the app's own voice rather than Inno's defaults.

Images are generated from `brand/o-view-mark.svg` into `installer/brand/` as BMP (what Inno takes), at 1× and 2× so a high-DPI display gets a crisp image rather than an upscale.

**`DisableWelcomePage=no` is deliberate.** The large image only appears on the Welcome and Finished pages, and modern wizard style suppresses Welcome by default — so without it the branding would first appear only *after* the install had finished. One extra click on a first-time install buys a setup that identifies itself; the auto-update path runs `/SILENT` and never sees that page.

### The surface that cannot be branded

**Balloon notifications are drawn by Windows.** They come from `Shell_NotifyIcon` via `NotifyIcon.ShowBalloonTip` (ADR-0005) and take the system's own chrome — O-view supplies only a title, a body and an icon. There is no styling hook. Branding them would mean replacing them with custom toast windows, which is a different feature, not a coat of paint.

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
