# UI specification

**Agreed:** 2026-07-20 · Supersedes the informal layout sketch in the README.

Three surfaces: the always-visible **tray icon**, the **popup panel** shown when it is left-clicked, and the **tray menu** flyout shown when it is right-clicked, plus the app's **dialogs** and its **setup wizard**, which carry the same brand.

---

## 0. What binds on which platform

**Added 2026-08-03, when the Linux head landed.** This document was written for a Windows-only
app and most of it still describes the Windows head specifically. Two kinds of statement are
mixed together throughout, and they are not equally binding:

| | Binds where | Lives in |
|---|---|---|
| **The shared contract**: *what* a surface says and when. Icon geometry and colour bands, the tooltip's wording and its degraded forms, panel copy, the reset-line phrasing, the countdown format, the panel's labels, which states are "unknown" rather than zero | **Both heads.** A user should not be told a different thing on a different OS | `O-view.Presentation` for the wording and view models (`PanelText`, `PanelSections` and the rest), `O-view.App` for the geometry and palette (`TrayIconGeometry`, `PanelPalette`, `PanelMetrics`). Changing the wording means changing it in one place, and exact-string tests pin it |
| **Windows presentation**: *where* a surface appears and how it moves. Docked placement, `PopupPositioner`, work-area insets, the slide in and out, foreground/`AttachThreadInput` handling, `Shell_NotifyIcon` behaviour, Segoe glyph avoidance | **The Windows head only** | `O-view.Tray` |

Read every section below as Windows presentation unless it is describing content. Where Linux
**cannot** meet a section, it is noted inline rather than left to be inferred, the real divergences are the tooltip (§1), the panel's placement (§2) and the menu (§3); motion (§4) is shared except for the menu. The panel's layout is not one of them: both heads draw the two blocks described in §2.

The Linux head's own constraints are [ADR-0013](adr/0013-linux-ui-toolkit.md) and the [tray
spike](findings/linux-tray-spike.md). Nothing in this document has been verified on Linux
hardware; see the README's support matrix.

---

## 1. Tray icon

Per [ADR-0003](adr/0003-windows-tray-constraints.md) and the [legibility spike](findings/tray-icon-rendering.md), revised 2026-07-21 to a **ring gauge** ([GitHub issue #1](https://github.com/mlengmark/O-view/issues/1)), then unified with the exe icon as the **brand mark** (ring + centre pupil) on 2026-07-22:

- **A circular arc, proportional to session %, plus a filled centre pupil, the brand
  "eye", and no digits.** Ring-only sidesteps the spike's finding (which rejected ring
  *plus* digits, where they competed for space) by removing the digits, the exact
  number lives in the tooltip instead. The pupil is brand, not a second signal: it
  carries no number and takes the arc's colour, so it coexists with the ring.
- Arc colour, the shared `UsageLevels` bands ([issue #2](https://github.com/mlengmark/O-view/issues/2)):
  **green < 50% · amber 50–69% · red ≥ 70%**. Same classifier drives the popup bars.
- No data / estimate: faint empty ring, never a fabricated fill
- Tooltip (≤127 chars): `5h: 47% · resets 16:32 · 7d: 61% · resets Tue 06:28`. The weekly reset is the instant Claude Code reports, shown exactly and never with a `~` ([ADR-0014](adr/0014-weekly-reset-is-a-reported-constant.md)); the session reset is the instant Claude Code reports while one is ahead of now, shown exactly with no `~`, and otherwise the time derived from drops, which carries a `~` when the observation behind it was bracketed by a gap in sampling. Either is absent while unknown.

> **Linux: GNOME shows none of this.** The text above is still what O-view publishes, but as
> the tray item's `Title`, Avalonia's tray backend fills `ToolTip` with an empty structure and
> offers no way to change that, and the AppIndicators extension GNOME needs before it has a
> tray at all has `ToolTip` commented out of its own D-Bus interface, so filling it would still
> show nothing there. The exact figures are in the detail panel instead; see the README's
> [Platform support](../README.md#platform-support) for what KDE Plasma does with an empty
> `ToolTip`, which is not known.

The icon tracks the **session** window, since that is the limit users hit first.
A threshold notification fires once when session usage first reaches the critical
band (default 70%, matching red; user-adjustable).

> **This section is the shared contract**, and the two heads render it from the same
> `TrayIconGeometry`, a divergence here would mean the same usage looked like a different
> number on a different OS. What differs is only the rasteriser (GDI+ on Windows, Skia on
> Linux) and the **sizes asked for**: Windows requests 16/20/24 px, while SNI hosts commonly
> want 22–24 px and 48 px on HiDPI. That last part matters, the original legibility
> measurements cover 16/20/24 and **do not carry to 48 px untested**, which is what
> `o-view --samples <dir>` exists to make judgeable from images rather than from prose.

---

## 2. Popup panel

Roughly 400 px wide. **Docks to the work-area corner adjacent to the taskbar**,
the same placement model as the system flyouts (volume, network, calendar), so it
always opens in the same dedicated place regardless of exact click position. The
cursor selects only the monitor; the taskbar edge is derived from the work-area
inset (handles all four dock positions and auto-hide).

> **Linux: the docking above does not apply; the content below does.** SNI gives an
> application no way to learn where its own icon was drawn (there is no
> `Shell_NotifyIconGetRect` equivalent), so the panel cannot anchor to the icon. It opens
> instead at the **work-area corner beside the panel bar**, computed by the same shared
> `WorkAreaPlacement` rule the Windows flyout docks with, and on an X11 or XWayland session
> it remembers a dragged position exactly as the Windows panel does (*Moving the panel*,
> below). A native Wayland compositor refuses to place or report a window, so there the panel
> keeps the corner and has no drag handle or reset row ([ADR-0013](adr/0013-linux-ui-toolkit.md),
> [ADR-0017](adr/0017-remembered-panel-position.md)). Everything from *Header* down, the copy,
> the bands, the figures and the rule that absent days draw no bar, is the shared contract and
> holds identically.

**The window never grows past the display's work area.** Whatever the content's own height,
the flyout stops at the space the work area leaves; the part that does not fit is not
clipped, it is reached by scrolling, in a viewport built only once the content is actually
taller than the cap. A panel with room shows no bar and has no viewport at all, laid out
exactly as it always was.

The scroll bar itself is thin, 6 px, and painted in O-view's own colours, from the shared
palette, in both the light and dark themes, so it reads as part of this panel rather than a
borrowed system control.

### Moving the panel

**The header is a drag handle.** A press that travels past a small threshold (4 dip) becomes
a drag; a press that does not is a click, and opens or dismisses the panel exactly as it did
before dragging existed, the threshold is what keeps the two apart.

**Letting go snaps and clamps, in that order.** The place where the pointer released is
nudged flush to whichever edge or corner it landed within 16 px of
([`PanelPlacementMemory.SnapThresholdDip`](../src/O-view.App/Rendering/PanelPlacementMemory.cs)),
then clamped back inside the work area as it stands right now, so letting go anywhere, even
partway off a screen edge, still leaves the whole panel on screen. The result is remembered
per display.

**The next open restores it, re-clamped and capped.** Each display keeps its own place;
opening the panel on one re-runs the same clamp against that display's work area as it stands
at that moment, so a shrunk screen or a moved taskbar still shows the whole panel, and the
result is then capped by the same height rule as a corner-placed panel (*The window never
grows past the display's work area*, above), a restored place scrolls exactly as a freshly
opened one does. A display that was never dragged to, or a panel that no longer fits it at
all, opens at the work-area corner instead, same as it always has.

**Alt and an arrow key moves the panel 10 dip; Alt+Shift and an arrow key moves it 1 dip**,
exactly that, and that place is what is remembered, however close to an edge the panel lands.
The keyboard is clamped like a release but never snapped like one: on a screen the panel all
but fills there is less room above and below it than the 16 px snap distance, so a step that
snapped would be pulled straight back to the edge it started against and the keys would move
nothing at all. Snapping is for the pointer, which the user aimed by hand and can miss an edge
narrowly with; a keystroke has no aim to miss by.

The clamp is still how the keyboard reaches flush: a press that would cross an edge leaves the
panel against it, and pressing again from there changes nothing, not the panel, and not what
is stored. A screen reader reaching the header announces "Move panel", naming what the drag and
the nudge both do.

> **Linux: asking to place the window is a request, not a command.** A window manager is free
> to ignore it, and a native Wayland compositor always will, a Wayland client can neither set
> nor read its own window position, so on that session the panel keeps the work-area corner,
> no drag handle appears on the header, and the menu's reset row is absent. Every other
> session reaches an X server, the session's own under Xorg, or XWayland under a Wayland
> compositor, so a position can be requested and read back, and the app's own log says
> whether the window manager actually honoured it (`"honoured"` or `"OVERRIDDEN by the window
> manager"`). This head has exactly one Linux backend, `Avalonia.X11`; there is no separate
> native-Wayland behaviour beyond the one difference stated above.

### Header

| Position | Content | Source |
|---|---|---|
| Top left | **O-view** title | static |
| Top left, beneath | `As of HH:mm`, when the reading was taken | runtime |
| Top right | Display name, email, tier badge | `~/.claude.json` → `oauthAccount` |

**The line states when the figures were captured, never when the panel was drawn**
(`PanelText.Freshness`): `As of now` · `As of HH:mm` · `Local estimate · as of HH:mm` ·
`Reading time unknown` · `No data`. It read `Updated 11:34 · live` until issue #192, a
repaint clock beside a claim of liveness, when O-view only ever holds the last poll's
result and the source samples on its own schedule. `As of now` is claimed only within the
capture's own clock minute; after that the reading is a past log and carries its own time.

**Estimates stay labelled** (CLAUDE.md rule 6, ADR-0002): the capture time joins `Local
estimate`, it does not replace it. Live and Stale share one wording deliberately, the
capture time states the age more precisely than the tier split did.

> ⚠️ **Tier comes from `organizationType`** (e.g. `claude_pro`). On the dev account, `seatTier` and `userRateLimitTier` are both **empty strings**, the obvious-looking fields are wrong and produce a blank badge.

Account data is read from local config, so the header needs no token and no network.

### Usage bars

Every bar shows **percentage of quota consumed**, consistent metric, with time-to-reset as adjacent plain text.

| Row | Bar | Text |
|---|---|---|
| **Current session** | % of 5-hour rolling limit used | `Resets in 2h 14m · 16:32` |
| **Weekly** | % of 7-day limit used | `Resets in 6d 3h · Tue 06:28`, same shape, plus a weekday (see below) |
| **Weekly · Fable** | % of that model's own 7-day limit used, one row per model limit the account has | The weekly row's line, exact and never `~` |

**A model's own limit ([ADR-0019](adr/0019-model-limit-and-reported-spend.md)).** An account with a weekly limit on one model gets that limit as its own bar under Weekly, labelled `Weekly · ` and the model's name as Claude Code cached it. It is built from the cached block alone, never from the snapshot, so it shows whichever source supplied the two meters above; Claude Desktop's history carries no model limits, and a bar that followed the snapshot would vanish whenever Desktop's reading won. A row has no boost chip.

- **A reading that no longer describes its window is unknown, and the row stays.** A passed reset or an aged zero reads `unknown` with no fill, by the rule the two meters' cached figures use. A passed reset also loses its reset line, since nothing steps it forward; an aged zero's reset is still ahead and keeps its line. Dropping the row instead would say the limit went away, when all that is known is that the reading is old (rule 6).
- **An account with no such row sees no bar, and nothing else changes.**
- **Windows** places the rows under the weekly reset line at the meters' own sizes, with the gap above each following the panel's density; a long model name is trimmed rather than drawn over the figure. **Linux** wraps a long name onto a second line and the panel grows to fit.

**Boost chip ([GitHub issue #254](https://github.com/mlengmark/O-view/issues/254)).** When Claude Code has cached a promo notice for a meter, that meter's heading carries a chip between its label and its percentage: `50% Boosted · until 31 Aug · ends in 2w 4d 14h`. It reuses the tier badge's `BadgeBg`/`BadgeText` pair, nothing is added to `PanelPalette`, so it reads as a factual label about the account rather than a warning; amber stays reserved for things needing attention.

| Part | Source | Missing when |
|---|---|---|
| `50%` | extracted from the message, `PromoText.Percent` | the sentence has no unambiguous increase figure |
| `Boosted` | O-view's own word | never |
| `until 31 Aug` | extracted, `PromoText.EndDate` | no date could be read without guessing |
| `ends in 2w 4d 14h` | derived from that date | as above |

Three rules, and each exists because of a specific way this row can mislead:

- **The figure leads and the word follows it immediately.** The same row shows utilisation, `51% used`, a *level*, a hundred pixels to the right, where the chip's figure is a *delta*. `50% Boosted` reads as one phrase; a bare `50%` adrift on that row would read as the same kind of number as the one beside it.
- **The heading is three columns, not an overlay**, label `Auto`, chip `*`, percentage `Auto`. The first implementation put the chip and the percentage in one cell and drew the chip straight over `100% used`. The chip is the only elastic thing on the row, so it is the one that gets trimmed, and **the row must never wrap**: a wrap shifts the bar beneath it and changes the panel's height.
- **The month is abbreviated**, because the budget is tight and it is measured, not estimated. Note the figure on the right reads `51% used`, not `51%`, a budget worked out against the shorter string is about 30px too generous, which is exactly how the overlap above got shipped into a first render.

The chip is hidden whenever the promo's last day has passed by the machine's own calendar, when the notice names a bar this gauge is not, and, for a notice carrying no readable date, once the flag cache has gone stale. **Hidden is the ordinary state**: most of the time no promo is running.

Hovering gives the card: Claude's sentence unedited, then `Ends Mon 31 Aug · reported by Claude Code, read 09:24`. O-view cannot verify that this account is boosted, the payload is a feature-flag cache, evaluated server-side, so the panel **relays rather than asserts**, and the provenance line is what makes that honest rather than a hedge (rule 6). Nothing on the panel rescales a percentage because a boost exists: the meters report utilisation against whatever limit is in force, and the boost widened that denominator upstream.

*Design note:* the original spec had the session bar showing **remaining time** and the weekly bar showing **remaining tokens**. Rejected during review: a time bar cannot warn about quota exhaustion, you could sit at 95% used with 4 hours "remaining" and the bar would still look healthy, which defeats the purpose of the tool. Time is still shown, as text.

**Weekly reset ([GitHub issue #6](https://github.com/mlengmark/O-view/issues/6), [ADR-0014](adr/0014-weekly-reset-is-a-reported-constant.md)):** **reported, never derived.** Claude Code writes the exact instant to `~/.claude.json` → `cachedUsageUtilization.utilization.seven_day.resets_at`; O-view persists it as an anchor in `%LOCALAPPDATA%\O-view\weekly-reset.json` and projects it forward by whole weeks, including from a stored instant already in the past. The reset is a fixed weekly grid tied to the account, same weekday, same time, every week, so one report is correct forever. A user-entered time fills the gap where nothing has ever been reported; a discovered anchor outranks it, and says so rather than replacing it silently.

The row has three states:

| State | Line |
|---|---|
| Known | `Resets in 6d 3h · Tue 06:28`, exact, no `~`; `· you entered this` when the source is the entry |
| Not known | `Weekly reset time not known`, hover names the one step that ends it; clicking copies `/usage` |
| No plan data at all | hidden; the no-data banner above already explains it |

> **The `~` is gone from this row, and its absence is the claim.** Both remaining sources report the instant rather than inferring it, so there is no bracket to state and the uncertainty field that carried one was removed (issue #248). The session row keeps its tilde only while its reset is derived, because that window rolls from first use; a reset Claude Code has reported is exact and carries none.

> **"Not known" is a setup step, not a wait.** This line used to read `Waiting for first reset…`, from when the reset was derived by watching the weekly percentage fall. ADR-0014 deleted that derivation, so nothing was watching and the promised fill-in could never arrive, a row that names a wait nobody is serving is the blank row's defect wearing an explanation. It now states the fact and carries the action: **run `/usage` in Claude Code**, which is the only thing that refreshes the block Claude Code caches, or enter the time from **Menu → Weekly reset…** where there is no `claude` to run. Which sentence appears is an observation (`UsageEngine.ClaudeCodeFound`), never an inference from the account file, a machine can carry a `~/.claude.json` and no executable.

O-view runs that command itself on its own cadence (`ClaudeCliRefresher`, issue #234), so for most users this row fills in without being read. The copy exists for the machines where it has not.

Percentages come from Claude Desktop's plan history or Claude Code's cached figures, whichever carries more meters most recently. On JSONL fallback they are estimates and must be labelled as such.

### The panel: two flat blocks

**Added 2026-11-08 for Windows; the Linux head draws it too.** The wording and the figures come from the same Presentation view models on both heads, so what a figure says is the shared contract, and where it sits is the same on both. How it is drawn is each head's, and the paragraphs below are written in Windows' terms (WPF elements, the fades and folds); *The Linux head* at the end of this section says where Linux differs. The Linux layout is built and tested. Nobody has yet seen it on a Linux desktop.

Below the header, two blocks sit directly on the panel's surface, the full 368 wide (the 400 panel less 16 of padding each side), **with no fill**: **Your plan** and **On this machine**. Each has 10 of vertical padding and no side padding, and one soft hairline lies between them. Nothing on the panel is grey except the controls named below. The measurements are stated once in `PanelMetrics` and the colours are `PanelPalette` keys, so a number here is a figure to read there, not a second copy to keep in step.

- **A block's title** is 11 px, semibold, uppercase and letter-spaced 0.04 em, in `TextSecondary`. A WPF `TextBlock` has no letter-spacing, so a small element draws it, and the same element draws the tables' column headings.
- **Lines are soft.** Row lines and the hairline between the blocks are 60 percent of `PanelBorder`; the vertical lines in the tables are 35 percent. The strength is set on the brush and not on the element, because a fold fades each cell to full opacity and would otherwise lift the line with it.

**Your plan.** The meters keep their shape and take the block's full width: the name left (12 px semibold), `n% used` right (12 px `TextSecondary`), the 8 px bar beneath with a 4 px radius, then the reset line in 10.5 px `TextMuted`. A model's own weekly limit is one more meter. The block states no reset note of its own; each meter states its own. **Off-plan spend is the block's last row, and only while there is off-plan usage**: `Off-plan usage` with the `credit-billed` chip (or `from Claude` for Claude's own amount), a caption (`est. credit spend · last 31 days`, or `reported by Claude`), the figure at 15 px semibold on the right and, under it, its coverage caveat (`10 of 31 days recorded`). It has a soft line above it and no bar. With nothing to show there is no row and no line.

**The banner** sits above both blocks, unchanged in wording, and has a **hide control**: a 16 by 16 `×` at its top right, labelled `Hide this notice`. Hidden, it stays hidden across opens and restarts, and it comes back only for a new off-plan spell, when a poll has read the account as on-plan in between. The panel closes up over it with a 140 fade and a 280 fold, and the window stays docked while it does. The banner's own wording, its three billing wordings and its link are *Off-plan usage*, below, and did not change.

**On this machine, the totals face.** In order:

1. **The chart,** 96 tall plus a 16 axis band, the full width, in no box. Bars sit 1.2 in from each slot edge with a 1.5 radius, drawn up from a baseline hairline; older bars fade a little, 0.55 at the left rising to 1 at today, and today's bar is `AccentBg`. A hairline stands at every Monday with its date written across it just to the right (`13 Oct`, `20 Oct`), and `today` is right-aligned at the end in `AccentInk`. There are no rotated day labels. Days before install draw **no bar** at all, never a zero-height one (CLAUDE.md rule 6). The week lines are the calendar's Mondays and carry no reset time: they replace the dotted lines at the plan's weekly reset that both charts used to draw. The hover card per bar is the existing one.
2. **A soft line,** then **the table.** Output and Overall differ in their rows, never in their grammar. Column headings are 10 px uppercase, letter-spaced, `TextMuted` and centred; row labels are 12 px `TextSecondary`; figures are 12 px semibold with tabular digits, **centred** in their column; a soft line runs between every row and a 35 percent line stands at the left of every data column.
   - **Output** (the default for a fresh install) has the columns `label | Today | 31 days` and the rows `Output tokens` and `Est. value of output`.
   - **Overall** has `Kind | Today | Share | 31 days | Share`: a row each for output, input, cache write and cache read with its chip from the token-kind ramp, a bold `Total`, then `Est. value`, each figure in a **pill** (`TileBg` fill, a 1 px soft border, radius 10) centred across the pair of columns it belongs to. Shares are 11 px `TextSecondary`.
3. **The footer line:** the one caveat, 10.5 px `TextMuted`, on the left (for example `Claude Code and Cowork transcripts only; chat and cloud sessions not counted.`, worded from the sources the scan found and never claiming one it did not), and on the right the **Output | Overall switch**, a segmented control on `TileBg` whose lit fill is one element behind the labels that slides between them. The partial-history and unpriced caveats sit under the table, flush right, so they end beneath the 31-day figures they qualify.

**What Output and Overall mean** is decided once, in Presentation (`PanelSections.LocalTotals`). **Output** counts output tokens only and prices only them, so `Est. value of output` is the output tokens at the output rate. **Overall** counts everything billed and prices everything, which is what `Est. value` has always meant, cached prompt re-reads included. The chart follows: output per day in Output, all billed per day in Overall. The value row reads `Est. spend on output` in Output and `Est. spend` in Overall where F19 does, and an unpriced figure reads unknown in either, never zero. No digit counts up: bars and arcs animate, and every figure is the real one from the first frame.

**On this machine, the model face.** A flip tag at the block's top right, flush with the right edge, reads `by model` with the tile's two-arrow mark on a `TileBg` pill; on the model face it reads `totals`. The block's subtitle reads `by model · output tokens` or `by model · all tokens`. The face is:

1. **Two doughnuts** side by side, 104 across, ring radius 38, stroke 13, with a 1.5 gap between sections, drawn clockwise from the top in the table's order. The centre carries the window's total at 13 px semibold with `output` (or `tokens` in Overall) beneath it; under each ring, `TODAY` and `LAST 31 DAYS`.
2. **The model table:** `Model | Tokens | Est. value | Tokens | Est. value`, four 62-wide columns under the group headings `TODAY` and `31 DAYS`. **Every model in the store is a row; nothing is folded into "other".** A colour chip and the name sit on the left, tokens in 11.5 px semibold and the value in 11 px `TextSecondary`, both centred. The value of an unpriced model reads `unpriced` in `TextMuted`, never blank and never zero, and a priced model that did nothing today reads a known `$0.00`. A long model name trims in its column and its row's tooltip carries the full id, since rule 6 does not allow a fact to be cut with no way to read it.
3. The footer line and the switch stay, and the switch applies here too (Overall: all tokens by model).

**Model colours on this face.** The three largest models take the three validated chromatic slots; every model after them takes a grey, in size order, from `SeriesTail1` to `SeriesTail4`, stepping towards the surface so the later a model comes the quieter it draws, and a model past the fourth grey takes the last again. A fourth hue is not added without the six-check validation the palette's comments describe. The later greys sit below 3:1 against the panel, which is the relief case again: every row is named in the table beside the ring and a section's hover card carries the model's name, so no grey is ever the only thing that tells two sections apart. A model has one colour on both rings and in the table, because both are cut from the same rows.

**Hover cards.** A doughnut section's card is the `Figure` shape with the model's chip and name above the figure, then `output · 37% of 31 days` and the estimated value (`$3.10 est. value`). The 31-day ring's card opens to the left of the ring and every card's words are capped at 190 wide, so no card leaves the panel from either ring.

**The view is remembered.** Output or Overall, totals or models, and the hidden banner are saved with the other settings, restored on every open and after a restart, and a fresh install opens on Output and the totals. This replaces the earlier rule that a tile's flipped view was transient and every open started on the summary. The words and the rule are in Presentation (`PanelViewChoice`); where it is stored is in [architecture.md](architecture.md).

**Nothing is clipped.** Every sample state, in both themes and in the compact and short renders, and every hover card, is asked whether anything crosses the panel's edge, is cut to its slot by the layout, or has text that trims. `--popup-samples` writes the answer to `popup-edge-check.txt` and exits 1 on a miss, which fails `tools/check.ps1` and CI's `visual` job. The Linux head asks the same in its tests (`PanelClippingTests`). The sample states include the widest figures the formatters produce (`longest-figures`) and the model face with a 47-character id (`longest-figures-models`). The one exemption is a model name in the model table's name column, and only while its full id is in a tooltip.

**Height.** Measured from the dark golden renders: the Output totals face is 588 tall, the Overall face 689, the model face 726, and a Pro account off-plan with the banner showing is 704 on Output, 805 on Overall and 842 on the model face, the tallest the panel gets. The cap and the scrolling are the ones above: on the 712 work area the off-plan Overall state, and the off-plan model face, compact first and then scroll, and each has a short render (`offplan-on-short`, `offplan-on-models-short`) that the clipping check also reads. Compact density applies `PanelDensity`'s steps to the chart's height, the blocks' gaps and the table rows, and leaves the columns and the type alone: `typical-compact` is the Overall face and measures 600, against 689 at normal density.

**The Linux head.** It reads the same view models in the same order, with the same measurements from `PanelMetrics` and the same colours from `PanelPalette`, and differs as follows:

- **Motion on the same figures, moved differently.** Section 4's figures and curves run on Linux too, through `LinuxMotion`. The panel's window is never moved (a native Wayland compositor refuses a client that moves its own window), so only the content slides, from the edge the panel is docked to; the window is see-through where the desktop composites, and where it is not the panel appears at once instead of showing an empty box. A bottom-docked panel keeps its lower edge while its height moves. The desktop's own animation setting decides whether anything moves.
- **Width.** The panel's 1 px border is drawn inside the 400, so the content column is 366 wide at the natural density and beside the scroll bar, and 378 at the compact density, as measured when the chart was fitted to it. The Windows figures above are the Windows head's.
- **The chart has no reset lines.** It has the Monday hairlines and dates, `today`, and no bar for a day before install. A bar's hover card is the shared figure card, the figure large with its day muted beneath, and a model's name in the table carries its full id on the shared text card. A doughnut section's hover card is the shared card with the model's chip and name, and the 31-day ring's opens to the left. Every card ends inside the panel.
- **Keyboard and pointer.** Each segment of the switch can take keyboard focus. The banner's `×` is a tab stop and answers a pointer press, Enter and Space, with a focus ring only when the keyboard put the focus there.
- **The short renders.** The panel is photographed short on the model face only, `offplan-on-models-short`, which is the tallest state that shows it; there is no `offplan-on-short` on Linux. `typical-compact` is drawn on both heads.
- **The view is remembered the same way**, through the shell, so a Linux restart opens on the Output or Overall, totals or models, and hidden banner the reader left.

### What the local figures mean

The local figures are the Output and Overall tables' columns, the model face's rows and the chart's bars (*The panel: two flat blocks*, above). Four tiles used to carry them, with a flip to a per-model bar behind each, and the tokens-used bars and the dotted-line graph sat beneath; neither head draws any of that now, and the sections that described it are gone from this document. What the figures are, and the rules that keep them honest, hold on both heads.

| Figure | Definition |
|---|---|
| Tokens today | Summed from rollup store, the reader's local day |
| Est. value today | Tokens priced at public API rates |
| Tokens · 31 days | Rollup store, trailing 31 local days |
| Est. value · 31 days | As above, priced |

> **Every day-shaped figure is a local day** ([issue #211](https://github.com/mlengmark/O-view/issues/211)), the two columns of the table, the model face, each bar in the 31-day chart, and the `N of 31 days recorded` caveat. Users mean local days, and one of those straddles two UTC ones, so the bucket is computed from each request's timestamp at query time rather than read from the `utc_date` it was stored under. Nothing about ingestion changes: storing a local date would bake one machine's offset into the row.
> They were UTC days until v0.6.x, and for one release they said so, `Tokens today (UTC)` ([issue #210](https://github.com/mlengmark/O-view/issues/210)), the honest stopgap while this was built. The qualifier is off again, because a label that no longer matches its figure is the same rule-6 failure in the opposite direction.
>
> **A local day is 23 or 25 hours twice a year.** Boundaries come from the timezone (`LocalDays`), never from 24-hour steps, and the chart's Monday lines are placed in the same frame as its columns, or they drift against the bars they mark from the transition onwards.
>
> **The plan meters are not days and keep their own clocks.** The five-hour window rolls from first use; the weekly reset is a fixed instant Claude reports (ADR-0014). Neither learns about local dates.

> **"Spend" means estimated API-equivalent value, not money charged.** Within plan limits the marginal cost is £0. These figures answer "what would this have cost on the API" and **must be labelled `Est.`**, presenting them as actual spend would be a fabricated number.

Any figure whose window exceeds recorded history shows coverage: `3 of 31 days recorded`.

#### Hover cards

**The hover card is styled, not system chrome (Windows).** A default WPF tooltip is a pale rectangle with a hard border and nothing to do with the rest of the app, so the template is replaced outright: the panel's rounded 6px card, its palette, its type. It sits on its own `TooltipBg` / `TooltipBorder` step, brighter than the panel in light and lighter than it in dark, so it reads as floating *above* the panel rather than as part of it, with a soft drop shadow for the same reason.

> **Every tooltip in the Windows panel is this card.** The template and the timing live in [`HoverCard.xaml`](../src/O-view.Tray/Popup/HoverCard.xaml) / [`HoverCard.cs`](../src/O-view.Tray/Popup/HoverCard.cs), merged into the panel. They were once declared *inside* a stat tile, which meant only the tiles had them, the usage graph's bars kept raw WPF chrome, so the panel showed two unrelated tooltip designs depending on where the pointer landed. A bare `ToolTip = "some string"` anywhere in this codebase is that bug returning; build the card instead.
>
> Three shapes cover everything: **`Figure`** leads with the number and names it in a muted line beneath, the reading order for *what am I pointing at, and how much*, with an optional colour swatch where the card floats clear of a coloured mark and would otherwise lose its tie to it. **`ModelFigure`** is that card for a doughnut section, with the model's chip and name above the figure. **`Text`** carries a sentence, for caveats and explanations, and carries a long model id whole where the table trims it. A fourth shape should be resisted.
>
> `--popup-samples` renders the graph's cards standalone as `graph-hover-cards-<theme>.png`, for the same reason `--tile-samples` renders the model-name card standalone: a `ToolTip` cannot be given a parent, so it appears in no screenshot of the panel, and hover is the only way to reach one in the running app.

**On Linux** every card is built in `PanelHoverCard`: a doughnut section's with the model's chip and name above the figure, a bar's as the figure over its caption, and a model's full id as a wrapping line of text.

**Hover timing.** Applied to **each** element that carries a tooltip, by `HoverCard.ApplyTiming`. Setting these once on the control and relying on property inheritance looks right and silently does not work, the bar segments resolved to framework values instead. That is invisible in a screenshot, which is why `--tile-samples` writes a `hover-timing.txt` reporting what actually resolves on a model name cell.

| Property | Value | Measured unset | Why |
|---|---|---|---|
| `InitialShowDelay` | **400 ms** | 1000 ms | The Windows convention; 1 s reads as sluggish for a deliberate point-at. The delay still exists to require *lingering*, the pointer crosses this bar on its way elsewhere, and much shorter makes tooltips flash during ordinary movement. |
| `BetweenShowDelay` | **3000 ms** | 100 ms | The one that matters most here. Within this window of the last tooltip, the next shows with **no** delay, so sliding along the segments reads as one continuous reveal instead of re-waiting per colour. |
| `ShowDuration` | **20 s** | `int.MaxValue` | Set for **determinism, not extension**, note the measured baseline was effectively unlimited, so this is a deliberate cap, though the documented WPF default is 5 s. 20 s far outlasts reading a two-field tooltip, so WCAG 1.4.13 (Content on Hover or Focus) holds in practice while the behaviour is the same on a machine whose default really is 5 s. |

**Not tunable:** WPF dismisses a tooltip the instant the pointer leaves the element, and that grace period is not exposed. Leaving a bar or a section should dismiss its card anyway, and the case that actually needed smoothing was moving *between* them, which `BetweenShowDelay` covers.

`<synthetic>` records appear in no figure: `TranscriptReader` drops them before the rollup store ever sees one, and measured on real transcripts they carry all-zero usage ([issue #57](https://github.com/mlengmark/O-view/issues/57)).

`--tile-samples <dir>` renders the Windows model face across one, two, three and four models and an unpriced and a long-named case, in both measures and both themes. It kept its name when the tiles went. Handled before the single-instance mutex, like `--diagnose`.

### Usage graph

**Both heads draw the chart described in *The panel: two flat blocks*, above:** a bar per day for the trailing 31 days, a hairline and date at every Monday, `today` at the right-hand end, no bar at all for a day before install, and hover text per bar: a hover card on both heads. The daily totals come from the rollup store ([ADR-0006](adr/0006-local-rollup-store.md)).

The earlier chart is gone from both: it drew dotted lines at the plan's weekly reset, or at Monday midnight as a labelled fallback until a reset had been seen, with a hover card each, rotated date labels under every column, and a light-to-dark blue banding within each week. The plan's weekly reset is now stated only by the Weekly meter's reset line, which is exact and never `~`; the chart claims nothing about it. Days before install are blank slots, never zero-height bars, which would misread as idle days (CLAUDE.md rule 6).

### Off-plan usage (was: Credits)

**Implemented 2026-07-21** after [credit-usage-divergence.md](findings/credit-usage-divergence.md) established that plan percentages can be accurate and misleading at once.

The panel carries the signal in **two independent registers**:

**Live (current session window)**, the real-time divergence detector:

1. **An amber banner** above both blocks, with a hide control that lasts until the next off-plan spell, the bars are correct but no longer the whole story, so the correction must appear before them, not after.
2. **Relabels the value figure** from value to spend: the value row of the totals table reads `Est. spend on output` in Output and `Est. spend` in Overall, and the model table's today column reads `Est. spend` while its 31-day column keeps `Est. value`. The "not money charged" framing is only true for plan usage; off-plan work bills at API rates, so the label flips with the reality.

A notification fires once per onset (edge-triggered, re-armed when it clears). When the plan limit is reached (≥99%) the wording changes: continued work is not drawing from the window by definition rather than by inference.

**What it may claim about billing is decided by the account, not by the meter** ([issue #259](https://github.com/mlengmark/O-view/issues/259)). The spec used to say an exhausted window meant work "bills beyond the plan by definition", and the banner said so, *"Plan limit reached, usage is billing beyond your plan"*, to every reader whose 5-hour window ran out, including the population that has extra usage switched off and cannot be billed anything. That is not a hedge that could be tightened; it is a false statement about the reader's money, and it read as an emergency.

The premise was wrong twice over: the meter says nothing about billing, and the answer was already on the machine. Claude Code caches it in `cachedUsageUtilization.utilization.extra_usage.is_enabled` ([findings](findings/cached-usage-utilization.md#extra_usage-whether-the-account-can-bill-beyond-the-plan)). So the banner and the balloon carry three wordings, one per state, and `Unknown` is not a rounding of the other two:

| Extra usage | Banner heading | What the detail adds |
|---|---|---|
| **On** | `Plan limit reached, further work bills as extra usage` | can bill beyond the plan, + provenance |
| **Off** | `Plan limit reached, extra usage is switched off` | should not bill beyond the plan, + provenance |
| **Unknown** | `Plan limit reached` | the setting could not be read; no claim either way |

The diverging heading (`This session's usage is not drawing from your plan`) is the same in all three: a flat meter may not be about billing at all, a Cowork session in a cloud container leaves no local record either.

Two rules on the wording:

- **Relayed, never asserted in O-view's own voice.** It is another application's cache, refreshed only when `/usage` runs, so it can be days old while looking current. Every claim carries `(reported by Claude Code, read 09:07)`, gaining a date once the reading is not from the reader's own today. Same treatment as the boost chip's hover card, for the same reason (rule 6).
- **The Est. figure is withheld from the balloon where nothing can be billed.** It is the API-rate *value* of the work, and the panel labels it as such in the table and a hover; a three-line notification cannot, and `Est. $92.75 so far this window` beside `extra usage is switched off` contradicts itself. The panel still shows it in full.

The banner also carries a link to Claude's own usage settings, the address Claude Code itself uses for that screen, not a plausible-looking guess. Every wording above ends by sending the reader there for the figure O-view will not state, so the instruction is the thing you click.

**The model limit has three sample states** (`model-limit`, `model-limit-spend` and `model-limit-spend-unknown`), the only ones with a model bar: the bar with its section hidden because nothing was reported, the bar with a reported figure beside $92.75 of recorded credit estimate that it must ignore, and the bar with extra usage on and no `spend`.

**The redesign's sample states.** `typical-overall`, `typical-models`, `typical-models-overall`, `offplan-on-overall`, `offplan-on-models`, `offplan-on-hidden`, `longest-figures` and `longest-figures-models` carry the Output/Overall, model-face and hidden-banner views, and seven models where a model face needs them; `typical-overall-folding` is Windows-only, because Linux has no fold, and `offplan-on-short` is Windows-only too. Both heads draw `typical-compact` and `offplan-on-models-short`, and every other state.

**The three states have offscreen renders** (`--popup-samples` → `panel-offplan-{unknown,off,on}-{light,dark}.png`). They had none before, which is how a banner asserting a charge at accounts that could not be charged survived: no fixture had an exhausted meter, so nobody ever looked at it. Same shape as issues #58 and #170.

**Standing**, the `Off-plan usage` section ([GitHub issue #3](https://github.com/mlengmark/O-view/issues/3)). **It is shown only when there is off-plan usage to show, on every account.** With nothing to show there is no row, no `$0.00`, no gap and no soft line above it, on Windows and on Linux. Without a model limit, usage with no published price still shows the section, with an unknown figure, because unknown is not zero. Which figure it carries depends on the account:

- **Without a model limit** the row's title is `Off-plan usage` and its caption reads `est. credit spend · last 31 days` (the day count is the chart's window), and it shows **estimated credit spend over 31 days**, the API-rate value of usage on credit-billed models ([`CreditBilledModels`](../src/O-view.Core/Models/CreditBilledModels.cs), currently Fable, the one case verified against billing).
- **Why not a lookback classifier:** there is no per-request billing-tier field (`service_tier` is uniformly `"standard"`, even on requests known to have billed to credits), and the plan meter's short retention makes retroactive divergence impossible. So the 31-day figure is a per-model estimate, not a per-request fact, hence the explicit "models billed as extra usage (Fable)" caption.
- Carries the same coverage caveat as the other 31-day figures (`N of 31 days recorded`) and, since [issue #32](https://github.com/mlengmark/O-view/issues/32), a **two-clause** caption: published API rates, balance unreadable, check billing for exact. The mechanics that used to be spelled out here (deduplicated locally; an upper bound, as bundles discount up to 30%) were cut for space, they described *how* the estimate is built, whereas the two clauses kept are the ones that stop the figure being read as money charged, which is what rule 6 actually requires.

- **With a model limit** (any model-scoped weekly row in the cached block, one whose reset has passed included) usage of that model draws from its own limit, so pricing it would call plan usage off-plan. The section instead shows **what Claude reports as spent past the plan**, from the block's `spend` ([ADR-0019](adr/0019-model-limit-and-reported-spend.md)): title `Off-plan usage` with no day count, badge `from Claude`, caption `reported by Claude`, no `Est.` and no coverage note (the view model's own `Reported spend` label is drawn by no head), because `spend` states no period and any span written beside it would be O-view's invention. The hint says so and carries the `(reported by Claude Code, read 10:47)` stamp.
  - **The amount decides, whatever `spend.enabled` says.** A non-zero amount shows; zero hides the section however heavily the model was used. USD reads `$12.34`; any other currency shows its own code, never `$`, with the decimal places it came with (`12.30 EUR`, `1234 JPY`).
  - **With no readable amount the setting decides.** `extra_usage.is_enabled` on (that field alone, because an unreadable `spend` keeps no `enabled` of its own) shows `unknown`, since money may have moved and nobody reported how much; off or not stated hides the section.
  - **The two figures are never added or shown together.** The banner, the value figure's relabel and the notification still read the estimate, because they answer a different question.

The two registers are independent by design: the section shows even when the current session is on-plan, and the live banner shows even before any credit spend has accrued.

**The tray icon is unchanged**, session % remains the headline per the product decision. The off-plan signal lives in the panel and notifications.

Exact credit *balances* remain deferred (no local or API source found). What the section shows is **spend**, estimated or as Claude reports it, not remaining balance.

`Limit Reset Credits` from the original spec could not be mapped to a documented concept and needs clarification.

---

## 3. Tray menu

The right-click surface. Rebuilt as a **docked flyout window** ([GitHub issue #33](https://github.com/mlengmark/O-view/issues/33)), replacing a WPF `ContextMenu` placed at `PlacementMode.MousePoint`.

> **The flyout below is Windows-only.** On Linux the menu belongs to the SNI host, not to
> O-view: the host renders it in its own style, at a position O-view cannot know, and the
> custom flyout below has no equivalent.
>
> The Linux menu carries these rows, in this order:
>
> | Row | Kind | Notes |
> |---|---|---|
> | Show usage | action | Opens the panel ([issue #315](https://github.com/mlengmark/O-view/issues/315)) |
> | Reset panel position | action | Forgets the dragged place for this display and puts an open panel back at the work-area corner. Absent on a native Wayland session, there is nothing to reset there |
> | Run at startup | toggle | Re-read from the autostart `.desktop` file whenever the panel opens |
> | Notify at | submenu | 70%, 80%, 90% and Off as radio rows, the setting in effect ticked. A threshold edited by hand into settings.json gets its own ticked row. The tick shows what the shell returned, never what was asked |
> | Weekly reset | action | Shows `derived` or the entered day and time, and opens a small dialog with the Windows dialog's words and rules. An empty time clears the entry |
> | Copy diagnostics | action | Puts the support bundle on the clipboard and says so in a notification, as on Windows |
> | Exit O-view | action | |
>
> **Show usage is first because on Ubuntu the panel otherwise has no gesture.** The
> AppIndicators extension GNOME ships opens this menu on a single left click and sends
> `Activate` only on a double click, so on O-view's main Linux target the panel was reachable
> only by a gesture nothing on screen suggests. A row reaches it on every SNI host whatever
> that host does with clicks, which is why this is the fix rather than asking each desktop for
> different click handling. It runs the icon click's own handler, so the row and the click open
> the same panel by the same route and cannot drift apart.
>
> **Reset panel position follows it directly**, as someone opening this menu for that reason
> has a panel sitting somewhere they cannot get it back from, the row belongs beside the one
> that opens the panel, not at the end of a list. It forgets the display's saved place first,
> so the corner placement that follows is the ordinary fallback rather than a special case.
>
> **The startup row** follows the same rule as its Windows counterpart, the tick shows the
> state as it stands *after* the write, never the state requested, because writing the
> autostart file can fail (rule 6). It is re-read from disk whenever the panel opens, since
> the file is the source of truth and the desktop's own settings may have changed it.
>
> A fifth row, **Resume usage refresh**, is built but kept hidden until the refresh has
> stopped itself; it sits between Weekly reset and Copy diagnostics, so a person sees seven
> rows until there is something to undo, six on a native Wayland session, where the reset
> row is absent.
>
> The update row is **not** on the Linux menu. The command-line flags (`--diagnose`, `--probe`,
> `--startup-status`) remain the only route that works on GNOME without an AppIndicator
> extension, where there is no menu at all. The update row is absent for a reason that is not
> about room: **Linux updates are notify-only**
> (ADR-0009 as amended), O-view says a newer version exists and the package manager installs
> it, so no row here downloads or runs anything, and adding one would be a change to that
> decision rather than to this menu.

**Why the cursor placement had to go.** The menu opened wherever the pointer was when the tray icon was hit, and the tray icon sits *inside* the taskbar, so that is reliably the one place a menu cannot fully fit. It clipped into the taskbar and off the screen edge, leaving items unclickable, and every item added made it worse.

**Placement.** The flyout ignores the cursor for positioning and docks to the work-area corner adjacent to the taskbar, through the same [`PopupPositioner`](../src/O-view.Tray/Popup/PopupPositioner.cs) the detail panel uses, one placement model for both surfaces, matching the system flyouts (volume, network, calendar). The work area **excludes the taskbar by definition**, so clearing it is structural, not a margin that happens to be large enough. The cursor still selects which monitor.

**Scaling.** A `StackPanel` under `SizeToContent="Height"`, with `ActualHeight` re-measured on every open. Adding a row needs no change to the placement code, and a taller menu still docks above the taskbar.

**Design.** Roughly 272 px wide, on the panel's own palette ([`PanelTheme`](../src/O-view.Tray/Popup/PanelTheme.cs), shared with the detail panel so the two surfaces cannot drift apart), rounded 10 px card, 34 px rows with a 6 px rounded hover fill, and a compact header carrying the brand mark and version. The header exists because the flyout appears in a fixed corner rather than under the cursor: detached placement without a label reads as a stray window.

| Row | Kind | Notes |
|---|---|---|
| Reset panel position | action | First row: there is no "Show usage" row above it to follow, since the tray icon's own click already opens the panel on this head. Forgets the dragged place for this display and puts an open panel back at the work-area corner |
| Run at startup | toggle | Re-read from `HKCU\...\Run` on every open |
| Update automatically | toggle | Installed copies only; a portable exe cannot replace itself ([ADR-0009](adr/0009-auto-update.md)) |
| Notify at *N*% usage | toggle, with a 70/80/90 selector | *N* from settings |
| Weekly reset… | action | Opens the dialog for entering or clearing the weekly reset time ([issue #186](https://github.com/mlengmark/O-view/issues/186)) |
| Resume usage refresh | action | Only while the background `/usage` refresh has stopped itself after detecting a charge |
| Copy diagnostics | action | Support bundle to clipboard |
| Check for updates… | action | Directly above Exit ([issue #18](https://github.com/mlengmark/O-view/issues/18)) |
| Exit O-view | action | |

**Toggles leave the flyout open** so the tick confirms the change; **actions close it first**, so a balloon or a modal never appears behind a topmost window.

A tick shows the state as it **actually stands after the write**, not the state requested, a registry write can fail, and a tick claiming otherwise would be a fabricated fact about the machine (rule 6). Checkmarks are drawn as vector `Path` geometry rather than Segoe Fluent Icons glyphs, which are not present on every supported build and would render as tofu.

Rows are `Button`s, so Tab/Space/Enter work; Up/Down is wired explicitly to match the menu behaviour it replaces.

**Dismissal is Esc or an outside click, and taking the foreground is not optional.** A tray-resident app owns no activated window, so the flyout must foreground its own HWND on open or it never receives the deactivation that closes it ([issue #11](https://github.com/mlengmark/O-view/issues/11)). `SetForegroundWindow` alone is **not sufficient**: Windows grants it only to a process that already holds the foreground or received the last input event, and a tray app frequently holds neither. Losing that race is not cosmetic, the flyout is shown but never activated, so it never fires `Deactivated` and **stays on screen with no way to dismiss it**, which is strictly worse than the clipping this replaced. That was reproduced on a live desktop, then fixed with an `AttachThreadInput` fallback: share an input queue with the current foreground thread for the duration of the call, and the grant succeeds.

**Verification hooks.** `--menu-samples <dir>` renders the flyout in both themes for visual review; `--show-menu` (with optional `--menu-pin`, `--menu-theme`) opens the real thing on the real desktop, which is the only way to check the docked placement against a live taskbar. Like `--diagnose`, `--menu-samples` is handled **before** the single-instance mutex, so it runs on a machine already running O-view.

Measured on a 2560×1440 display, taskbar bottom-docked at `y=1392`, **before the Reset panel position row was added**: the flyout landed at `2276,1159–2548,1380`, 272×221, inside the work area, 12 px clear of the taskbar and 12 px from the screen edge. One row taller now; not re-measured.

---

## 4. Motion

> **Both heads move the panel; only Windows moves the menu.** The curves below were fitted to
> Windows' own Quick Settings flyout, and the Linux panel runs the same figures and curves so
> the two read as one product. The Linux menu is drawn by the desktop, so O-view cannot
> animate it at all, and the sections below that describe the menu, its tick and its threshold
> list are Windows only. Linux runs the panel's slide, the block's turn, the Output | Overall
> fold, the bars and arcs arriving and the banner's fold through `LinuxMotion`, and follows the
> desktop's animation setting, read through the XDG portal. Linux moves only the content, never
> its window.

Every figure in this section is a base figure in
[`PanelMotion`](../src/O-view.App/Rendering/PanelMotion.cs) multiplied by its `Pace`, which is
**2**. The figures quoted here are what runs on screen, so retuning the whole app is one edit to
that constant and this section's figures follow it. `PanelMotion` holds no toolkit type, so a
test on Linux can pin the rules between the figures; the Windows head turns them into WPF
animations through `WpfMotion`.

### Opening and closing a surface

Both docked surfaces, the detail panel and the tray menu, **slide out of the docked edge as one
rigid piece**: border, corners and content move together, at full opacity, with no clip of
their own. Opening takes **460 ms** on the decelerate curve. Closing is the reverse, **300 ms**
on the accelerate curve, and the window is hidden when it ends. Docked at the bottom the card
rises; docked at the top it drops. The window's bounds are the edge the card emerges from, so it
appears out of a line 12 px above the taskbar, not out of the taskbar itself. The panel and the
menu use the same figures.

**What changed, and why.** Until 2026-11-06 the surfaces were a wipe: a clip rectangle grew from
the docked edge over 230 ms while the content moved the last 20 px behind it and a 90 ms fade
took the edge off the first instant, and closing ran the same in reverse over 150 ms. That was
three motions at three rates, so the card was uncovered by a moving line instead of moving, and
its border and rounded top corners existed only on the last frame. The menu, a third of the
panel's height, took the same time and so crossed its edge at a third of the speed. Maximilian
compared the wipe with a plain slide in a side-by-side demo on 2026-11-06, chose the slide, and
asked for every duration to be twice the first proposal. The wipe's clip, its 20 px content
shift and its fade are gone.

**The motion is copied from the platform, not invented.** Windows' own Quick Settings flyout was recorded frame by frame on this machine: its top edge rises while the bottom stays pinned to the taskbar, travelling the full height in ~230 ms with almost no fade. The slide's durations are deliberately slower than that recording; its curves are still the platform's.

The easing is **fitted** to that trace by searching cubic-Bezier control points against measured progress, rather than picked by name:

| Curve | RMSE | @7 ms | @57 ms | @115 ms |
|---|---|---|---|---|
| **`KeySpline(0.02,0.16 0.20,0.96)`** | **0.0033** | 0.14 | 0.60 | 0.84 |
| quartic ease-out | 0.0633 | 0.14 | 0.69 | 0.91 |
| `cubic-bezier(0,0,0,1)` | 0.0638 | 0.23 | 0.69 | 0.89 |
| cubic ease-out | 0.1603 | 0.05 | 0.37 | 0.68 |
| *measured Windows* | none | *0.15* | *0.60* | *0.84* |

The third row was the first attempt here, on the assumption that Windows uses `cubic-bezier(0,0,0,1)`. Measured against the real thing it is twenty times further out and visibly too abrupt, it passes 70% before the platform reaches 15%, which reads as a pop rather than a rise. **No named easing was close enough to trust.**

A **scale** transform was tried first and rejected: smooth, but it reads as a zoom rather than as a panel rising out of the taskbar. A plain slide was rejected next, because the content fills the window, so the part sliding in from beyond the edge is cut off by the window's bounds. That cut-off is exactly what the demo showed, and on 2026-11-06 it was chosen over the wipe that had replaced it.

The slide direction comes from [`PopupPositioner`](../src/O-view.Tray/Popup/PopupPositioner.cs), the only thing that knows which edge was docked to, so a top-docked taskbar drops the card down.

**The curve was verified against the trace when the wipe was built**, by re-measuring O-view the same way: the reveal reached 0.60 at 33–41 ms and 0.83 at ~87 ms against the reference's 57 ms and 115 ms. That gap sat inside the measurement's own precision, ~8 ms of sampling granularity plus screen-capture latency, and a start instant known only to within one frame, so it was a match to the limit of what the method could resolve. The curves are unchanged, but the durations are now twice the reference's by design, so that comparison was not repeated for the slide.

Closing defers `Hide()` to the end of the slide, since hiding first would make the slide invisible, and the menu and the panel each carry a guard timer 150 ms past the close that settles the surface if the animation never reports it finished. A close in flight is cancelled if the surface is shown again by anything other than the icon. A click on the icon during the close is the second half of the toggle and leaves the surface closed (see *Clicking the icon toggles* below). Menu **actions** run after the slide completes, 300 ms after the choice instead of the 150 ms they once waited, which preserves the rule that a balloon or modal never appears behind a still-visible topmost flyout.

The animated offset is snapped to whole device pixels. Both windows format text for pixel grids (`TextFormattingMode="Display"`), and a fractional translate makes that text shimmer, most visibly in the long slow end of the decelerate curve.

`--popup-pin` and `--menu-pin` skip the animation entirely: a verification still needs the finished state, not a frame from the middle of a slide.

### When Windows animations are off

`WpfMotion.AnimationsOn` reads `SystemParameters.ClientAreaAnimation` (Settings > Accessibility > Visual effects > Animation effects) each time a transition starts, so changing the setting takes effect without a restart. With it off, **every transition in this section is skipped**: each state is applied at once through the same instant paths the verification renders use, hover fills switch instantly, and the panel opens on a settled frame with the plan bars, the chart's bars and the doughnuts already at their full size. `PanelMotion.Runs` is the one rule that says so. `--still-check` proves it by forcing the setting off inside the check, so it runs wherever it is started. The live checks that need something mid-flight report NOT RUN on a machine whose setting really is off, instead of failing for the machine's setting: `--motion-check` as a whole, and for `--fold-check` the claims that need a fold or a turn in flight, while its end-state claims still run and it can still end `RESULT: PASS`.

### Arriving

**Added 2026-11-08, with v1.3.0.** As the panel slides in, the figures are already the real ones; only bars and arcs move, and whichever face was left open fills in this way.

- **The plan bars** grow from 0 to their percentage over **600 ms** on the decelerate curve, starting **120 ms** after the slide begins, a model's own weekly limit included.
- **On the totals face each chart bar** rises from the baseline to its height over **600 ms** on the decelerate curve, bar `i` starting **120 + 10 × i ms** in, so the thirty-one overlap and the chart rises as one wave rather than one bar after another.
- **On the model face** the doughnuts sweep and the model rows fade in as they do when the block turns to it (*The block's turn*, below), starting **120 ms** in, with row `i` at **120 + 70 × i ms**.

### The Output/Overall switch and the table's fold

Pressing **Output** or **Overall** moves the panel to the other view instead of redrawing it, and the window stays on its docked corner while it does.

- **The highlight** slides from one segment to the other, position and width together, **360 ms** on the decelerate curve in both directions, from wherever it is on screen.
- **The chart's bars** move to the other series together over **400 ms** on the decelerate curve, each from the height it has on screen. A day with a bar in only one series moves to or from nothing.
- **The table** first fades its old rows out over **140 ms**, linear. Then its height moves to the new view's, **400 ms** on the decelerate curve growing and **280 ms** on the accelerate curve shrinking, while the new rows fade in and rise 4 DIP into place over **300 ms** each, row `i` (0 at the top) starting **100 + 36 × i ms** in, so the last starts before the fold ends. Opacity and a render transform take no part in layout, so the fold's measured height is unchanged. The fold is [`DisclosureAnimation`](../src/O-view.Tray/Popup/DisclosureAnimation.cs) with its own figures, handed to it by its caller; the window is re-docked on every frame of it, and the panel is first fitted to the display for the height it is about to have, so a short display compacts before anything is laid out at the far end.
- **A hover fill** on the switch fades in over **140 ms** and out over **320 ms**; a press is never animated. The switch is the only thing here that reacts to the pointer, since the table's rows and pills have no hover.

The fold used to be the tokens-used breakdown's, under a Bars / Breakdown switch, with the same curves and the same lessons: it appeared whole until 2026-11-06, the body, the `SizeToContent` window's height and the re-dock all changing in one frame.

**The table's hairlines keep their strength on their brush,** not on the element, because the fold fades every cell to full opacity and an element-level strength would lift the line with it.

**The frozen mid-fold render is a point in time.** `typical-overall-folding` is the switch caught **320 ms** after Output was pressed to Overall, with the height from the curve at that time and each row's opacity and offset from its own start and duration at that time, all read through `PanelMotion`, so the still and the animation cannot drift apart. It is not a fraction of the way through anything.

**Two failures here are invisible to any offscreen render**, and both were found by [`--fold-check`](../src/O-view.Tray/Diagnostics/SampleRenderer.cs), which drives the real panel and grabs the pixels the compositor actually presented:

- **The window must never be laid out at the far end of the fold.** Applying the end state and laying it out, the obvious way to measure where the fold ends, resizes a window docked by its top-left, and that HWND is presented before the re-dock catches up: one frame of full-height panel 72 px down, its bottom inside the taskbar. The end point is measured off a copy of the table that is never shown, and the body is pinned at the fold's starting height before any layout runs, so every size the window takes is one the fold docks it for.
- **Dock against the content's `DesiredSize`, never the window's `ActualHeight`.** A `SizeToContent` window's own size trails the layout pass that produced it, and the fold changes that height sixty times a second; docking against the trailing number left a collapse settled 13 px above its edge.

### Hiding the banner

The banner fades over **140 ms** while its height folds closed over **280 ms** on the accelerate curve. The blocks below move up with it and the window is re-docked on every frame, through the same fold class as the table.

### The menu's threshold list, ticks and highlights

The menu's alert-threshold list folds the same way, with `DisclosureAnimation` and its own
figures: **340 ms** open on the decelerate curve, **240 ms** closed on the accelerate curve, the
chevron turning 0 to 180° with it, and the window re-docked on every frame. The three option
rows fade in over **260 ms**, row `i` starting **80 + 50 × i ms** in; closing, they fade out
together over **120 ms**. Choosing a threshold shows the new value in the pill at once, scales
the dot on the chosen row from 0.3 to 1 while it fades in over **240 ms**, and starts folding the
list closed **300 ms** after the click so the dot is seen first. If the menu is dismissed during
that hold, nothing fires afterwards.

A tick **draws itself** from its start to its end over **320 ms** (decelerate) when it turns
on, and fades out over **200 ms** when it turns off. Each row holds its state in a field and the
visual follows it, because a tick still fading out is still visible and reading it back would
give the wrong answer to a quick second click. A row's hover and keyboard-focus fill fades in
over **140 ms** and out over **320 ms**; a press is never animated, so pressed fills stay
instant.

### The block's turn

**Added 2026-11-08, with v1.3.0.** The local block turns over between its totals face and its model face with both faces in the layout, using the stat tile's old crossfade on the whole block (`PopupLocalBlock.Turn`). The face going out fades to 0 and moves 5 DIP over **180 ms** on the accelerate curve, up on the way to the models and down on the way back; the face coming in fades to 1 and settles from the opposite side over **340 ms** on the decelerate curve, starting **100 ms** in so the two overlap. The block's **height** moves to the new face's over **400 ms** on the decelerate curve in both directions, and the window is re-docked on every frame; the panel is fitted for the new height first, as for the table's fold. The two-arrow mark on the flip tag turns half a circle over **480 ms** on the decelerate curve, and is symmetric, so it looks the same at rest either way.

- **On the model face** each doughnut fills as **one clockwise sweep** from 0 to 100 percent over **600 ms** on the decelerate curve, starting **180 ms** in on a turn and 120 ms in on an open, and its sections appear in turn as the sweep passes them rather than each drawing on its own. The sweep is a cover in the panel's colour that retreats around the ring, and the cover's resting state paints nothing, so a missed completion cannot leave a ring hidden. The model rows fade in over **280 ms** each, row `i` at **300 + 70 × i ms** on a turn (120 + 70 × i on an open); heading rows count as rows.
- **On the way back** the chart's bars rise from the baseline as on open, **600 ms** from **180 ms** in.
- **A press on the Output/Overall switch during a turn** lets the block keep turning and land on the new measure, with the height re-aimed at it. **A turn during the switch's fold** ends the fold, so the block lands on the face that was pressed.

**The hover cue.** While the pointer is over the local block, the flip tag's fill brightens to `BarTrack` and its text to `TextPrimary` (**140 ms** in, **320 ms** out), the mark makes the turn's half circle (**480 ms**), and a faint outline appears around the block: 1 px at 35 percent of `PanelBorder`, 6 outside the block, with a 6 radius. It shows what would turn.

**Hover cards** fade in over **140 ms**, with the tooltip timings above.

### A second click lands where it asked

Every open shows the view the panel was left in, **settled**, with no transition in flight but the arrival fills above: the panel on its remembered measure and face, the menu with the threshold list closed, set at once. Past that, **a click during a transition continues from what is on screen**. It never snaps to an end and replays. The table's fold, the block's turn, the switch's highlight and the threshold list each turn round from where they are, and a second click ends in the state **that click** asked for, whichever way the first one was going: a second press on the switch mid-fold reverses the fold from its current height, and a second turn mid-turn reverses the crossfade from its current opacity. The banner's fold does not turn round: it cannot be brought back from there, so a second press on the × changes nothing. One case runs on two clocks: a press during a fold turns the height round at once, while the rows on screen fade out over their usual 140 ms and the other view's rows take their place; and a press during that 140 brings the same rows back from where they are, because they are the rows that press asks for. That is a small state machine with several ways to end up wrong, so it is proved on the real windows rather than assumed:

- [`--motion-check`](cli.md) turns the block between its totals and its models and folds the menu's threshold list to each end, then clicks a second time while the first transition is still running, once reversing an opening and once reversing a closing, and reads the state each rests in. A reversal of the block lands while both faces are on show, and the check fails if either face jumps. Every wait is a `PanelMotion` figure, so a retuned pace retunes the check.
- `--fold-check` does the same for the Output/Overall fold: a click mid-opening ends closed, and a click reversing a close, landing mid-fold, continues from the height on screen and ends open. It then turns the block over and back and reads the panel's height and its bottom edge on every frame.
- `--view-check` closes the panel with a fold still running and reads the view back from a panel built from the settings file alone, for each of the four views, and `--still-check` reads a settled panel with animations forced off.

### Clicking the icon toggles

The tray icon **opens and closes** the surface, as every taskbar flyout does. Left-click toggles the panel, right-click toggles the menu.

This needs more than an `IsVisible` check. The click itself dismisses the surface by taking focus from it, so by the time the click handler runs the surface is already closing and looks closed, which is why it used to reopen immediately and could only ever open. Each surface therefore records **when it last began closing from lost focus**, and a click arriving within 400 ms is treated as the second half of the toggle. The window is wide enough to cover the deactivate-then-click ordering plus the 300 ms close slide, and short enough that a deliberate click a moment later still opens. A click during the close therefore leaves the surface closed; it does not bring it back. The window is a contract with the tray icon, not a tuning knob, and `--toggle-check` and `--menu-check` are what notice if a longer slide ever outruns it.

---

## 5. Dialogs

O-view asks questions on **its own window**, not a `MessageBox`. The system box was the one surface that gave the app away, raw Win32 chrome, a stock blue "i" glyph, Yes/No buttons, and no room for a mark, so nothing on it said which application was asking.

[`DialogWindow`](../src/O-view.Tray/Popup/DialogWindow.xaml) carries the same mark, palette and type as the menu and the panel: rounded card, brand mark beside the title, message, an optional muted detail line.

- **The two answers have different weight**, an accent-filled primary and an outlined secondary, rather than two identical buttons the user must read both of to tell apart.
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

**`DisableWelcomePage=no` is deliberate.** The large image only appears on the Welcome and Finished pages, and modern wizard style suppresses Welcome by default, so without it the branding would first appear only *after* the install had finished. One extra click on a first-time install buys a setup that identifies itself; the auto-update path runs `/SILENT` and never sees that page.

### The surface that cannot be branded

**Balloon notifications are drawn by Windows.** They come from `Shell_NotifyIcon` via `NotifyIcon.ShowBalloonTip` (ADR-0005) and take the system's own chrome, O-view supplies only a title, a body and an icon. There is no styling hook. Branding them would mean replacing them with custom toast windows, which is a different feature, not a coat of paint.

---

## Data source summary

| UI element | Source |
|---|---|
| Name, email, tier | `~/.claude.json` |
| Session and weekly % | `plan-usage-history.json` (Claude Desktop) or `cachedUsageUtilization` in `~/.claude.json` (Claude Code), whichever carries more meters most recently; O-view runs `claude /usage` to keep the latter fresh |
| Session reset | Reported in the cached figures (`five_hour.resets_at`) while one is ahead of now, exact; otherwise derived from `fh` drops in the plan history, bracketed |
| Weekly reset | Reported in the cached figures and stored as an anchor ([ADR-0014](adr/0014-weekly-reset-is-a-reported-constant.md)); entered by hand where never reported |
| Tokens today / 31 days | Rollup store, fed from Claude Code and Cowork transcripts ([ADR-0006](adr/0006-local-rollup-store.md)) |
| Est. value | Rollup store and the bundled price table, checked weekly against the published one ([ADR-0016](adr/0016-published-reference-data-is-fetchable.md)) |
| Usage graph | Rollup store |
| A model's weekly limit | `cachedUsageUtilization.utilization.limits[]` in `~/.claude.json`, read directly rather than through the snapshot ([ADR-0019](adr/0019-model-limit-and-reported-spend.md)) |
| Off-plan banner | The divergence detector, with the account's extra-usage setting from the same cached block |
| Off-plan section | Without a model limit, the rollup store priced over the credit-billed model list; with one, `cachedUsageUtilization.utilization.spend`, which states no period. An exact credit balance has no local source and is not shown |

Every element is served from local files and from invoking Claude's own client, with no credential handled ([ADR-0015](adr/0015-no-credential-based-usage-sources.md)).

Degradation path: if Claude Desktop is not installed or its data is stale, the session and weekly bars fall back to JSONL estimates labelled `local estimate`, and reset times show as unknown. The panel degrades; it does not break.
