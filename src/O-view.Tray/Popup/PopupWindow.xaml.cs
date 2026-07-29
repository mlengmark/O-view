using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
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
public partial class PopupWindow : Window
{
    private static readonly Color Green = Color.FromRgb(64, 200, 110);
    private static readonly Color Amber = Color.FromRgb(240, 170, 40);
    private static readonly Color Red = Color.FromRgb(232, 72, 72);

    /// <summary>Forces a theme for verification screenshots; null follows the OS.</summary>
    public bool? ThemeOverride { get; set; }

    /// <summary>Disables auto-hide for verification screenshots.</summary>
    public bool PinForVerification { get; set; }

    /// <summary>Corner the panel is docked to, so it grows from the tray rather than the middle.</summary>
    private Point _dockOrigin = new(1, 1);

    /// <summary>Guards the close transition against a re-open landing mid-fade.</summary>
    private bool _closing;

    /// <summary>
    /// When the panel last began closing because it lost focus. Clicking the tray icon
    /// while the panel is open deactivates it — so by the time the click arrives the
    /// panel is already closing, and re-showing it would make the icon impossible to
    /// toggle with. <see cref="ClosedByClickAway"/> lets the caller tell the two cases
    /// apart.
    /// </summary>
    private DateTimeOffset _closedAt = DateTimeOffset.MinValue;

    /// <summary>
    /// True if the panel dismissed itself a moment ago, which for a tray click means the
    /// click was the dismissal — so it should NOT reopen.
    ///
    /// The window is generous enough to cover the deactivate-then-click ordering and the
    /// close transition, and short enough that a deliberate second click a moment later
    /// still opens the panel.
    /// </summary>
    public bool ClosedByClickAway =>
        DateTimeOffset.UtcNow - _closedAt < TimeSpan.FromMilliseconds(400);

    public PopupWindow()
    {
        InitializeComponent();
        Deactivated += (_, _) => { if (!PinForVerification) BeginClose(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) BeginClose(); };
    }

    /// <summary>
    /// Closes the panel from outside — the tray icon completing a toggle. Idempotent, so
    /// a click that arrives while the panel is already closing changes nothing.
    /// </summary>
    public void DismissNow() => BeginClose();

    /// <summary>
    /// Fades and shrinks back into the docked corner, then hides. The Hide() is deferred
    /// to the end of the transition — hiding first would make the animation invisible,
    /// which is the whole point of having one.
    /// </summary>
    private void BeginClose()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        _closedAt = DateTimeOffset.UtcNow;
        FlyoutAnimation.Close(this, _dockOrigin, () =>
        {
            // Re-checked because 110ms is long enough for the tray icon to be clicked
            // again: if the panel was re-opened while this was running, the flag is
            // already clear and hiding now would undo that.
            if (_closing)
            {
                _closing = false;
                Hide();
            }
        });
    }

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

    public void ShowNearTrayIcon(UsageSnapshot snapshot, PanelStatistics stats, ClaudeAccount? account)
    {
        PanelTheme.Apply(Resources, ThemeOverride ?? PanelTheme.IsAppsLight());
        Populate(snapshot, stats, account);

        // Cancels a close still in flight, so clicking the tray icon again mid-fade
        // brings the panel straight back instead of letting it finish disappearing.
        _closing = false;

        // SizeToContent height is unknown until measured: lay out off-screen, then place.
        // Opacity starts at 0 so the off-screen frame never flashes at the final spot.
        Opacity = 0;
        Left = -10_000;
        Top = -10_000;
        Show();
        UpdateLayout();
        // The chart is a Canvas — its ActualWidth is only known after layout, so it is
        // drawn here rather than in Populate.
        BuildGraph(stats, snapshot);
        (Left, Top, _dockOrigin) = PopupPositioner.Place(ActualWidth, ActualHeight);
        Activate();

        if (PinForVerification)
        {
            // Stills need the finished state, not a frame from the middle of a fade.
            FlyoutAnimation.Reset(this);
        }
        else
        {
            FlyoutAnimation.Open(this, _dockOrigin);
        }
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
        UsageSnapshot snapshot, PanelStatistics stats, ClaudeAccount? account, double scale)
    {
        PanelTheme.Apply(Resources, ThemeOverride ?? PanelTheme.IsAppsLight());
        Populate(snapshot, stats, account);

        var root = (FrameworkElement)Content;
        void Layout()
        {
            root.Measure(new Size(Width, double.PositiveInfinity));
            root.Arrange(new Rect(root.DesiredSize));
            root.UpdateLayout();
        }

        Layout();
        BuildGraph(stats, snapshot);
        Layout();

        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Math.Ceiling(root.ActualWidth * scale),
            (int)Math.Ceiling(root.ActualHeight * scale),
            96 * scale, 96 * scale, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(root);
        return bitmap;
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

    // ── data ───────────────────────────────────────────────────────────────────

    private void Populate(UsageSnapshot snapshot, PanelStatistics stats, ClaudeAccount? account)
    {
        var local = TimeZoneInfo.Local;

        UpdatedText.Text = $"Updated {Now(local):HH:mm} · {SourceLabel(snapshot, local)}";
        NameText.Text = account?.DisplayName ?? "account unknown";
        EmailText.Text = account?.Email ?? "";
        EmailText.Visibility = account?.Email is null ? Visibility.Collapsed : Visibility.Visible;
        // Tier from organizationType — seatTier is empty and would render blank (rule 8).
        TierText.Text = account?.Tier ?? "tier unknown";

        var authoritative = snapshot.Source is DataSource.Live or DataSource.Stale;

        // Explain a blank panel rather than leaving the user to guess (rule 6: if data is
        // unavailable, say so). Only shown when the figures are actually unavailable.
        var explanation = !authoritative ? DataReport?.Explain() : null;
        if (explanation is { Length: > 0 })
        {
            NoDataTitle.Text = "No usage data";
            NoDataDetail.Text = explanation;
            NoDataBanner.Visibility = Visibility.Visible;
        }
        else
        {
            NoDataBanner.Visibility = Visibility.Collapsed;
        }

        PopulateBar(SessionPctText, SessionBar, SessionBarFill,
            authoritative ? snapshot.SessionPercent : null);
        SessionResetText.Text = snapshot.SessionResetAtUtc is { } reset
            ? $"Resets in {FormatCountdown(reset - Now(TimeZoneInfo.Utc))} · {TimeZoneInfo.ConvertTime(reset, local):HH:mm}"
            : "Reset time unknown (no reset observed yet)";

        PopulateBar(WeeklyPctText, WeeklyBar, WeeklyBarFill,
            authoritative ? snapshot.WeeklyPercent : null);
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
        if (stats.RecordedDays == 0 && authoritative && snapshot.SessionPercent is > 0)
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
            var approximate = bracket > WeeklyResetDetector.PreciseBracket;
            var at = TimeZoneInfo.ConvertTime(reset, local);

            WeeklyResetText.Text =
                $"Resets in {FormatCountdown(reset - Now(TimeZoneInfo.Utc))} · {(approximate ? "~" : "")}{at:ddd HH:mm}";
            WeeklyResetText.ToolTip = approximate
                ? HoverCard.Text(this,
                    $"The weekly reset was observed while Claude Desktop wasn't sampling, so it is "
                    + $"known to within {FormatCountdown(bracket)}. O-view keeps watching and will "
                    + "sharpen this if it sees a reset while Desktop is running.")
                : null;
            HoverCard.ApplyTiming(WeeklyResetText);
            WeeklyResetText.Visibility = Visibility.Visible;
            return;
        }

        WeeklyResetText.Text = "Waiting for first reset…";
        WeeklyResetText.ToolTip = HoverCard.Text(this,
            "Claude Desktop reports weekly usage as a percentage and never reports when the "
            + "window resets, so O-view derives it by watching that percentage fall. It checks "
            + "on every refresh and fills this in on the first reset it sees — within a week.");
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
        var coverage = stats.HasPartialHistory
            ? $"{stats.RecordedDays} of {stats.WindowDays} days recorded"
            : "";
        if (stats.UnpricedModels.Count > 0)
        {
            var excluded = $"excludes {string.Join(", ", stats.UnpricedModels)} (no published rate)";
            coverage = coverage.Length > 0 ? $"{coverage} · {excluded}" : excluded;
        }

        // The "not money charged" framing is only true for plan usage. Off-plan work
        // bills at API rates, so the label has to flip with it.
        var offPlan = stats.IsOffPlan;

        TileTokensToday.FormatSlice = s => FormatTokens(s.Tokens);
        TileTokensToday.Populate(
            "Tokens today", FormatTokens(stats.TokensToday), "",
            stats.ModelsToday, BreakdownMeasure.Tokens, stats.ModelColourOrder);

        TileEstToday.FormatSlice = s => FormatUsd(s.EstUsd);
        TileEstToday.Populate(
            offPlan ? "Est. spend today" : "Est. value today",
            FormatUsd(stats.EstTodayUsd),
            offPlan ? "incl. off-plan usage" : "",
            stats.ModelsToday, BreakdownMeasure.EstValue, stats.ModelColourOrder);

        TileTokens31.FormatSlice = s => FormatTokens(s.Tokens);
        TileTokens31.Populate(
            "Tokens · 31 days", FormatTokens(stats.Tokens31Days), coverage,
            stats.Models31Days, BreakdownMeasure.Tokens, stats.ModelColourOrder);

        TileEst31.FormatSlice = s => FormatUsd(s.EstUsd);
        TileEst31.Populate(
            "Est. value · 31 days", FormatUsd(stats.Est31DaysUsd), coverage,
            stats.Models31Days, BreakdownMeasure.EstValue, stats.ModelColourOrder);
    }

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
                ? "The 5-hour window is exhausted, so continued work bills as extra usage at API rates."
                : $"About {FormatTokens(d.OutputTokensInWindow)} output tokens ran this window while the plan meter moved "
                  + $"{d.PlanRisePoints} point{(d.PlanRisePoints == 1 ? "" : "s")}. That work is billing elsewhere — most likely extra-usage credits.";
        }

        // ── standing 31-day off-plan spend (issue #3) ────────────────────────────
        if (stats.HasCreditUsage)
        {
            CreditsBadgeText.Text = "credit-billed";
            CreditsSpendLabel.Text = "Est. credit spend";
            CreditsSpendValue.Text = FormatUsd(stats.EstCredit31DaysUsd);
            CreditsCoverage.Text = stats.HasPartialHistory
                ? $"{stats.RecordedDays} of {stats.WindowDays} days recorded"
                : "";
            // Issue #32: trimmed to two clauses. What remains still carries the rule-6
            // caveat — "Estimated at published API rates" and "check your billing page for
            // exact figures" both say this is not the charged number.
            CreditsNote.Text = $"Estimated at published API rates for models billed as extra usage ({CreditBilledModels.DisplayList}). "
                + "O-view cannot read your credit balance; check your billing page for exact figures.";
        }
        else
        {
            CreditsBadgeText.Text = "none recorded";
            CreditsSpendLabel.Text = "Est. credit spend";
            CreditsSpendValue.Text = "$0.00";
            CreditsCoverage.Text = "";
            CreditsNote.Text = $"No credit-billed usage ({CreditBilledModels.DisplayList}) recorded in the last 31 days. "
                + "Off-plan usage while O-view wasn't running isn't captured.";
        }
    }

    private void PopulateBar(TextBlock pctText, Grid bar, Border fill, int? percent)
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
            pctText.Text = "unknown";
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
        const double barAreaHeight = 52;
        const double labelTop = 58;
        var col = width / series.Count;
        var globalMax = Math.Max(1, series.Max(d => d.TotalTokens));

        var weeks = PlanWeeks.ForSeries(series, snapshot);

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
                        day.DateUtc.ToString("dddd d MMMM", CultureInfo.InvariantCulture)),
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
                Text = day.DateUtc.ToString("d MMM", CultureInfo.InvariantCulture),
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
            .Select((day, index) => (day.DateUtc, index))
            .ToDictionary(t => t.DateUtc, t => t.index);

        foreach (var boundary in weeks.Boundaries)
        {
            if (!columnOf.TryGetValue(boundary.DayUtc, out var index))
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
    /// </summary>
    private readonly record struct WeekBoundary(DateTimeOffset AtUtc)
    {
        public DateOnly DayUtc => DateOnly.FromDateTime(AtUtc.UtcDateTime);
        public double FractionOfDay => AtUtc.UtcDateTime.TimeOfDay / TimeSpan.FromDays(1);
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

        private PlanWeeks(IReadOnlyList<DateTimeOffset> boundaries, bool isPlanDerived)
        {
            _boundaries = boundaries;
            IsPlanDerived = isPlanDerived;
        }

        /// <summary>False when these are calendar weeks standing in for an unknown plan week.</summary>
        public bool IsPlanDerived { get; }

        public IEnumerable<WeekBoundary> Boundaries => _boundaries.Select(b => new WeekBoundary(b));

        public static PlanWeeks ForSeries(IReadOnlyList<DayUsage> series, UsageSnapshot snapshot)
        {
            var fromUtc = new DateTimeOffset(series[0].DateUtc.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            var toUtc = new DateTimeOffset(series[^1].DateUtc.ToDateTime(TimeOnly.MaxValue), TimeSpan.Zero);

            if (snapshot.WeeklyResetAtUtc is { } next && snapshot.WeeklyResetPeriod is { } period)
            {
                return new PlanWeeks(
                    WeeklyResetDetector.BoundariesWithin(next, period, fromUtc, toUtc),
                    isPlanDerived: true);
            }

            // Fallback: midnight at the start of each Monday in range.
            var mondays = series
                .Select(d => d.DateUtc)
                .Where(d => d.DayOfWeek == DayOfWeek.Monday)
                .Select(d => new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero))
                .ToList();
            return new PlanWeeks(mondays, isPlanDerived: false);
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
            var midday = new DateTimeOffset(day.DateUtc.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
                .AddHours(12);
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

    private static string SourceLabel(UsageSnapshot s, TimeZoneInfo local) => s.Source switch
    {
        DataSource.Live => "live",
        DataSource.Stale => s.CapturedAtUtc is { } at
            ? $"as of {TimeZoneInfo.ConvertTime(at, local):HH:mm}"
            : "stale",
        DataSource.Estimate => "local estimate",
        _ => "no data",
    };

    /// <summary>
    /// Coarsest-two-units countdown. The days branch exists for the weekly window: a
    /// 7-day reset rendered in the hours format reads "163h 45m", which is technically
    /// right and useless.
    /// </summary>
    private static string FormatCountdown(TimeSpan t) => t.TotalMinutes < 1
        ? "under a minute"
        : t.TotalDays >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{t.Days}d {t.Hours}h")
            : t.TotalHours >= 1
                ? string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours}h {t.Minutes}m")
                : string.Create(CultureInfo.InvariantCulture, $"{t.Minutes}m");

    private static string FormatTokens(long tokens) => tokens switch
    {
        >= 1_000_000 => string.Create(CultureInfo.InvariantCulture, $"{tokens / 1_000_000.0:0.0}M"),
        >= 1_000 => string.Create(CultureInfo.InvariantCulture, $"{tokens / 1_000.0:0.0}K"),
        _ => tokens.ToString(CultureInfo.InvariantCulture),
    };

    private static string FormatUsd(decimal? usd) => usd is { } v
        ? "$" + v.ToString("0.00", CultureInfo.InvariantCulture)
        : "unknown";

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
