using System.Windows;
using System.Windows.Input;
using OView.App.Rendering;
using OView.Core.Models;
using OView.Core.Providers.CachedUsage;
using OView.Core.Providers.Jsonl;
using OView.Core.Providers.PlanHistory;
using OView.Presentation.Panel;
using OView.Presentation.Text;
using ToolTip = System.Windows.Controls.ToolTip;

namespace OView.Tray.Popup;

/// <summary>
/// The detail panel (ui-spec.md). Times convert to local here, at the display edge.
/// Dismisses on Esc and on deactivation (click-outside). Theme follows
/// AppsUseLightTheme — the app-window setting, distinct from the taskbar's — and is
/// re-read on every open so a theme switch never needs a restart.
///
/// <para>What the panel draws lives in one piece per thing drawn —
/// <see cref="PopupTiles"/>, <see cref="PopupWeeklyRow"/>, <see cref="PopupMeter"/>,
/// <see cref="PopupDivergence"/>, <see cref="PopupGraph"/> and
/// <see cref="PopupTokenComposition"/> — each reading exactly the section view model
/// <see cref="PanelSections"/> already built, and placing it. This file is left with
/// the window's own lifecycle, layout and verification hooks.</para>
/// </summary>
public partial class PopupWindow : Window, IFlyout
{
    /// <summary>Forces a theme for verification screenshots; null follows the OS.</summary>
    public bool? ThemeOverride { get; set; }

    /// <summary>
    /// Pins the clock for verification renders; null reads the real one. Golden images are
    /// compared pixel for pixel, so a countdown that ticked between two renders would fail
    /// every comparison.
    /// </summary>
    internal DateTimeOffset? FixedNow { get; set; }

    /// <summary>
    /// Pins the time zone for verification renders; null follows the machine. Without it a
    /// UTC CI runner and a CET desktop render different reset times for the same sample.
    /// </summary>
    internal TimeZoneInfo? FixedZone { get; set; }

    private TimeZoneInfo LocalZone => FixedZone ?? TimeZoneInfo.Local;

    /// <summary>
    /// Docking, the open/close transitions and the toggle grace window — shared with the
    /// tray menu rather than reimplemented here (issue #54).
    /// </summary>
    private readonly DockedFlyout _flyout;

    private readonly PopupTiles _tiles;
    private readonly PopupWeeklyRow _weeklyRow;
    private readonly PopupDivergence _divergence;
    private readonly PopupGraph _graph;
    private readonly PopupTokenComposition _composition;

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

        _tiles = new PopupTiles(TokenScopeLine, TileTokensToday, TileEstToday, TileTokens31, TileEst31, TokenScopeNote);
        _weeklyRow = new PopupWeeklyRow(this, WeeklyResetText, BoostChip, BoostChipText);
        _divergence = new PopupDivergence(
            this, DivergenceBanner, DivergenceTitle, DivergenceDetail, DivergenceLinkAnchor,
            CreditsHeading, CreditsBadgeText, CreditsSpendLabel, CreditsSpendValue, CreditsCoverage, CreditsSection);
        _graph = new PopupGraph(this, GraphHost);

        var disclosure = new DisclosureAnimation(
            // No chevron: the Bars/Breakdown switch shows its state by which segment is lit
            // (issue #253), and an arrow beside it would be a second indicator of one thing.
            this, TokenBreakdownView, null,
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
        _composition = new PopupTokenComposition(
            this, disclosure, TokenViewBars, TokenViewBreakdown, TokenBarsView, TokenBreakdownView,
            TokensUsedSection, TokensUsedHeading, TokenCoverageLine,
            RefitForPendingGrowth, () => _flyout.Redock(ActualWidth, DockHeight));
    }

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
    internal double DisclosureScreenTop => _composition.ScreenTop;

    /// <summary>
    /// The folding body's current height — what <c>--fold-check</c> samples.
    /// </summary>
    internal double DisclosureHeight => _composition.Height;

    /// <summary>
    /// Opens the breakdown for a verification render. The view resets to Bars on every
    /// Populate, so a sample of the expanded state cannot be produced any other way — and an
    /// unrendered state is how the no-data banner spent its whole life saying the wrong
    /// thing (issues #58, #170).
    /// </summary>
    internal void ExpandCompositionForVerification() => _composition.ExpandForVerification();

    /// <summary>
    /// Folds the breakdown from outside, for <c>--fold-check</c>. The real transition,
    /// not the instant state: an animation's completion is the thing being checked, and this
    /// app has shipped a missed one before (<see cref="FlyoutAnimation"/>).
    /// </summary>
    internal void ToggleCompositionForVerification() => _composition.ToggleForVerification();

    /// <summary>
    /// Freezes the disclosure partway open for a verification render. On a real desktop this
    /// state exists for about a tenth of a second, which is long enough to look wrong and far
    /// too short to inspect — and the failure it guards against, content drawn outside the
    /// height it has been given, is invisible at both ends of the fold.
    /// </summary>
    /// <param name="fraction">How far open, 0–1.</param>
    internal void RevealCompositionForVerification(double fraction) => _composition.RevealForVerification(fraction);

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

    /// <summary>
    /// The promo notices Claude Code has cached, for the boost chip on the weekly row
    /// (issue #254). Null means none were read, which is the ordinary state and renders nothing.
    ///
    /// <para>Set on open beside <see cref="DataReport"/> rather than carried on the snapshot:
    /// a promo notice is not a meter reading, and putting it on <c>UsageSnapshot</c> would
    /// oblige every provider to carry something only this file has.</para>
    /// </summary>
    public BoostNotices? BoostNotices { get; set; }

    /// <summary>
    /// Claude Code's cached usage block, for the one thing the snapshot does not carry: whether
    /// extra usage is switched on for this account, and when that was last read (issue #259).
    ///
    /// <para>Set on open from the engine, which owns the read. Null renders the banner's
    /// unknown wording — the same thing a machine with no Claude Code gets, and correctly so.
    /// </para>
    /// </summary>
    public CachedUtilization? CachedUsage { get; set; }

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
    /// (<see cref="PopupTokenComposition"/>). Zero everywhere else, where what is on screen and
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

        _graph.Draw(stats, snapshot, LocalZone);
    }

    /// <summary>
    /// Re-fits the panel for height the composition disclosure is about to gain or lose, if
    /// the panel has been populated yet. The disclosure measures before it plays, so the
    /// window is never laid out at the far end of the fold (<see cref="PopupTokenComposition"/>).
    /// </summary>
    internal void RefitForPendingGrowth(double pendingGrowth)
    {
        if (_lastStats is { } stats && _lastSnapshot is { } snapshot)
        {
            FitToWorkArea(stats, snapshot, pendingGrowth);
        }
    }

    /// <summary>
    /// Renders the panel to a bitmap without showing it — the detail panel's equivalent of
    /// <see cref="MenuWindow.RenderToBitmap"/>, driving the same <see cref="Populate"/> and
    /// <see cref="FitToWorkArea"/> the live path does.
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
    /// drawing the graph. Drawing the graph before the first pass silently
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

            _graph.Draw(stats, snapshot, LocalZone);
        });
    }

    /// <summary>
    /// The graph's hover cards, built standalone for verification. They cannot appear in a
    /// render of the panel — a <c>ToolTip</c> throws if given a parent, so it can neither be
    /// laid out inside one nor screenshotted with it — and hover is the only way to reach
    /// them in the running app, so without this they are unverifiable.
    ///
    /// <para>The wording comes from the same place the live cards read it from, or the render
    /// would be proof of copy that does not ship. Only the figures are stand-ins.</para>
    /// </summary>
    internal IReadOnlyList<ToolTip> BuildSampleHoverCards(bool light)
    {
        PanelTheme.Apply(Resources, light);
        return
        [
            HoverCard.Figure(this, TokenText.DayBarTokens(234_800_000), "Tuesday 21 July"),
            HoverCard.Figure(this, "Tue 28 Jul · 06:28", PanelText.WeeklyResetMarker),
            HoverCard.Text(this, PanelText.CalendarWeekMarker),
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

        var local = LocalZone;
        var utcNow = Now(TimeZoneInfo.Utc);

        var header = PanelSections.Header(snapshot, account, utcNow, local);
        UpdatedText.Text = header.Freshness;
        NameText.Text = header.AccountName;
        EmailText.Text = header.Email ?? "";
        EmailText.Visibility = header.Email is null ? Visibility.Collapsed : Visibility.Visible;
        TierText.Text = header.Tier;

        var authoritative = PanelSections.IsAuthoritative(snapshot);

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

        var session = PanelSections.SessionMeter(snapshot, banner, utcNow, local);
        PopupMeter.Populate(SessionPctText, SessionBar, SessionBarFill, session);
        // The session line always has something to say — the unknown state is a sentence of its
        // own, not an absence — so SetLine's empty case is unreachable here.
        PopupText.SetLine(SessionResetText, session.ResetLine ?? "");

        var weekly = PanelSections.WeeklyMeter(
            snapshot, banner, _lastSettings, BoostNotices, utcNow, local);
        PopupMeter.Populate(WeeklyPctText, WeeklyBar, WeeklyBarFill, weekly);
        _weeklyRow.Populate(weekly);

        _tiles.Populate(stats, authoritative, snapshot.SessionPercent is > 0, ScopeReport);
        _composition.Populate(
            stats.CompositionToday, stats.Composition31Days,
            (ScopeReport ?? TranscriptScopeReport.Inspect()).CoverageLine());
        _divergence.Populate(stats, CachedUsage, utcNow, local);

        // Set here rather than in the markup so the day count is the window the bars below it
        // actually cover, which is the same thing the Linux panel's heading reads.
        GraphHeading.Text = PanelText.UsageWindowHeading(stats.WindowDays);

        // BuildGraph is deferred to ShowNearTrayIcon (needs post-layout ActualWidth).
    }

    /// <summary>
    /// Opens the off-plan banner's link in the user's browser (issue #259).
    ///
    /// <para><c>UseShellExecute</c> because the target is a URL rather than an executable —
    /// the same call <see cref="OView.Tray.Updates.UpdateService.OpenInBrowser"/> makes, and
    /// the only form that hands a URL to the default handler rather than trying to run it.</para>
    ///
    /// <para>Swallows its failures deliberately. A machine with no registered browser handler
    /// throws here, and the panel losing a link is a smaller cost than the panel closing with
    /// an unhandled exception while the reader is looking at a warning.</para>
    /// </summary>
    private void OnUsageSettingsNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        e.Handled = true;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
            // No handler for https, or the shell refused. Nothing useful to say in a panel.
        }
    }

    // ── formatting (display edge) ──────────────────────────────────────────────

    private DateTimeOffset Now(TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(FixedNow ?? DateTimeOffset.UtcNow, zone);

    // The header's freshness line lives in OView.Presentation.Text.PanelText, shared with the
    // Linux panel: it is a statement about how old the figures are, and two panels wording that
    // differently is the failure PanelText exists to prevent (issue #192).

    /// <summary>
    /// Coarsest-two-units countdown. The days branch exists for the weekly window: a
    /// 7-day reset rendered in the hours format reads "163h 45m", which is technically
    /// right and useless.
    /// </summary>
    // Duration wording lives in OView.Presentation.Text.PanelText, shared with the Linux panel so
    // the two cannot describe the same reset differently. Kept as a local alias because it
    // reads better unqualified at its call sites.
    private static string FormatCountdown(TimeSpan t) => PanelText.Countdown(t);

    // Theming lives in PanelTheme, shared with the tray menu flyout (issue #33).
}
