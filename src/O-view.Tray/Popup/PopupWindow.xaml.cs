using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using OView.Core.Models;
using OView.Core.Providers.PlanHistory;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Rectangle = System.Windows.Shapes.Rectangle;
using Size = System.Windows.Size;

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

    public PopupWindow()
    {
        InitializeComponent();
        Deactivated += (_, _) => { if (!PinForVerification) Hide(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Hide(); };
    }

    /// <summary>
    /// Why plan data is unavailable, surfaced when the figures read "unknown". Null skips
    /// the banner entirely (nothing to explain).
    /// </summary>
    public PlanHistoryReport? DataReport { get; set; }

    public void ShowNearTrayIcon(UsageSnapshot snapshot, PanelStatistics stats, ClaudeAccount? account)
    {
        PanelTheme.Apply(Resources, ThemeOverride ?? PanelTheme.IsAppsLight());
        Populate(snapshot, stats, account);

        // SizeToContent height is unknown until measured: lay out off-screen, then place.
        Left = -10_000;
        Top = -10_000;
        Show();
        UpdateLayout();
        // The chart is a Canvas — its ActualWidth is only known after layout, so it is
        // drawn here rather than in Populate.
        BuildGraph(stats);
        (Left, Top) = PopupPositioner.Place(ActualWidth, ActualHeight);
        Activate();
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
        // Issue #6: show the weekly reset only once it has genuinely been derived
        // (two observed resets). Until then, no line at all — an honest blank beats a
        // "reset time unknown" that reads as broken.
        if (snapshot.WeeklyResetAtUtc is { } weeklyReset)
        {
            WeeklyResetText.Text = $"Resets in {FormatCountdown(weeklyReset - Now(TimeZoneInfo.Utc))} · {TimeZoneInfo.ConvertTime(weeklyReset, local):ddd HH:mm}";
            WeeklyResetText.Visibility = Visibility.Visible;
        }
        else
        {
            WeeklyResetText.Visibility = Visibility.Collapsed;
        }

        PopulateTiles(stats);
        PopulateDivergence(stats);

        // Nothing recorded at all, while the plan meters show real usage: the tiles are
        // measuring a source this user does not feed, not measuring zero usage. Say which
        // source, so the 0 is interpretable instead of looking broken.
        if (stats.RecordedDays == 0 && authoritative && snapshot.SessionPercent is > 0)
        {
            TokenScopeNote.Text =
                "No Claude Code usage recorded. Token counts and Est. value are read from Claude Code "
                + "transcripts (%USERPROFILE%\\.claude\\projects) — chats in the Claude Desktop app do "
                + "not produce these, so they are not counted here. The session and weekly bars above "
                + "come from Claude Desktop itself and do cover all of your usage.";
            TokenScopeNote.Visibility = Visibility.Visible;
        }
        else
        {
            TokenScopeNote.Visibility = Visibility.Collapsed;
        }

        // BuildGraph is deferred to ShowNearTrayIcon (needs post-layout ActualWidth).
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
            stats.ModelsToday, BreakdownMeasure.Tokens);

        TileEstToday.FormatSlice = s => FormatUsd(s.EstUsd);
        TileEstToday.Populate(
            offPlan ? "Est. spend today" : "Est. value today",
            FormatUsd(stats.EstTodayUsd),
            offPlan ? "incl. off-plan usage" : "",
            stats.ModelsToday, BreakdownMeasure.EstValue);

        TileTokens31.FormatSlice = s => FormatTokens(s.Tokens);
        TileTokens31.Populate(
            "Tokens · 31 days", FormatTokens(stats.Tokens31Days), coverage,
            stats.Models31Days, BreakdownMeasure.Tokens);

        TileEst31.FormatSlice = s => FormatUsd(s.EstUsd);
        TileEst31.Populate(
            "Est. value · 31 days", FormatUsd(stats.Est31DaysUsd), coverage,
            stats.Models31Days, BreakdownMeasure.EstValue);
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
    private void BuildGraph(PanelStatistics stats)
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

        // Per-calendar-week peak, so colour intensity is normalised within each week.
        var weekMax = series
            .Where(d => !d.PreInstall)
            .GroupBy(d => WeekStart(d.DateUtc))
            .ToDictionary(g => g.Key, g => Math.Max(1, g.Max(d => d.TotalTokens)));

        var gridBrush = (Brush)FindResource("TextMuted");
        var labelBrush = (Brush)FindResource("TextMuted");

        for (var i = 0; i < series.Count; i++)
        {
            var day = series[i];
            var x = i * col;

            // Monday gridline (skip index 0 — the left edge needs no line).
            if (i > 0 && day.DateUtc.DayOfWeek == DayOfWeek.Monday)
            {
                var line = new Line
                {
                    X1 = x, X2 = x, Y1 = 0, Y2 = barAreaHeight + 3,
                    Stroke = gridBrush,
                    StrokeThickness = 1,
                    StrokeDashArray = [1, 2],
                    Opacity = 0.7,
                };
                GraphHost.Children.Add(line);
            }

            // Bar — height by absolute tokens, colour by within-week intensity.
            if (!day.PreInstall && day.TotalTokens > 0)
            {
                var intensity = day.TotalTokens / (double)weekMax[WeekStart(day.DateUtc)];
                var height = Math.Max(2, barAreaHeight * day.TotalTokens / globalMax);
                var barWidth = Math.Max(2, col - 2);
                var bar = new Rectangle
                {
                    Width = barWidth,
                    Height = height,
                    Fill = new SolidColorBrush(Lerp(GraphBlueLo, GraphBlueHi, intensity)),
                    RadiusX = 1,
                    RadiusY = 1,
                    ToolTip = $"{day.DateUtc:ddd d MMM} · {FormatTokens(day.TotalTokens)} tokens",
                };
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
    }

    /// <summary>
    /// Horizontal centre of the column starting at <paramref name="x"/>. The single
    /// anchor the bar and its date label are both placed from (issue #31) — the label
    /// was previously offset by a constant that no longer matched the bar.
    /// </summary>
    private static double BarCentre(double x, double col) => x + col / 2;

    /// <summary>Monday of the ISO week containing the date.</summary>
    private static DateOnly WeekStart(DateOnly date) =>
        date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

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

    private static string FormatCountdown(TimeSpan t) => t.TotalMinutes < 1
        ? "under a minute"
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
