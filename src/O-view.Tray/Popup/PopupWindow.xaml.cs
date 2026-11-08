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
/// AppsUseLightTheme, the app-window setting, distinct from the taskbar's, and is
/// re-read on every open so a theme switch never needs a restart.
///
/// <para>What the panel draws lives in one piece per thing drawn,
/// <see cref="PopupPlanBlock"/>, <see cref="PopupWeeklyRow"/>,
/// <see cref="PopupMeter"/>, <see cref="PopupModelMeters"/>, <see cref="PopupDivergence"/>
/// and <see cref="PopupLocalBlock"/>, each reading exactly the section view model
/// <see cref="PanelSections"/> already built, and placing it. This file is left with
/// the window's own lifecycle, layout and verification hooks.</para>
/// </summary>
public partial class PopupWindow : Window, IFlyout
{
    /// <summary>Forces a theme for verification screenshots; null follows the OS.</summary>
    public bool? ThemeOverride { get; set; }

    /// <summary>The theme the panel was last laid out in, for the parts that paint from the shared palette in code.</summary>
    private bool _light;

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
    /// Docking, the open/close transitions and the toggle grace window, shared with the
    /// tray menu rather than reimplemented here (issue #54).
    /// </summary>
    private readonly DockedFlyout _flyout;

    /// <summary>
    /// The viewport the sections move into on a display too short for them, so nothing past
    /// the cap is left unreachable (<see cref="PopupScroll"/>).
    /// </summary>
    private readonly PopupScroll _scroll;

    /// <summary>
    /// The window's height-fitting block, the density, the work-area cap and the scroll
    /// decision (<see cref="PopupFit"/>).
    /// </summary>
    private readonly PopupFit _fit;

    private readonly PopupPlanBlock _planBlock;
    private readonly PopupWeeklyRow _weeklyRow;
    private readonly PopupModelMeters _modelMeters;
    private readonly PopupDivergence _divergence;
    private readonly PopupLocalBlock _localBlock;

    /// <summary>Disables auto-hide for verification screenshots.</summary>
    public bool PinForVerification
    {
        get => _flyout.PinForVerification;
        set => _flyout.PinForVerification = value;
    }

    /// <inheritdoc cref="DockedFlyout.ClosedByClickAway"/>
    public bool ClosedByClickAway => _flyout.ClosedByClickAway;

    /// <summary>
    /// Where the user last dragged the panel on each display. Null is the honest default
    /// for a panel with nowhere to remember to, the offscreen verification renders, which place
    /// no window at all, and leaves the panel opening at the tray corner exactly as it did
    /// before dragging existed.
    /// </summary>
    public PanelPlacementMemory? PlacementMemory
    {
        get => _flyout.PlacementMemory;
        set => _flyout.PlacementMemory = value;
    }

    public PopupWindow()
    {
        InitializeComponent();
        _flyout = new DockedFlyout(this);
        Deactivated += (_, _) => _flyout.OnDeactivated();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                _flyout.BeginClose();
                return;
            }

            // Alt and an arrow key moves the panel. Marked handled only when it actually moved,
            // so every other key, and the same press on a panel with nowhere to save a place
            // to, reaches the content exactly as it did before.
            if (_flyout.Nudge(e))
            {
                e.Handled = true;
            }
        };

        // The header is the handle. Hooked here rather than in the markup because the
        // threshold, the move loop and the save-on-release are one state machine, and it is the
        // same one that decides the panel does not dismiss itself mid-drag.
        _flyout.MakeDragHandle(PanelHeader);

        _scroll = new PopupScroll(PanelRoot, PanelBody);

        _planBlock = new PopupPlanBlock(
            this, PlanBlock, PlanBlockTitle, SessionMeter, WeeklyMeter,
            OffPlanRow, OffPlanRowTitle, OffPlanRowBadge, OffPlanRowCaption, OffPlanRowValue, OffPlanRowCoverage);
        _weeklyRow = new PopupWeeklyRow(this, WeeklyResetText, BoostChip, BoostChipText);
        _modelMeters = new PopupModelMeters(this, ModelLimitMeters);
        _localBlock = new PopupLocalBlock(
            this, LocalBlock, LocalBlockTitle, LocalBlockSubtitle, GraphHost, LocalChartLine, LocalTotalsTable,
            LocalWindowCaveat, TokenScopeNote, LocalFooter, LocalFooterCaveat,
            MeasureOutput, MeasureOverall, MeasureHighlight, ChooseView,
            growth => _fit!.RefitForPendingGrowth(growth), () => _flyout.Redock(ActualWidth, DockHeight));
        _localBlock.Start();
        _fit = new PopupFit(this, PanelRoot, _scroll, ApplyDensity, _localBlock.DrawChart);

        // The banner has the one fold rule the breakdown has: it fades ahead of its height, and
        // it never rises, because it is only ever leaving.
        var bannerFold = new DisclosureAnimation(
            this, DivergenceBannerFold, null,
            PanelMotion.Ms(PanelMotion.BannerFoldBaseMs), PanelMotion.Ms(PanelMotion.BannerFoldBaseMs),
            new DisclosureRowMotion(_ => 0, 0, PanelMotion.Ms(PanelMotion.BannerFadeBaseMs), RiseDip: 0),
            () => _flyout.Redock(ActualWidth, DockHeight));
        bannerFold.SetRows([[DivergenceBanner]]);
        _divergence = new PopupDivergence(
            this, DivergenceBannerFold, bannerFold,
            DivergenceTitle, DivergenceDetail, DivergenceLinkAnchor, HideBannerButton,
            RememberBannerHidden, _fit.RefitForPendingGrowth, () => _flyout.Redock(ActualWidth, DockHeight));
        _divergence.Start();
    }

    /// <summary>
    /// The height to dock against: the content's freshly measured size, not the window's.
    ///
    /// <para>A <c>SizeToContent</c> window's <c>ActualHeight</c> trails the layout pass that
    /// produced it by a frame, and the fold changes that height sixty times a second. Docking
    /// against the trailing number places the panel for the height it had a frame ago, which
    /// <c>--fold-check</c> caught twice: 72 px out for one frame when the fold starts, and a
    /// collapse settling 13 px above its docked edge and staying there. <c>DesiredSize</c> is
    /// the measure that the window's own size is about to follow.</para>
    /// </summary>
    private double DockHeight =>
        Content is FrameworkElement root && root.DesiredSize.Height > 0
            ? root.DesiredSize.Height
            : ActualHeight;

    /// <summary>
    /// Where the local block's table sits on the physical screen, so <c>--fold-check</c> can cut
    /// its filmstrip to the band that actually moves rather than to the whole panel.
    /// </summary>
    internal double DisclosureScreenTop => _localBlock.TableScreenTop;

    /// <summary>
    /// The table's current height, what <c>--fold-check</c> samples.
    /// </summary>
    internal double DisclosureHeight => _localBlock.TableHeight;

    /// <summary>
    /// Presses the switch from outside, for <c>--fold-check</c>: the real control, so the check
    /// sees whatever a reader's press does.
    /// </summary>
    internal void ToggleMeasureForVerification() => _localBlock.ToggleMeasureForVerification();

    /// <summary>
    /// Closes the panel from outside, the tray icon completing a toggle. Idempotent, so
    /// a click that arrives while the panel is already closing changes nothing.
    /// </summary>
    public void DismissNow() => _flyout.BeginClose();

    /// <summary>
    /// Puts an open panel back at the tray corner, the second half of the menu's "Reset panel
    /// position" row, whose first half is forgetting the saved place.
    ///
    /// <para>Goes through the same re-placement a growing panel does, so the corner it lands at
    /// is the shared rule's answer rather than a second one stated here. Does nothing when the
    /// panel is not open: the next open will place it at the corner anyway, there being nothing
    /// left to remember.</para>
    /// </summary>
    internal void ReturnToCorner()
    {
        if (IsVisible)
        {
            _flyout.Redock(ActualWidth, DockHeight);
        }
    }

    /// <summary>
    /// Why plan data is unavailable, surfaced when the figures read "unknown". Null skips
    /// the banner entirely (nothing to explain).
    /// </summary>
    public PlanHistoryReport? DataReport { get; set; }

    /// <summary>
    /// Where the local token counts were read from, for the scope note and the footer's caveat.
    /// Inspected on open like <see cref="DataReport"/>, so it reflects the machine as it is
    /// now. Null falls back to inspecting on demand, which keeps the verification renders
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
    /// Claude Code's cached usage block, for what the snapshot does not carry: whether extra
    /// usage is switched on for this account and when that was last read (issue #259), and each
    /// model's own weekly limit.
    ///
    /// <para>Set on open from the engine, which owns the read. Null renders the banner's
    /// unknown wording and no model bars, the same thing a machine with no Claude Code gets, and
    /// correctly so.</para>
    /// </summary>
    public CachedUtilization? CachedUsage { get; set; }

    /// <summary>
    /// The view the reader left the panel in, set on open from the shell. The default is what a
    /// fresh install and a verification render with no view of its own should show.
    /// </summary>
    public PanelViewChoice ViewChoice { get; set; }

    /// <summary>
    /// Stores a changed view and returns what is now in effect. Null on an offscreen render,
    /// which has nowhere to store one and nothing that presses a control.
    /// </summary>
    public Func<PanelViewChoice, PanelViewChoice>? SetViewChoice { get; set; }

    private TraySettings? _lastSettings;

    /// <summary>
    /// What the local block was last filled from, so pressing the switch redraws it from the
    /// figures on screen rather than inspecting the transcripts again.
    /// </summary>
    private (UsageSnapshot Snapshot, PanelStatistics Stats, TranscriptScopeReport Report)? _localShown;

    public void ShowNearTrayIcon(UsageSnapshot snapshot, PanelStatistics stats, ClaudeAccount? account)
    {
        _light = ThemeOverride ?? PanelTheme.IsAppsLight();
        PanelTheme.Apply(Resources, _light);
        Populate(snapshot, stats, account);

        // The chart is a Canvas, its ActualWidth is only known after layout, so it is
        // drawn between the flyout's layout pass and its placement rather than in Populate.
        // FitToWorkArea does that drawing, because choosing a density resizes the canvas.
        _flyout.Show(
            afterLayout: () => _fit.FitToWorkArea(stats, snapshot),
            arriving: () =>
            {
                PopupMeter.Arrive(PlanBlock);
                _localBlock.Arrive();
            });
    }

    /// <summary>
    /// Renders the panel to a bitmap without showing it, the detail panel's equivalent of
    /// <see cref="MenuWindow.RenderToBitmap"/>, driving the same <see cref="Populate"/> and
    /// <see cref="PopupFit.FitToWorkArea"/> the live path does.
    ///
    /// <para>It exists because the panel is otherwise only inspectable by opening it on a
    /// real desktop, which needs the single-instance mutex, puts a window over whatever the
    /// user is doing, and simply fails when something is running fullscreen, the case that
    /// blocked verifying the graph's week gridlines. Rendering offscreen also reaches the
    /// states this machine's real data may never produce, exactly as
    /// <c>--tile-samples</c> does for the tiles.</para>
    ///
    /// <para>The two layout passes are load-bearing and mirror the live sequence. The chart
    /// is a <c>Canvas</c>, so its <c>ActualWidth</c> is unknown until the tree has been laid
    /// out once; the live path gets that from <c>Show()</c> + <c>UpdateLayout()</c> before
    /// drawing the graph. Drawing the graph before the first pass silently
    /// falls back to the hard-coded default width and the gridlines land in the wrong
    /// places, which is precisely what this hook is meant to catch.</para>
    /// </summary>
    /// <param name="availableHeightDip">
    /// The work area to fit the render into, for the short-display sample; null renders the
    /// panel at whatever height it wants, as every other sample does. Given one, this does
    /// what <see cref="PopupFit.FitToWorkArea"/> does on that display, caps the content and scrolls
    /// it, with the figure pinned by the caller, because an offscreen tree has no window and
    /// no monitor to be granted one by.
    /// </param>
    internal System.Windows.Media.Imaging.BitmapSource RenderToBitmap(
        UsageSnapshot snapshot, PanelStatistics stats, ClaudeAccount? account, double scale,
        PanelDensity? density = null, double? availableHeightDip = null)
    {
        _light = ThemeOverride ?? PanelTheme.IsAppsLight();
        PanelTheme.Apply(Resources, _light);
        ApplyDensity(density ?? PanelDensity.Normal);
        Populate(snapshot, stats, account);

        // Before the render's own layout passes, so the chart the second one draws is measured
        // in the column a scroll bar has already narrowed.
        if (availableHeightDip is { } available)
        {
            _fit.ApplyFit(PanelFit.Resolve(_fit.NaturalHeightDip(), available), available);
        }

        // The second layout pass is the whole reason betweenPasses exists, see the
        // remarks above and on VisualRenderer.RenderContent.
        return VisualRenderer.RenderContent(this, scale, betweenPasses: _localBlock.DrawChart);
    }

    /// <summary>
    /// Renders the panel partway through a press on the switch, from <see cref="ViewChoice"/> to
    /// <paramref name="to"/>, <paramref name="atMs"/> after the press. The second layout pass is
    /// what lays the frame out; the first has to run on the settled view, which is what the frame
    /// is partway from and where the chart's and the highlight's starting points come from.
    /// </summary>
    internal System.Windows.Media.Imaging.BitmapSource RenderSwitchFrameToBitmap(
        UsageSnapshot snapshot, PanelStatistics stats, ClaudeAccount? account, double scale,
        PanelViewChoice to, double atMs)
    {
        _light = ThemeOverride ?? PanelTheme.IsAppsLight();
        PanelTheme.Apply(Resources, _light);
        ApplyDensity(PanelDensity.Normal);
        Populate(snapshot, stats, account);

        return VisualRenderer.RenderContent(this, scale, betweenPasses: () =>
        {
            _localBlock.DrawChart();
            ViewChoice = to;
            ShowLocalBlock((totals, footer, note) => _localBlock.ApplyPartialSwitch(totals, footer, note, atMs));
        });
    }

    /// <summary>
    /// The chart's hover card, built standalone for verification. They cannot appear in a
    /// render of the panel, a <c>ToolTip</c> throws if given a parent, so it can neither be
    /// laid out inside one nor screenshotted with it, and hover is the only way to reach
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
        ];
    }

    /// <summary>
    /// Lays the panel out at the given density (<see cref="PanelDensity"/>), the response to
    /// a display too short for the natural size.
    ///
    /// <para>Only spacing moves. Every figure, caveat and section is present at either
    /// density: a panel that dropped a section to fit would make a number quietly absent,
    /// which reads as a number that is zero (rule 6).</para>
    ///
    /// <para>The chart is the one non-spacing change, and it is the reason this is worth doing
    /// at all: the largest fixed height in the panel. Callers must redraw the chart after this:
    /// its bars are laid out against the canvas height, so a canvas that changed size leaves
    /// the previous drawing behind.</para>
    /// </summary>
    internal void ApplyDensity(PanelDensity density)
    {
        PanelRoot.Padding = new Thickness(density.RootPadding);

        _planBlock.ApplyDensity(density);
        _modelMeters.ApplyDensity(density);
        _localBlock.ApplyDensity(density);
    }

    // ── data ───────────────────────────────────────────────────────────────────

    private void Populate(UsageSnapshot snapshot, PanelStatistics stats, ClaudeAccount? account)
    {
        _lastSettings = SettingsForDisplay;

        // Back to the top: a populate is a fresh open or a re-layout at a new density, and
        // either way the header is where the panel should start (PopupScroll).
        _scroll.ScrollToTop();

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
        // worded from both reports together, a missing plan file beside working transcripts
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
        PopupMeter.Populate(SessionPctText, SessionBar, SessionBarFill, session, _light);
        // The session line always has something to say, the unknown state is a sentence of its
        // own, not an absence, so SetLine's empty case is unreachable here.
        PopupText.SetLine(SessionResetText, session.ResetLine ?? "");

        var weekly = PanelSections.WeeklyMeter(
            snapshot, banner, _lastSettings, BoostNotices, utcNow, local);
        PopupMeter.Populate(WeeklyPctText, WeeklyBar, WeeklyBarFill, weekly, _light);
        _weeklyRow.Populate(weekly);
        var modelLimits = PanelSections.ModelLimitMeters(CachedUsage, utcNow, local);
        _modelMeters.Populate(modelLimits, _light);

        var plan = PanelSections.PlanBlock(
            session, weekly, modelLimits, PanelSections.OffPlan(stats, CachedUsage, utcNow, local),
            stats, CachedUsage, ViewChoice);
        _planBlock.Populate(plan);
        _divergence.Populate(plan);

        _localShown = (snapshot, stats, ScopeReport ?? TranscriptScopeReport.Inspect());
        ShowLocalBlock(_localBlock.Populate);

        // The chart itself waits for ShowNearTrayIcon (needs post-layout ActualWidth).
    }

    private void ShowLocalBlock(Action<PanelLocalTotalsView, PanelLocalFooterView, string?> show)
    {
        if (_localShown is { } shown)
        {
            show(
                PanelSections.LocalTotals(shown.Stats, shown.Snapshot, LocalZone, ViewChoice),
                PanelSections.LocalFooter(shown.Report, ViewChoice),
                PanelSections.ScopeNote(shown.Stats, shown.Snapshot, shown.Report));
        }
    }

    /// <summary>
    /// The switch was pressed: keeps the new view, the moment it is pressed, so a panel closed
    /// mid-fold still opens on it, and has the block move to it.
    /// </summary>
    private void ChooseView(PanelViewChoice next)
    {
        ViewChoice = SetViewChoice?.Invoke(next) ?? next;
        ShowLocalBlock(_localBlock.SwitchTo);
    }

    /// <summary>
    /// Keeps what the reader pressed, the moment they press it, so a panel closed mid-fold still
    /// opens without the banner.
    /// </summary>
    private void RememberBannerHidden()
    {
        var next = ViewChoice with { BannerHidden = true };
        ViewChoice = SetViewChoice?.Invoke(next) ?? next;
    }

    /// <summary>
    /// Opens the off-plan banner's link in the user's browser (issue #259).
    ///
    /// <para><c>UseShellExecute</c> because the target is a URL rather than an executable,
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


    // Theming lives in PanelTheme, shared with the tray menu flyout (issue #33).
}
