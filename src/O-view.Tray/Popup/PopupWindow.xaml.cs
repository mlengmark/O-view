using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using OView.App.Rendering;
using OView.Core.Models;
using OView.Core.Providers.Jsonl;
using OView.Core.Providers.PlanHistory;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;
using Size = System.Windows.Size;
using ToolTip = System.Windows.Controls.ToolTip;

namespace OView.Tray.Popup;

/// <summary>
/// The detail panel (ui-spec.md). Times convert to local here, at the display edge.
/// Dismisses on Esc and on deactivation (click-outside). Theme follows
/// AppsUseLightTheme — the app-window setting, distinct from the taskbar's — and is
/// re-read on every open so a theme switch never needs a restart.
/// </summary>
public partial class PopupWindow : Window, IFlyout
{
    private static readonly Color Green = Color.FromRgb(64, 200, 110);
    private static readonly Color Amber = Color.FromRgb(240, 170, 40);
    private static readonly Color Red = Color.FromRgb(232, 72, 72);

    /// <summary>Forces a theme for verification screenshots; null follows the OS.</summary>
    public bool? ThemeOverride { get; set; }

    /// <summary>
    /// Docking, the open/close transitions and the toggle grace window — shared with the
    /// tray menu rather than reimplemented here (issue #54).
    /// </summary>
    private readonly DockedFlyout _flyout;

    /// <summary>Disables auto-hide for verification screenshots.</summary>
    public bool PinForVerification
    {
        get => _flyout.PinForVerification;
        set => _flyout.PinForVerification = value;
    }

    /// <inheritdoc cref="DockedFlyout.ClosedByClickAway"/>
    public bool ClosedByClickAway => _flyout.ClosedByClickAway;

    public PopupWindow()
    {
        InitializeComponent();
        _flyout = new DockedFlyout(this);
        Deactivated += (_, _) => _flyout.OnDeactivated();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) _flyout.BeginClose(); };
        _disclosure = new DisclosureAnimation(
            this, TokenExplainBody, TokenExplainChevronRotation,
            // Re-dock, exactly as the menu does when its threshold list opens (issue #33).
            // The panel is docked by its top-left and is SizeToContent, so growing it without
            // this pushes the new content down into the taskbar — which is the failure the
            // docked placement was introduced to avoid, reintroduced the moment the panel
            // gained something that could grow. It also re-fits the open animation's clip,
            // without which the window is the right size and the card inside it is not.
            //
            // The fold calls this on every frame it changes the window's height, so the
            // bottom edge stays pinned to the docked corner for the whole transition.
            () => _flyout.Redock(ActualWidth, DockHeight));

        TokenExplainToggle.Click += (_, _) => ToggleComposition();
    }

    /// <summary>
    /// Opens or closes the token explanation, as a fold rather than as a jump.
    ///
    /// <para><b>The panel is never laid out at the far end of the fold.</b> The obvious order —
    /// apply the final state, lay it out, then animate back from where the fold was — puts the
    /// window at its full height for the length of that layout, still docked for the height it
    /// had. It does not stay there, but it is presented: <c>--fold-check</c>'s screen grabs
    /// caught the frame, a whole panel 72 px down with its bottom inside the taskbar. So the
    /// end point is measured off the body alone, the body is pinned at the fold's starting
    /// height, and every size the window takes from here is one the fold puts it at and
    /// re-docks it for.</para>
    /// </summary>
    private void ToggleComposition()
    {
        _compositionExpanded = !_compositionExpanded;
        RunFold(_disclosure, _compositionExpanded, TokenCompositionLine.ActualWidth);
    }

    /// <summary>
    /// The fold itself.
    ///
    /// <para><b>Kept as one method, because what makes it correct is easy to leave out.</b> The
    /// re-fit below is one line and looks optional; without it a panel that fitted closed and
    /// does not fit open is clipped by the taskbar, which is the reported failure this whole
    /// path exists to prevent. A hand-written copy of this method would work on the developer's
    /// tall display and fail on a short one.</para>
    /// </summary>
    /// <param name="availableWidth">
    /// The text column the body wraps within — a disclosure measures against the line it sits
    /// under, not against the panel.
    /// </param>
    private void RunFold(DisclosureAnimation disclosure, bool expanded, double availableWidth)
    {
        // Captured before anything moves, so a click landing mid-fold continues from what is
        // on screen rather than snapping to one end and travelling the whole way again.
        var (fromHeight, fromAngle) = disclosure.Current;
        var toHeight = expanded ? disclosure.MeasureNatural(availableWidth) : 0;

        disclosure.Prepare(fromHeight);

        // Expanding is exactly when the panel crosses the work area, so the density is
        // re-decided here rather than only at open — a panel that fitted closed and does
        // not fit open is the whole reported case. It is told what the fold is about to add,
        // because the panel in front of it is still the size it was before the fold.
        if (_lastStats is { } stats && _lastSnapshot is { } snapshot)
        {
            FitToWorkArea(stats, snapshot, pendingGrowth: toHeight - fromHeight);
        }

        UpdateLayout();
        _flyout.Redock(ActualWidth, DockHeight);

        disclosure.Run(expanded, fromHeight, fromAngle, toHeight);
    }

    /// <summary>
    /// Opens the token explanation for a verification render. The disclosure resets on every
    /// Populate, so a sample of the expanded state cannot be produced any other way — and an
    /// unrendered state is how the no-data banner spent its whole life saying the wrong
    /// thing (issues #58, #170).
    /// </summary>
    internal void ExpandCompositionForVerification() => SetCompositionExpanded(true);

    /// <summary>
    /// Folds the explanation from outside, for <c>--fold-check</c>. The real transition,
    /// not the instant state: an animation's completion is the thing being checked, and this
    /// app has shipped a missed one before (<see cref="FlyoutAnimation"/>).
    /// </summary>
    internal void ToggleCompositionForVerification() => ToggleComposition();

    /// <summary>
    /// The height to dock against: the content's freshly measured size, not the window's.
    ///
    /// <para>A <c>SizeToContent</c> window's <c>ActualHeight</c> trails the layout pass that
    /// produced it by a frame, and the fold changes that height sixty times a second. Docking
    /// against the trailing number places the panel for the height it had a frame ago — which
    /// <c>--fold-check</c> caught twice: 72 px out for one frame when the fold starts, and a
    /// collapse settling 13 px above its docked edge and staying there. <c>DesiredSize</c> is
    /// the measure that the window's own size is about to follow.</para>
    /// </summary>
    private double DockHeight =>
        Content is FrameworkElement root && root.DesiredSize.Height > 0
            ? root.DesiredSize.Height
            : ActualHeight;

    /// <summary>
    /// Where the disclosure row sits on the physical screen, so <c>--fold-check</c> can cut
    /// its filmstrip to the band that actually moves rather than to the whole panel.
    /// </summary>
    internal double DisclosureScreenTop => TokenExplainToggle.PointToScreen(new Point(0, 0)).Y;

    /// <summary>
    /// The folding body's current height — what <c>--fold-check</c> samples.
    ///
    /// <para>Read from the <c>Height</c> property rather than from <c>ActualHeight</c>: a
    /// dependency property returns its animated value, while <c>ActualHeight</c> reported the
    /// settled layout throughout the first trace and made a working fold look instant. Auto
    /// (NaN) means nothing is animating, so the arranged height is the answer.</para>
    /// </summary>
    internal double DisclosureHeight
    {
        get
        {
            if (TokenExplainBody.Visibility != Visibility.Visible)
            {
                return 0;
            }

            var height = (double)TokenExplainBody.GetValue(HeightProperty);
            return double.IsNaN(height) ? TokenExplainBody.ActualHeight : height;
        }
    }

    /// <summary>
    /// Freezes the disclosure partway open for a verification render. On a real desktop this
    /// state exists for about a tenth of a second, which is long enough to look wrong and far
    /// too short to inspect — and the failure it guards against, content drawn outside the
    /// height it has been given, is invisible at both ends of the fold.
    /// </summary>
    /// <param name="fraction">How far open, 0–1.</param>
    internal void RevealCompositionForVerification(double fraction)
    {
        // Laid out fully open first: the fold's geometry is a fraction of the body's natural
        // height, which is only known once it has been measured at that height.
        SetCompositionExpanded(true);
        UpdateLayout();
        _disclosure.ApplyPartial(fraction, TokenExplainBody.ActualHeight);
    }

    /// <summary>
    /// Closes the panel from outside — the tray icon completing a toggle. Idempotent, so
    /// a click that arrives while the panel is already closing changes nothing.
    /// </summary>
    public void DismissNow() => _flyout.BeginClose();

    /// <summary>
    /// Why plan data is unavailable, surfaced when the figures read "unknown". Null skips
    /// the banner entirely (nothing to explain).
    /// </summary>
    public PlanHistoryReport? DataReport { get; set; }

    /// <summary>
    /// Where the local token counts were read from, for the scope note beneath the tiles.
    /// Inspected on open like <see cref="DataReport"/>, so it reflects the machine as it is
    /// now. Null falls back to inspecting on demand — which keeps the verification renders
    /// honest rather than letting them show a note no real run would produce.
    /// </summary>
    public TranscriptScopeReport? ScopeReport { get; set; }

    /// <summary>
    /// The settings behind this render, so the weekly line can say when the reset shown is
    /// one the user entered rather than one O-view derived (issue #186). Null falls back to
    /// treating everything as derived, which is the honest default for a render with no
    /// settings behind it.
    /// </summary>
    public TraySettings? SettingsForDisplay { get; set; }

    private TraySettings? _lastSettings;

    public void ShowNearTrayIcon(UsageSnapshot snapshot, PanelStatistics stats, ClaudeAccount? account)
    {
        PanelTheme.Apply(Resources, ThemeOverride ?? PanelTheme.IsAppsLight());
        Populate(snapshot, stats, account);

        // The chart is a Canvas — its ActualWidth is only known after layout, so it is
        // drawn between the flyout's layout pass and its placement rather than in Populate.
        // FitToWorkArea does that drawing, because choosing a density resizes the canvas.
        _flyout.Show(afterLayout: () => FitToWorkArea(stats, snapshot));
    }

    /// <summary>
    /// Picks the density this display can actually fit, then draws the chart at it.
    ///
    /// <para><b>Measured, not guessed.</b> The natural layout is applied first and laid out,
    /// because the panel's height depends on what is in it — a no-data banner, an off-plan
    /// section, an expanded explanation — and a threshold on the work area alone would be
    /// wrong for most of those combinations. The extra layout pass costs nothing next to the
    /// two the flyout already runs.</para>
    ///
    /// <para>The chart is drawn last and unconditionally: its bars are laid out against the
    /// canvas height, so a canvas that just changed size would otherwise keep the previous
    /// drawing at the previous scale.</para>
    /// </summary>
    /// <param name="pendingGrowth">
    /// Height the panel is about to gain or lose but has not yet — the disclosure fold, which
    /// is measured before it plays so that the window is never laid out at the far end of it
    /// (<see cref="ToggleComposition"/>). Zero everywhere else, where what is on screen and
    /// what is being measured are the same panel.
    /// </param>
    private void FitToWorkArea(PanelStatistics stats, UsageSnapshot snapshot, double pendingGrowth = 0)
    {
        ApplyDensity(PanelDensity.Normal);
        UpdateLayout();

        var density = PanelDensity.For(
            ActualHeight + pendingGrowth, PopupPositioner.AvailableHeightDip());
        if (density.IsCompact)
        {
            ApplyDensity(density);
            UpdateLayout();
        }

        BuildGraph(stats, snapshot);
    }

    /// <summary>
    /// Renders the panel to a bitmap without showing it — the detail panel's equivalent of
    /// <see cref="MenuWindow.RenderToBitmap"/>, driving the same <see cref="Populate"/> and
    /// <see cref="BuildGraph"/> the live path does.
    ///
    /// <para>It exists because the panel is otherwise only inspectable by opening it on a
    /// real desktop, which needs the single-instance mutex, puts a window over whatever the
    /// user is doing, and simply fails when something is running fullscreen — the case that
    /// blocked verifying the graph's week gridlines. Rendering offscreen also reaches the
    /// states this machine's real data may never produce, exactly as
    /// <c>--tile-samples</c> does for the tiles.</para>
    ///
    /// <para>The two layout passes are load-bearing and mirror the live sequence. The chart
    /// is a <c>Canvas</c>, so its <c>ActualWidth</c> is unknown until the tree has been laid
    /// out once; the live path gets that from <c>Show()</c> + <c>UpdateLayout()</c> before
    /// calling <see cref="BuildGraph"/>. Drawing the graph before the first pass silently
    /// falls back to the hard-coded default width and the gridlines land in the wrong
    /// places — which is precisely what this hook is meant to catch.</para>
    /// </summary>
    internal System.Windows.Media.Imaging.BitmapSource RenderToBitmap(
        UsageSnapshot snapshot, PanelStatistics stats, ClaudeAccount? account, double scale,
        bool expandComposition = false, PanelDensity? density = null, double? compositionReveal = null)
    {
        PanelTheme.Apply(Resources, ThemeOverride ?? PanelTheme.IsAppsLight());
        ApplyDensity(density ?? PanelDensity.Normal);
        Populate(snapshot, stats, account);

        // The second layout pass is the whole reason betweenPasses exists — see the
        // remarks above and on VisualRenderer.RenderContent. The disclosure is opened here
        // rather than before it, because Populate resets it.
        return VisualRenderer.RenderContent(this, scale, betweenPasses: () =>
        {
            if (compositionReveal is { } fraction)
            {
                RevealCompositionForVerification(fraction);
            }
            else if (expandComposition)
            {
                ExpandCompositionForVerification();
            }

            BuildGraph(stats, snapshot);
        });
    }

    /// <summary>
    /// The graph's hover cards, built standalone for verification. They cannot appear in a
    /// render of the panel — a <c>ToolTip</c> throws if given a parent, so it can neither be
    /// laid out inside one nor screenshotted with it — and hover is the only way to reach
    /// them in the running app, so without this they are unverifiable.
    /// </summary>
    internal IReadOnlyList<ToolTip> BuildSampleHoverCards(bool light)
    {
        PanelTheme.Apply(Resources, light);
        return
        [
            HoverCard.Figure(this, "234.8M tokens", "Tuesday 21 July"),
            HoverCard.Figure(this, "Tue 28 Jul · 06:28", "Weekly limit reset"),
            HoverCard.Text(this,
                "Start of the calendar week (Monday). O-view hasn't observed a weekly reset "
                + "yet, so this is a calendar reference, not your plan's boundary."),
        ];
    }

    /// <summary>
    /// Lays the panel out at the given density (<see cref="PanelDensity"/>) — the response to
    /// a display too short for the natural size.
    ///
    /// <para>Only spacing moves. Every figure, caveat and section is present at either
    /// density: a panel that dropped a section to fit would make a number quietly absent,
    /// which reads as a number that is zero (rule 6).</para>
    ///
    /// <para>The graph canvas is the one non-spacing change, and it is the reason this is
    /// worth doing at all — 86 px of hard-coded height that never asked how much room
    /// existed. Callers must redraw the chart after this: its bars are laid out against the
    /// canvas height, so a canvas that changed size leaves the previous drawing behind.</para>
    /// </summary>
    internal void ApplyDensity(PanelDensity density)
    {
        PanelRoot.Padding = new Thickness(density.RootPadding);

        HeaderSeparator.Margin = new Thickness(0, density.SeparatorGap, 0, density.SeparatorGap);
        CreditsSeparator.Margin = new Thickness(0, density.SeparatorGap, 0, density.CreditsSeparatorBottom);

        WeeklyHeading.Margin = new Thickness(0, density.SectionGap, 0, 0);
        TileGrid.Margin = new Thickness(0, density.TileGridTop, 0, 0);

        GraphHeading.Margin = new Thickness(
            0, density.GraphHeadingTop, 0, density.GraphHeadingBottom);
        GraphHost.Height = density.GraphHeight;

        // Four arguments: WPF's Thickness has no (horizontal, vertical) overload — that is
        // Avalonia's, and the two heads sit close enough together to invite the mistake.
        var tilePadding = new Thickness(
            density.TilePaddingX, density.TilePaddingY, density.TilePaddingX, density.TilePaddingY);
        TileTokensToday.TilePadding = tilePadding;
        TileEstToday.TilePadding = tilePadding;
        TileTokens31.TilePadding = tilePadding;
        TileEst31.TilePadding = tilePadding;
    }

    // ── data ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// What the panel is currently showing, so the disclosure can re-decide the density and
    /// redraw the chart without the caller handing them back. Set by every
    /// <see cref="Populate"/>, so they can never describe a different panel than the one on
    /// screen.
    /// </summary>
    private PanelStatistics? _lastStats;

    private UsageSnapshot? _lastSnapshot;

    private void Populate(UsageSnapshot snapshot, PanelStatistics stats, ClaudeAccount? account)
    {
        _lastStats = stats;
        _lastSnapshot = snapshot;
        _lastSettings = SettingsForDisplay;

        var local = TimeZoneInfo.Local;

        UpdatedText.Text = PanelText.Freshness(snapshot, Now(TimeZoneInfo.Utc), local);
        NameText.Text = account?.DisplayName ?? "account unknown";
        EmailText.Text = account?.Email ?? "";
        EmailText.Visibility = account?.Email is null ? Visibility.Collapsed : Visibility.Visible;
        // Tier from organizationType — seatTier is empty and would render blank (rule 8).
        TierText.Text = account?.Tier ?? "tier unknown";

        var authoritative = snapshot.Source is DataSource.Live or DataSource.Stale;

        // Explain a blank panel rather than leaving the user to guess (rule 6: if data is
        // unavailable, say so). Only shown when the figures are actually unavailable, and
        // worded from both reports together — a missing plan file beside working transcripts
        // is a CLI-only user, not a fault (issue #170).
        var banner = PanelBanner.Resolve(authoritative, DataReport, ScopeReport, stats.Tokens31Days);
        if (banner is not null)
        {
            NoDataTitle.Text = banner.Title;
            NoDataDetail.Text = banner.Detail;
            NoDataBanner.Visibility = Visibility.Visible;
        }
        else
        {
            NoDataBanner.Visibility = Visibility.Collapsed;
        }

        var placeholder = banner?.GaugePlaceholder ?? PanelBanner.UnknownGauge;

        PopulateBar(SessionPctText, SessionBar, SessionBarFill,
            authoritative ? snapshot.SessionPercent : null, placeholder);
        SessionResetText.Text = PanelText.SessionReset(
            snapshot.SessionResetAtUtc, Now(TimeZoneInfo.Utc), local, snapshot.SessionResetUncertainty);

        PopulateBar(WeeklyPctText, WeeklyBar, WeeklyBarFill,
            authoritative ? snapshot.WeeklyPercent : null, placeholder);
        PopulateWeeklyReset(snapshot, authoritative, local);

        PopulateTiles(stats);
        PopulateDivergence(stats);

        // Nothing recorded at all, while the plan meters show real usage: the tiles are
        // measuring a source this user does not feed, not measuring zero usage. Say which
        // source, so the 0 is interpretable instead of looking broken.
        //
        // The text is derived from what the scan actually resolved, never a literal. It
        // used to name a hard-coded %USERPROFILE%\.claude\projects and only Claude Code —
        // so a Cowork user, whose transcripts ARE read, was told their source was not
        // counted, and a packaged Desktop install was pointed at a path O-view never
        // searched (issue #58; the mistake ClaudeDataRoots exists to prevent).
        //
        // Keyed on the token total, not on RecordedDays. Those meant the same thing while
        // RecordedDays counted days with usage; now that it counts days observed (issue
        // #142), a store older than the window reports full coverage while the tiles still
        // read zero — the exact case this note is for. "The tiles show nothing" is what the
        // note is about, so it is what the condition should say.
        if (stats.Tokens31Days == 0 && authoritative && snapshot.SessionPercent is > 0)
        {
            TokenScopeNote.Text = (ScopeReport ?? TranscriptScopeReport.Inspect()).Explain();
            TokenScopeNote.Visibility = Visibility.Visible;
        }
        else
        {
            TokenScopeNote.Visibility = Visibility.Collapsed;
        }

        // BuildGraph is deferred to ShowNearTrayIcon (needs post-layout ActualWidth).
    }

    /// <summary>
    /// The weekly reset line, which mirrors the session one (issue #6, ADR-0011). Three
    /// states, and the middle one is the point of the whole feature:
    ///
    /// <list type="bullet">
    /// <item><b>Derived</b> — same shape as the session line, with the weekday added
    /// because a reset a week out needs one. A reset that was observed while Claude
    /// Desktop was closed is only bracketed to within hours, so its time is prefixed
    /// <c>~</c> and the hover says how wide the bracket is: showing an hour and a minute
    /// we do not have would be a fabricated number (rule 6).</item>
    /// <item><b>Waiting</b> — plan data is flowing but no `sd` drop has been seen yet.
    /// This used to render as nothing at all, which is indistinguishable from a bug; it
    /// now says what O-view is waiting for and the hover says how long that takes.</item>
    /// <item><b>No plan data</b> — nothing to wait for yet, and the no-data banner above
    /// already explains why, so the line is hidden rather than repeating it.</item>
    /// </list>
    /// </summary>
    private void PopulateWeeklyReset(UsageSnapshot snapshot, bool authoritative, TimeZoneInfo local)
    {
        if (snapshot.WeeklyResetAtUtc is { } reset)
        {
            var bracket = snapshot.WeeklyResetUncertainty ?? TimeSpan.Zero;

            // A user-supplied reset is exact, so it carries no "~" — and it is labelled, so
            // an unexpectedly precise time is explained rather than mysterious (issue #186).
            var userSupplied = _lastSettings?.WeeklyReset is not null && bracket == TimeSpan.Zero;

            WeeklyResetText.Text = PanelText.WeeklyReset(
                reset, snapshot.WeeklyResetUncertainty, Now(TimeZoneInfo.Utc), local)
                + (userSupplied ? $" · {PanelText.WeeklyResetUserSupplied}" : "");
            WeeklyResetText.ToolTip = userSupplied
                ? HoverCard.Text(this, PanelText.WeeklyResetUserSuppliedHint)
                : PanelText.IsApproximate(snapshot.WeeklyResetUncertainty)
                    ? HoverCard.Text(this, PanelText.WeeklyResetApproximateHint(bracket))
                    : null;
            HoverCard.ApplyTiming(WeeklyResetText);
            WeeklyResetText.Visibility = Visibility.Visible;
            return;
        }

        WeeklyResetText.Text = PanelText.WeeklyResetWaiting;
        WeeklyResetText.ToolTip = HoverCard.Text(this, PanelText.WeeklyResetWaitingHint);
        HoverCard.ApplyTiming(WeeklyResetText);
        WeeklyResetText.Visibility = authoritative ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Fills the four statistics tiles, each with the per-model split behind its total
    /// (issue #37). The split is passed in, not fetched: it is already on the
    /// PanelStatistics the panel was opened with, so a click costs no I/O.
    ///
    /// Both today tiles share ModelsToday and both 31-day tiles share Models31Days —
    /// one list per window, split two ways, because a token breakdown and a value
    /// breakdown of the same usage are the same rows measured differently.
    /// </summary>
    private void PopulateTiles(PanelStatistics stats)
    {
        // Partial history states its coverage — a small number without this caveat
        // reads as low usage rather than short history (ADR-0006). An unpriced model is
        // the same class of caveat: the total is real but incomplete, so say which model
        // is missing rather than letting the figure read as the whole picture.
        var coverage = PanelText.Caveat(stats);

        // Always shown, unlike the caveat above it. Partial history and an unpriced model are
        // conditions that pass; the tiles excluding chat and cloud sessions is the standing shape
        // of the data, and a note that appeared only sometimes would teach a reader that its
        // absence means full coverage (issue #235).
        TokenScopeLine.Text = PanelText.TokenScopeCaveat;

        // The "not money charged" framing is only true for plan usage. Off-plan work
        // bills at API rates, so the label has to flip with it.
        var offPlan = stats.IsOffPlan;

        // The "today" tiles carried a "(UTC)" hint for one release, while the figure was a UTC
        // day under a local-time header (issue #210). The figure is the reader's own day now,
        // so there is nothing left to qualify.
        TileTokensToday.FormatSlice = s => FormatTokens(s.Tokens);
        TileTokensToday.Populate(
            PanelText.TokensTodayLabel, FormatTokens(stats.TokensToday), "",
            stats.ModelsToday, BreakdownMeasure.Tokens, stats.ModelColourOrder);

        TileEstToday.FormatSlice = s => FormatUsd(s.EstUsd);
        TileEstToday.Populate(
            PanelText.EstTodayLabel(offPlan),
            FormatUsd(stats.EstTodayUsd),
            PanelText.OffPlanNote(offPlan),
            stats.ModelsToday, BreakdownMeasure.EstValue, stats.ModelColourOrder);

        TileTokens31.FormatSlice = s => FormatTokens(s.Tokens);
        TileTokens31.Populate(
            PanelText.Tokens31DaysLabel, FormatTokens(stats.Tokens31Days), coverage,
            stats.Models31Days, BreakdownMeasure.Tokens, stats.ModelColourOrder);

        TileEst31.FormatSlice = s => FormatUsd(s.EstUsd);
        TileEst31.Populate(
            PanelText.Est31DaysLabel, FormatUsd(stats.Est31DaysUsd), coverage,
            stats.Models31Days, BreakdownMeasure.EstValue, stats.ModelColourOrder);

        // Which surfaces these figures are made of. Empty when nothing was found at all —
        // the scope note owns that state and says considerably more (issue #171).
        PopulateComposition(
            stats.CompositionToday,
            (ScopeReport ?? TranscriptScopeReport.Inspect()).CoverageLine());
    }

    /// <summary>
    /// The four-way split behind today's token total, and why it dwarfs the context figure
    /// in Claude's own UI (issue #169).
    ///
    /// <para>Hidden rather than shown empty when there is nothing to break down: a
    /// composition of zero explains nothing, and the scope note that appears in that state
    /// is already saying the useful thing.</para>
    ///
    /// <para>The figures stay on screen; the prose folds behind a disclosure. It was four
    /// lines of standing text that every user read on every open, and it answers a question
    /// only some of them have — but the ones who do have it are looking straight at the
    /// number that prompted it, so the answer stays one click away rather than moving to a
    /// document nobody opens.</para>
    ///
    /// <para>Collapsed on every Populate, deliberately: the panel is a transient view, not a
    /// setting, and StatTile resets its own expansion for the same reason.</para>
    /// </summary>
    /// <summary>
    /// Sets a line's text and hides it when there is none. The empty string is the signal —
    /// every <c>PanelText</c> member that can have nothing to say returns one, so the decision
    /// lives with the text rather than being re-derived from the statistics at each call site.
    /// A visible but empty <see cref="TextBlock"/> leaves a gap that reads as a figure which
    /// failed to load.
    /// </summary>
    private static void SetLine(TextBlock line, string text)
    {
        line.Text = text;
        line.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PopulateComposition(TokenComposition composition, string coverageLine)
    {
        var show = composition.HasTokens;
        TokenCompositionLine.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        TokenExplainToggle.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        if (!show)
        {
            TokenExplainBody.Visibility = Visibility.Collapsed;
            return;
        }

        TokenCompositionLine.Text = PanelText.TokenCompositionLine(
            composition, PanelText.TokenCompositionTodayScope);
        TokenCompositionHint.Text = PanelText.TokenCompositionHint(composition);
        TokenExplainLabel.Text = PanelText.TokenExplainToggleLabel;

        TokenCoverageLine.Text = coverageLine;
        TokenCoverageLine.Visibility = coverageLine.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        SetCompositionExpanded(false);
    }

    /// <summary>
    /// Puts the explanation in a finished state at once — open or closed, nothing in flight.
    /// The interactive path goes through <see cref="ToggleComposition"/>, which folds; this
    /// is what <c>Populate</c> and the verification renders use.
    /// </summary>
    private void SetCompositionExpanded(bool expanded)
    {
        _compositionExpanded = expanded;
        _disclosure.Apply(expanded);
    }

    private bool _compositionExpanded;

    private readonly DisclosureAnimation _disclosure;

    /// <summary>
    /// Surfaces off-plan usage in two distinct registers:
    ///   • a live banner + tile relabel for what is happening in the CURRENT session
    ///     window (the divergence detector's real-time signal), and
    ///   • a standing 31-day off-plan SPEND total (GitHub issue #3), estimated from
    ///     credit-billed models rather than the 5-hour session.
    /// The two are independent: the 31-day figure shows even when the current window
    /// is on-plan, and the banner shows even before any credit spend has accrued.
    /// </summary>
    private void PopulateDivergence(PanelStatistics stats)
    {
        var d = stats.Divergence;
        var offPlan = stats.IsOffPlan;

        // ── live, session-scoped signal ──────────────────────────────────────────
        DivergenceBanner.Visibility = offPlan ? Visibility.Visible : Visibility.Collapsed;

        // The Est.-today tile's label and note also flip with off-plan state; that now
        // happens in PopulateTiles, which owns everything the tiles show.

        if (offPlan && d is not null)
        {
            var limitReached = d.State == DivergenceState.PlanLimitReached;
            DivergenceTitle.Text = limitReached
                ? "Plan limit reached — usage is billing beyond your plan"
                : "This session's usage is not drawing from your plan";
            DivergenceDetail.Text = limitReached
                ? PanelText.PlanLimitReachedDetail
                : PanelText.DivergenceDetail(d.OutputTokensInWindow, d.PlanRisePoints);
        }

        // ── standing 31-day off-plan spend (issue #3) ────────────────────────────
        if (stats.HasCreditUsage)
        {
            CreditsBadgeText.Text = "credit-billed";
            CreditsSpendLabel.Text = "Est. credit spend";
            CreditsSpendValue.Text = FormatUsd(stats.EstCredit31DaysUsd);
            CreditsCoverage.Text = stats.CoverageNote;
        }
        else
        {
            CreditsBadgeText.Text = "none recorded";
            CreditsSpendLabel.Text = "Est. credit spend";
            CreditsSpendValue.Text = "$0.00";
            CreditsCoverage.Text = "";
        }

        // The explanation is on hover now rather than standing under the figure (issue #181).
        // Both wordings still carry the rule-6 caveat that this is not what was charged —
        // moving a caveat behind a hover is the obvious way to lose one, so the sentences
        // live in PanelText where they have one definition and a test can assert them.
        CreditsSection.ToolTip = HoverCard.Text(this, PanelText.OffPlanHint(stats.HasCreditUsage));
        HoverCard.ApplyTiming(CreditsSection);
    }

    private void PopulateBar(TextBlock pctText, Grid bar, Border fill, int? percent, string placeholder)
    {
        System.Windows.Data.BindingOperations.ClearBinding(fill, WidthProperty);

        if (percent is { } p)
        {
            pctText.Text = string.Create(CultureInfo.InvariantCulture, $"{p}% used");
            // Shared colour bands (UsageLevels) so the popup and tray icon agree.
            fill.Background = new SolidColorBrush(UsageLevels.Classify(p) switch
            {
                UsageLevel.Critical => Red,
                UsageLevel.Warning => Amber,
                _ => Green,
            });
            // Fill width tracks the bar's laid-out width × percent.
            fill.SetBinding(WidthProperty, new System.Windows.Data.Binding(nameof(bar.ActualWidth))
            {
                Source = bar,
                Converter = new PercentWidthConverter(),
                ConverterParameter = Math.Clamp(p, 0, 100),
            });
        }
        else
        {
            pctText.Text = placeholder;
            fill.Width = 0;
        }
    }

    // Blue gradient endpoints for within-week intensity (issue #5): light → dark.
    private static readonly Color GraphBlueLo = Color.FromRgb(176, 208, 240);
    private static readonly Color GraphBlueHi = Color.FromRgb(24, 95, 165);

    /// <summary>
    /// Draws the 31-day usage chart on the Canvas (issues #4, #5): one bar per day,
    /// coloured light→dark blue by its intensity WITHIN its calendar week, dotted
    /// gridlines at Monday boundaries, vertical date labels, and a per-bar hover
    /// tooltip. Pre-install days are blank columns (no bar) — with the date axis, an
    /// empty column reads as "no data" without needing the old caption.
    ///
    /// Bar and label are both placed from <see cref="BarCentre"/> (issue #31) so a date
    /// always sits under the bar it describes; with 31 columns in ~344 px a few pixels
    /// of drift is enough to make a label read against its neighbour.
    /// </summary>
    private void BuildGraph(PanelStatistics stats, UsageSnapshot snapshot)
    {
        GraphHost.Children.Clear();

        var series = stats.DailySeries;
        if (series.Count == 0)
        {
            return;
        }

        var width = GraphHost.ActualWidth > 0 ? GraphHost.ActualWidth : 344;

        // Derived from the canvas rather than fixed at 52. The canvas height is no longer a
        // constant — a display too short for the panel gets a shorter one (PanelDensity) —
        // and a fixed bar area meant the rotated date labels below it simply ran off the
        // bottom and collided with the next section. Caught by rendering it; the constants
        // alone look fine. At the normal 86 px canvas this is 52, exactly as before.
        const double labelAreaHeight = 34;
        var barAreaHeight = Math.Max(18, GraphHost.Height - labelAreaHeight);
        var labelTop = barAreaHeight + 6;
        var col = width / series.Count;
        var globalMax = Math.Max(1, series.Max(d => d.TotalTokens));

        // The columns are LOCAL days (issue #211), so everything drawn over them has to be
        // placed in the same frame: a gridline positioned as though every column were 24 UTC
        // hours drifts against the bar it annotates from the DST change onwards.
        var weeks = PlanWeeks.ForSeries(series, snapshot, TimeZoneInfo.Local);

        // Per-week peak, so colour intensity is normalised within each week. The week is
        // the PLAN's week wherever it is known, so the shading and the gridlines below
        // always describe the same bands — they used to be able to disagree.
        var weekMax = series
            .Where(d => !d.PreInstall)
            .GroupBy(weeks.IndexOf)
            .ToDictionary(g => g.Key, g => Math.Max(1, g.Max(d => d.TotalTokens)));

        var labelBrush = (Brush)FindResource("TextMuted");

        for (var i = 0; i < series.Count; i++)
        {
            var day = series[i];
            var x = i * col;

            // Bar — height by absolute tokens, colour by within-week intensity.
            if (!day.PreInstall && day.TotalTokens > 0)
            {
                var intensity = day.TotalTokens / (double)weekMax[weeks.IndexOf(day)];
                var height = Math.Max(2, barAreaHeight * day.TotalTokens / globalMax);
                var barWidth = Math.Max(2, col - 2);
                var bar = new Rectangle
                {
                    Width = barWidth,
                    Height = height,
                    Fill = new SolidColorBrush(Lerp(GraphBlueLo, GraphBlueHi, intensity)),
                    RadiusX = 1,
                    RadiusY = 1,
                    // Figure first, date beneath — the panel's shared hover card, not the
                    // raw string this used to be. A bare `ToolTip = "…"` renders as system
                    // chrome: a pale rectangle with a hard border, on a panel that is
                    // otherwise entirely rounded cards on one palette.
                    ToolTip = HoverCard.Figure(
                        this,
                        $"{FormatTokens(day.TotalTokens)} tokens",
                        day.Date.ToString("dddd d MMMM", CultureInfo.InvariantCulture)),
                };
                HoverCard.ApplyTiming(bar);
                // Placed from the column centre, like the label below it, so the two
                // cannot drift apart as the gutter or column width changes (issue #31).
                Canvas.SetLeft(bar, BarCentre(x, col) - barWidth / 2);
                Canvas.SetTop(bar, barAreaHeight - height);
                GraphHost.Children.Add(bar);
            }

            // Vertical date label under every column (rotated, small but legible),
            // centred on its own bar (issue #31).
            //
            // A RenderTransform does not move the element's LAYOUT box, so Canvas.SetLeft
            // still positions the un-rotated text while the ink lands somewhere else
            // entirely: at 90° the box [0,w]×[0,h] maps to [-h,0]×[0,w], i.e. the label
            // hangs to the LEFT of its anchor by one line height. The old code
            // compensated with a constant +3, which stood in for half a line and was ~2 px
            // short of an 8 pt one — a fifth of a column at 31 days, and what made the
            // dates read as offset from their bars.
            //
            // Ask the transform for the rendered bounds rather than re-deriving them: the
            // line height moves with font, DPI and the OS text-scaling setting, and this
            // stays correct if the angle is ever changed.
            var rotation = new RotateTransform(90);
            var label = new TextBlock
            {
                Text = day.Date.ToString("d MMM", CultureInfo.InvariantCulture),
                FontSize = 8,
                Foreground = labelBrush,
                RenderTransform = rotation,
                Opacity = day.PreInstall ? 0.5 : 1.0,
            };
            // Parent first, so the font properties measured are the inherited ones.
            GraphHost.Children.Add(label);
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var ink = rotation.TransformBounds(new Rect(label.DesiredSize));
            Canvas.SetLeft(label, BarCentre(x, col) - ink.Left - ink.Width / 2);
            Canvas.SetTop(label, labelTop - ink.Top);
        }

        DrawWeekBoundaries(weeks, series, col, barAreaHeight);
    }

    /// <summary>
    /// The week-boundary gridlines, drawn <em>last</em> so they sit above the bars.
    ///
    /// <para>A Canvas paints in child order, so these used to be added first and were
    /// overdrawn by every bar they crossed — which is most of them in a busy week, leaving
    /// the boundary visible only in the gaps. A reset line that disappears exactly where
    /// the usage is heaviest is missing the moment it exists to mark.</para>
    ///
    /// <para>They wear the panel's note colour (<c>WarnText</c>), the same amber the
    /// coverage and caveat lines use, rather than the muted grey they shared with the date
    /// labels: this is an annotation over the data, not another axis decoration, and grey
    /// on blue at one pixel was not carrying that.</para>
    ///
    /// <para>Placement is unchanged. A plan reset happens at an instant, not at midnight,
    /// so the line sits at its true fractional position inside the day it falls in —
    /// snapping to the nearest column edge would claim a boundary the data does not have.
    /// The calendar-week fallback lands on a column edge because midnight is one.</para>
    /// </summary>
    private void DrawWeekBoundaries(
        PlanWeeks weeks, IReadOnlyList<DayUsage> series, double col, double barAreaHeight)
    {
        var gridBrush = (Brush)FindResource("WarnText");
        var columnOf = series
            .Select((day, index) => (day.Date, index))
            .ToDictionary(t => t.Date, t => t.index);

        foreach (var boundary in weeks.Boundaries)
        {
            if (!columnOf.TryGetValue(boundary.Day, out var index))
            {
                continue;   // outside the plotted range
            }

            var x = (index + boundary.FractionOfDay) * col;
            if (x <= 0)
            {
                continue;   // exactly on the left edge, where a line reads as a border
            }

            var line = new Line
            {
                X1 = x, X2 = x, Y1 = 0, Y2 = barAreaHeight + 3,
                Stroke = gridBrush,
                StrokeThickness = 1,
                StrokeDashArray = [2, 2],
                ToolTip = weeks.IsPlanDerived
                    ? HoverCard.Figure(
                        this,
                        TimeZoneInfo.ConvertTime(boundary.AtUtc, TimeZoneInfo.Local)
                            .ToString("ddd d MMM · HH:mm", CultureInfo.InvariantCulture),
                        "Weekly limit reset")
                    : HoverCard.Text(
                        this,
                        "Start of the calendar week (Monday). O-view hasn't observed a weekly "
                        + "reset yet, so this is a calendar reference, not your plan's boundary."),
            };
            HoverCard.ApplyTiming(line);
            GraphHost.Children.Add(line);
        }
    }

    /// <summary>
    /// Horizontal centre of the column starting at <paramref name="x"/>. The single
    /// anchor the bar and its date label are both placed from (issue #31) — the label
    /// was previously offset by a constant that no longer matched the bar.
    /// </summary>
    private static double BarCentre(double x, double col) => x + col / 2;

    /// <summary>
    /// Where one week boundary lands on the chart: the day column it falls in, and how far
    /// through that column, since a plan reset happens at a time of day rather than at
    /// midnight.
    ///
    /// <para>Both answers are taken in the column's own frame — a <b>local</b> day, of
    /// whatever length the timezone says it was (issue #211). Dividing by a flat 24 hours
    /// would put the line up to an hour off its true position on the two DST days, and off in
    /// the same direction for every day after them if the day boundaries were stepped that way
    /// too.</para>
    /// </summary>
    private readonly record struct WeekBoundary(DateTimeOffset AtUtc, TimeZoneInfo Zone)
    {
        public DateOnly Day => LocalDays.DateOf(AtUtc, Zone);
        public double FractionOfDay => LocalDays.FractionThrough(AtUtc, Day, Zone);
    }

    /// <summary>
    /// The week bands the 31-day graph is divided into.
    ///
    /// <para>Originally these were calendar weeks (Mon–Sun) with a stated reason: the plan's
    /// true weekly boundary was not derivable, so Monday was an honest visual reference
    /// rather than a claim about the plan. That premise no longer holds — ADR-0011 derives
    /// the weekly reset — so the bands now follow the plan's own boundary wherever it is
    /// known, which is what the graph was always trying to convey. Mondays remain the
    /// fallback, and say so on hover, because until a reset has been observed the real
    /// boundary is still genuinely unknown (rule 6).</para>
    ///
    /// <para>Boundaries are derived by stepping the cadence back from the predicted next
    /// reset, so they cover the whole window including days before O-view was installed —
    /// the log only holds resets it was running for.</para>
    /// </summary>
    private sealed class PlanWeeks
    {
        private readonly IReadOnlyList<DateTimeOffset> _boundaries;
        private readonly TimeZoneInfo _zone;

        private PlanWeeks(IReadOnlyList<DateTimeOffset> boundaries, bool isPlanDerived, TimeZoneInfo zone)
        {
            _boundaries = boundaries;
            _zone = zone;
            IsPlanDerived = isPlanDerived;
        }

        /// <summary>False when these are calendar weeks standing in for an unknown plan week.</summary>
        public bool IsPlanDerived { get; }

        public IEnumerable<WeekBoundary> Boundaries => _boundaries.Select(b => new WeekBoundary(b, _zone));

        public static PlanWeeks ForSeries(
            IReadOnlyList<DayUsage> series, UsageSnapshot snapshot, TimeZoneInfo zone)
        {
            // The span the columns actually cover, taken from the timezone. The columns are
            // local days now (issue #211), so their first and last instants are not local
            // midnight interpreted as UTC — which is what these two lines used to say, and
            // what would clip or over-collect boundaries by the offset.
            var fromUtc = LocalDays.StartUtc(series[0].Date, zone);
            var toUtc = LocalDays.EndUtc(series[^1].Date, zone);

            if (snapshot.WeeklyResetAtUtc is { } next && snapshot.WeeklyResetPeriod is { } period)
            {
                return new PlanWeeks(
                    WeeklyWindow.BoundariesWithin(next, period, fromUtc, toUtc),
                    isPlanDerived: true,
                    zone);
            }

            // Fallback: the start of each local Monday in range.
            var mondays = series
                .Select(d => d.Date)
                .Where(d => d.DayOfWeek == DayOfWeek.Monday)
                .Select(d => LocalDays.StartUtc(d, zone))
                .ToList();
            return new PlanWeeks(mondays, isPlanDerived: false, zone);
        }

        /// <summary>
        /// Which band a day belongs to, for the purpose of normalising its colour.
        ///
        /// <para>A day that contains a boundary genuinely belongs to neither band — its
        /// tokens are split across two weeks and the rollups are daily, so the split is not
        /// recoverable. It is assigned to whichever band holds the majority of the day,
        /// which is what testing the MIDPOINT does. That is an approximation, and the only
        /// one available at this grain; it affects shading alone, never a stated figure.</para>
        /// </summary>
        public int IndexOf(DayUsage day)
        {
            var start = LocalDays.StartUtc(day.Date, _zone);
            var midday = start + (LocalDays.EndUtc(day.Date, _zone) - start) / 2;
            return _boundaries.Count(boundary => boundary <= midday);
        }
    }

    private static Color Lerp(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    // ── formatting (display edge) ──────────────────────────────────────────────

    private static DateTimeOffset Now(TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);

    // The header's freshness line lives in OView.Core.Models.PanelText, shared with the Linux
    // panel: it is a statement about how old the figures are, and two panels wording that
    // differently is the failure PanelText exists to prevent (issue #192).

    /// <summary>
    /// Coarsest-two-units countdown. The days branch exists for the weekly window: a
    /// 7-day reset rendered in the hours format reads "163h 45m", which is technically
    /// right and useless.
    /// </summary>
    // Duration wording lives in OView.Core.Models.PanelText, shared with the Linux panel so
    // the two cannot describe the same reset differently. Kept as a local alias because it
    // reads better unqualified at its call sites — the same reason FormatTokens exists.
    private static string FormatCountdown(TimeSpan t) => PanelText.Countdown(t);

    // Token and money formatting live in OView.Core.Models.UsageFormatter, shared with the
    // off-plan notification and the verification renders so one amount cannot be written
    // two ways (issue #55). Kept as local aliases because they read better unqualified at
    // the dozen call sites above.
    private static string FormatTokens(long tokens) => UsageFormatter.Tokens(tokens);

    private static string FormatUsd(decimal? usd) => UsageFormatter.Usd(usd);

    // Theming lives in PanelTheme, shared with the tray menu flyout (issue #33).
}

/// <summary>Bar fill width = track ActualWidth × percent.</summary>
internal sealed class PercentWidthConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double actual && parameter is int percent ? Math.Max(percent == 0 ? 0.0 : 4.0, actual * percent / 100.0) : 0.0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
