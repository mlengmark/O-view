# Architecture

O-view reads Claude's usage from files on this machine and shows it in the notification area on Windows and Linux. This page describes the rules the code follows, and **all new code follows them.** Display text has moved out of Core into Presentation; what's left in Core by design is Core's own provider reports, which compose prose from data they resolve, and `DiagnosticsHint`, a phrase each head supplies once at startup.

## Layers

References run one way: **skins → App → Presentation → Core**. A project may use only the ones to its right. Core uses none of them, and Presentation never uses App or a skin.

| Project | Holds | Never holds |
|---|---|---|
| `O-view.Core` | Reading Claude's files, the usage store, pricing, reset and divergence detection, usage snapshots and statistics | Display text, formatting, colours, UI or platform APIs |
| `O-view.Presentation` | Everything a user reads or sees: wording, number and date formats, colour bands, icon geometry, and a view model per panel section, tooltip and menu, shared by both skins | WPF, Avalonia, Win32, D-Bus; anything platform-specific |
| `O-view.App` | The shell: polling, settings, alerts, updates, and the two interfaces to the skins | Formatting; any knowledge of which skin is running |
| `O-view.Tray` | The Windows skin: XAML layout and styles, `NotifyIcon`, the registry, the Windows updater | Formatting or business logic |
| `O-view.Linux` | The Linux skin: Avalonia layout and styles, StatusNotifierItem over D-Bus, XDG autostart | Formatting or business logic |

**The test for any change:** a new panel tile is added once, in Presentation. Each skin then needs only layout to show it. If a change needs the same logic written in both skins, it belongs in Presentation instead.

## Where the panel's content is built

`PanelSections` (`src/O-view.Presentation/Panel/PanelSections.cs`) builds one view model per section, each in its own file beside it under `src/O-view.Presentation/Panel/`:

| Section | View model | Read by |
|---|---|---|
| Header, account | `PanelHeaderView` | both |
| Session/weekly meters, boost chip | `PanelMeterView`, `PanelBoostChipView` | both |
| A model's own weekly limit bar | `PanelSections.ModelLimitMeters`, one `PanelMeterView` per model | both |
| **Your plan** block: the meters, the off-plan row that closes it, and the off-plan banner above both blocks | `PanelPlanBlockView` (`PanelSections.PlanBlock`), `PanelOffPlanRowView`, `PanelOffPlanBannerView` | Windows |
| **On this machine**, totals face: the 31-day chart with its week marks, and the Output or Overall table | `PanelLocalTotalsView` (`PanelSections.LocalTotals`), `PanelLocalChartView`, `PanelLocalWeekMarkView`, `PanelLocalTableView`, `PanelLocalRowView` | Windows |
| **On this machine**, model face: two doughnuts and a row for every model | `PanelModelFaceView` (`PanelSections.ModelFace`), `PanelModelDoughnutView`, `PanelModelSectionView`, `PanelModelTableView`, `PanelModelRowView` | Windows |
| The local block's footer: the one caveat and the Output/Overall switch | `PanelLocalFooterView` (`PanelSections.LocalFooter`), `PanelMeasureOption` | Windows |
| The view the reader left the panel in: Output or Overall, totals or models, banner hidden | `PanelViewChoice`, passed in by the head and given back; Presentation stores nothing | Windows |
| Stat tiles, per-model breakdown | `PanelTileView`, `PanelTilesView`, `PanelTileSegmentView` | Linux |
| 31-day graph, weekly-reset gridlines | `PanelGraphView`, `PanelGraphColumnView`, `PanelGraphBoundaryView` | Linux; Windows reads the columns only |
| Off-plan banner and section | `PanelOffPlanView`, `PanelOffPlanBannerView`, `PanelOffPlanSectionView` | Linux |
| Tokens-used bars, legend and breakdown table | `PanelTokenBarsView`, `PanelTokenBarView`, `PanelTokenBreakdownRow` | Linux |
| The scope note beneath the tiles | `PanelSections.ScopeNote` | Linux |

**The two heads draw different panels today.** Windows has the two-block layout, and reads the plan block, the totals face, the model face and the footer. Linux has not been given it: it keeps its earlier layout of tiles, tokens-used bars and graph, reads none of the four view models above, and draws every sample state, including the Overall and model-face ones, through that layout. Both are built from the same Presentation, so the words and figures are shared where the two panels show the same thing; the layouts are not. Bringing Linux to the new design is separate work, and nothing in the documentation says it has it.

A skin only lays the view model it is given out, it computes no figure, no band and no word. Windows does this in `src/O-view.Tray/Popup/`: `PopupWindow.xaml.cs` assembles the panel, `PopupPlanBlock.cs`, `PopupMeter.cs`, `PopupModelMeters.cs`, `PopupWeeklyRow.cs` and `PopupDivergence.cs` lay out the plan block and the banner, and `PopupLocalBlock.cs` lays out the local block and its footer with `PopupGraph.cs` (the chart), `PopupTotalsTable.cs` (the Output and Overall table), `PopupModelFace.cs` and `PopupDoughnut.cs` (the model face). Linux does it in `src/O-view.Linux/Panel/` (`PanelContent.cs`, plus `PanelGraph.cs`, `PanelOffPlan.cs`, `PanelStatTiles.cs` and `PanelTokenBars.cs`). Both files that assemble a whole panel, `PopupWindow.xaml.cs` and `PanelContent.cs`, stay under 600 lines by design, a skin that grows past that is usually a skin that has started computing content again.

## The panel's height

One rule answers how tall the flyout window may be, and whether its content has to scroll:
`PanelFit.Resolve` (`src/O-view.App/Rendering/PanelFit.cs`) takes the content's own measured
height and the space the display's work area leaves, and caps the window at whichever is
smaller: the content's height if it fits, the available space if it doesn't, with
`MustScroll` true exactly in the second case. It lives in App so both faces ask the one rule
the same question, rather than each picking its own answer. `PanelFit.Fit` runs the whole
sequence around it (measure, pick a density, compact and measure again, cap) through two
callbacks each head supplies, so neither head orders those steps itself. The layout numbers
both heads draw with (panel width, scroll bar, the blocks' padding, the table columns, the
doughnut and chart figures, and the strength of each line) are stated once in `PanelMetrics`
beside it.

- **Both faces measure the content, never the window.** A flyout is sized to its own content,
  so its frame is already capped by whatever the display granted before `PanelFit` is ever
  asked, reading the frame back would describe the screen, not what the content wants. Each
  face has its own `NaturalHeightDip()` that measures the tree directly instead
  (`PanelContent.NaturalHeightDip` on Linux, `PopupFit.NaturalHeightDip` on Windows).
- **A display too short even for the compact layout (`PanelDensity`) gets a scroll viewport,**
  built only once `PanelFit` says the content overflows, a panel with room keeps exactly the
  tree it always had, so no existing render moves. Windows: `PopupScroll`. Linux:
  `PanelContent.ScrollContent`.
- **Neither face decides the cap or the scroll choice itself.** Both call `PanelFit.Resolve`
  and act on what it returns; the rule never duplicates into a skin.

## The panel's motion

`PanelMotion` (`src/O-view.App/Rendering/PanelMotion.cs`) states the Windows head's motion once:
the pace, every base duration and delay, the two fitted curves as four numbers each, and the
small arithmetic they imply (when row `i` of a group starts, where a curve is at a given
fraction of its time, whether a transition runs at all when Windows' animation setting is off).
It holds no WPF or Avalonia type, so App's tests on Linux pin the rules between the figures: an
exit is quicker than its entrance, every figure scales with the pace, a curve is monotonic from
0 to 1. The Windows head reads it through `WpfMotion` (`src/O-view.Tray/Popup/WpfMotion.cs`),
which answers "are animations on?" from `SystemParameters.ClientAreaAnimation` and turns a
`PanelMotion` curve and figure into a `KeySpline` and a `Duration`; `FlyoutAnimation`,
`DisclosureAnimation`, `MenuWindow`, `PopupLocalBlock`, `PopupTotalsTable`, `PopupModelFace`,
`PopupDoughnut` and `PopupGraph` ask those two and decide nothing about how long anything takes. The Linux head reads none of it: it has no motion. What each figure is, and
why, is in [ui-spec.md](ui-spec.md) section 4.

## Where the panel's remembered view lives

Which way the panel was left, Output or Overall (`PanelMeasure`), the totals or the model face (`PanelFace`), and whether the reader hid the off-plan banner, is one value that goes round a single loop and is decided nowhere else:

- **`TraySettings`** (`src/O-view.Core/Models/TraySettings.cs`) keeps the three fields in `settings.json`, the two enums by name, and a file from before them opens on the defaults: Output, totals, banner shown. The enums live in Core (`PanelView.cs`) so the setting and Presentation agree on them without either referencing the other.
- **`ISkinToShell.PanelView` and `SetPanelView`** are how a head reads it on every open and writes it when the reader presses the switch, the flip tag or the banner's hide control. There is one setter for all three parts, so a head changing one cannot lose the other two.
- **`PanelViewChoice`** (`src/O-view.Presentation/Panel/PanelLocalFooterView.cs`) is the value Presentation takes in and gives back. Each view model's builder reads it, so the rule for what Output and Overall count, which face shows and whether the banner draws is stated once, in Presentation.
- **The banner's flag belongs to the off-plan spell it was pressed in.** The shell clears it when a poll reads the account as no longer off-plan, so a later divergence shows the banner again; a poll that cannot tell, or fails, leaves it hidden.

`--view-check` reads the view back from a panel built from the settings file alone, which is all a restart carries over.

## Where a dragged panel's place lives

Clamping, snapping, saving and restoring a dragged panel's place are each decided once, in
App, behind an injected store, never per head:

- **`PanelPlacementMemory`** (`src/O-view.App/Rendering/PanelPlacementMemory.cs`) owns the
  rule. `Resolve` answers where the panel opens, re-clamping a remembered place into the work
  area as it stands now; `Remember` answers what to store when the user lets go, snapping the
  offset flush once it is within 16 px of an edge or corner. `Nudge` answers the same question
  for a keyboard step: the same clamp and the same store, but the step is kept exactly rather
  than snapped, since a panel that nearly fills its screen has less room either side of it than
  the snap distance and every step would be pulled back to the edge it came from.
- **`IPanelPositionStore`** (`src/O-view.App/Platform/IPanelPositionStore.cs`) is the seam
  between that rule and storage, so `PanelPlacementMemory`'s edge cases can be exercised
  against an in-memory fake instead of real file I/O.
- **`PanelPositionFile`** (`src/O-view.Core/Storage/PanelPositionFile.cs`) is where a place is
  actually kept on disk, one entry per display, written atomically and read defensively,
  the same shape as `WeeklyResetAnchor`. It stores the work-area corner as a bare string,
  since Core may not name `PanelPlacementMemory`'s own `WorkAreaCorner` type; translating
  between the two is `FilePanelPositionStore`'s job, in App, and both heads construct that
  one class.

Each head only wires up the gesture that reaches these: a drag handle on the header and the
Alt-arrow-key shortcuts, ending at `Remember`/`Resolve` and at `Nudge` respectively. Neither
head clamps, snaps, steps or reads the file itself ([ADR-0017](adr/0017-remembered-panel-position.md)).

## How data flows

    Claude's files ──▶ Core (providers ▶ snapshot + statistics)
                         │
                         ▼
                       App (poll loop, settings, alerts) ──▶ Presentation (view models, text)
                         │                                        │
                         ▼                                        ▼
                    IShellToSkin  ◀── skins render view models ◀──┘
                    ISkinToShell  ──▶ user commands (open panel, toggle setting, exit)

- **Snapshots are chosen whole**, from one source at a time, and never merged field by field. Two things are not in a snapshot and are read from Claude Code's cached block directly: a model's own weekly limit and the spend Claude reports past the plan. `PanelSections.ModelLimitMeters` and the off-plan section take that block, never a snapshot, so the same block gives the same bar and the same figure whichever source won ([ADR-0019](adr/0019-model-limit-and-reported-spend.md)).
- **Every value says what it is.** A session percentage, a reset time, a token count and a modelled USD figure are never a bare nullable, each is one of Core's four status-paired types (`UsagePercent`, `UsageInstant`, `TokenCount`, `EstimatedUsd`), a `Value` beside a `UsageValueStatus` of `Real` (measured), `Estimated` (derived or modelled) or `Unavailable` (no source produced one). A value flagged `Unavailable` never carries a figure, so a reader never has to decide whether a null means "nothing was read" or "the answer is nothing" (CLAUDE.md rule 6). Core produces these pairs; Presentation is the only layer that turns a status into wording, "local estimate", "unavailable", the as-of suffix on a stale tooltip, so a skin never has to decide what a status means, only how to lay out what Presentation already said.
- **The shell calls skins on the UI thread, always, and only through `IUiDispatcher`.** `ShellHost` is the one place the engine is built, started and disposed; it posts every `IShellToSkin` push onto the dispatcher, including the ones that already arrive on the UI thread, so a skin never has to work out which of the four it can assume the thread on. On Linux, D-Bus callbacks arrive on a thread of their own first: they go through `BusCallback`, which posts to the UI thread and never blocks it, before reaching `ShellHost`'s dispatcher the same way every other push does.

## The two interfaces

- **`ISkinToShell`**, what a skin asks of the shell: the latest snapshot, settings, cached usage, the panel's statistics, the view the panel was left in and the setter for it, and the refresh and weekly-reset commands. `UsageEngine` implements it.
- **`IShellToSkin`**, what the shell tells a skin: show a snapshot, show a notification, and the two "something is due" pushes (an update check, a rate-card check). Both skins implement it; `ShellHost` is what calls it, always through `IUiDispatcher`.

## UsageEngine's pieces

`UsageEngine` now composes the following, each owning one part of what it used to do all itself; `UsageEngine` forwards to them rather than re-implementing, so no caller outside `O-view.App` changed:

- **`UsageProviders`**, the account read, the plan-history and cached-utilization sources, the JSONL fallback, and the composite provider they feed.
- **`PollRunner`**, the off-thread read/publish mechanics and the per-cadence gate, shared by the full poll, the plan poll and the usage-cache refresh.
- **`PlanHistoryPoll`**, the cheap plan-history-only cadence, and the panel's statistics build, including where the off-plan window starts ([ADR-0018](adr/0018-session-window-source.md)).
- **`WeeklyResetCoordinator`**, the weekly reset end to end: the manual override, Claude Code's reported instant, and the conflict between the two (ADR-0014).
- **`UsageAlerts`**, the threshold and off-plan notification decisions (the wording itself lives in Presentation's `NoticeText` and `OffPlanText`).
- **`UsageCacheRefresh`**, the floor, freshness gate and billed-charge latch behind the Claude-CLI usage-cache refresh (issue #234).

`ShellHost` sits outside this list: it is the attach point a skin is built from, not a piece `UsageEngine` was carved out of.

## Where platform differences live

They live in whichever implementation gets constructed, never in `#if` blocks or runtime OS checks scattered through the logic. The one deliberate runtime check is data-root discovery (`ClaudeDataRoots`), which lives in a single named place.

## The linux-runtime check

CI's `linux-runtime` job runs a handful of tests under a virtual display (`xvfb-run`) and a real session bus (`dbus-run-session`), with `OVIEW_LINUX_RUNTIME=1` telling them to run rather than return early. A stand-in tray host stands in for a notification area, test code that owns `org.kde.StatusNotifierWatcher` on the bus and talks the real protocol, so the real Linux app's tray icon, click and menu reach it over a real D-Bus connection with Avalonia's own dispatcher on the other end.

It proves a real left click opens the panel end to end, and that the tray menu answers within its budget, both under that virtual display and Avalonia 12.1.3. It does not prove `BusCallback`'s UI-thread hand-off: in this job the click already arrives on the UI thread, so there is no wrong-thread moment to catch. The mocked `BusCallbackTests` prove the hand-off instead, by choosing the thread themselves.

## What enforces this

- **A reference test:** `PresentationBoundaryTests` fails if Presentation's compiled assembly references a UI framework or platform API.
- **A source scan:** `SkinBoundaryTests` fails if either skin's source names `UsageEngine` or any of the classes it split into, a skin may reach the shell only through `ISkinToShell` and `IShellToSkin`.
- **A dispatch test:** `ShellToSkinDispatchTests` fails if any `IShellToSkin` push reaches a skin before `IUiDispatcher` has run it, including the three pushes that already arrive on the UI thread.
- **Golden images:** every shared sample state is rendered for both skins and compared, pixel for pixel, with `tests/golden/` (`tools/check.ps1`, and CI's `visual` job).
- **A clipping check:** `--popup-samples` asks every settled Windows panel and every hover card whether anything crosses the panel's edge, is cut to its slot or has text that trims (`PanelEdgeCheck`), and exits 1 on any miss, which `tools/check.ps1` and CI's `visual` job fail on. The sample states include the widest figures the formatters produce and a model with a long id.
- **The smoke test:** the real engine runs against `tests/fixtures/claude-home`, so files on disk must reach the numbers the panel shows.
- **Shared test fakes:** `tests/O-view.TestSupport` holds the fixed and throwing providers, the in-memory position store, the capturing log and the temp directory the test projects use, so a fake is written once.
- **`docs/feature-checklist.md`:** each feature has a test or sample state that proves it.
- **CI's `guard-ratchet` check:** no PR removes a required check, a feature, its proof or a test.
- **CI's `merge-order` check:** parallel PRs never change the same files, and land in their planned order.
- **A reflection test keeps display text out of `OView.Core.Models`:** `CoreModelsHoldNoDisplayTextTests.NoPublicMemberInCoreModelsReadsLikeDisplayCopy` fails on a public member named like copy (ending `Note`, `Label`, `Caption`, `Hint`, `Title`, `Detail`, `Caveat`, `Heading`, `Message`, `Summary`, `Text` or `Instruction`) or a public string constant whose value reads as a sentence.

Changes to the checks themselves, to CI or to dependencies are guarded. They're made only when a phase brief lists them.

## Unchanged from before

These rules all still hold; their reasons are in `CLAUDE.md` and the ADRs:
- read-only against Claude's files;
- no credential handling;
- local machine only;
- de-duplication of usage records by request id;
- weekly resets as a reported constant (ADR-0014);
- no third-party tray library on Windows (ADR-0005).
