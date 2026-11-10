using System.Reflection;

using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.VisualTree;
using OView.App.Rendering;
using OView.Core.Models;
using OView.Linux.Panel;
using OView.Presentation.Panel;
using OView.Presentation.Samples;
using OView.Presentation.Text;
using Line = Avalonia.Controls.Shapes.Line;

namespace OView.Linux.Tests;

/// <summary>
/// The 31-day chart as this head lays it out: that every measurement is the shared one, that it
/// fills the column it is given at whatever width that is, and that nothing older than the date
/// axis is left on it.
///
/// <para>The trees are real and laid out with a real font, for the reason
/// <see cref="PanelShortScreenTests"/> gives. A chart that placed itself against a width it had
/// to wait for would come out blank in these, because they are laid out by hand exactly as the
/// verification renders are.</para>
/// </summary>
public class PanelGraphLayoutTests
{
    private const string Typical = "typical";

    // ── the marks ───────────────────────────────────────────────────────────────────

    /// <summary>The bars' area follows the density, and the axis band under it holds text and stays.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheHeightIsTheSharedChartHeightAtTheDensitysScale(bool compact)
    {
        var density = compact ? PanelDensity.Compact : PanelDensity.Normal;
        var chart = ChartOf(Laid(Typical, false, density));
        var bars = Math.Round(PanelMetrics.ChartHeightDip * density.GraphScale);

        Assert.Equal(bars + PanelMetrics.ChartAxisBandDip, chart.Bounds.Height);

        var baseline = Assert.Single(Hairlines(chart), line => line.Bounds.Width > 1);
        Assert.Equal(0, baseline.Bounds.X);
        Assert.Equal(bars, baseline.Bounds.Y);
        Assert.Equal(chart.Bounds.Width, baseline.Bounds.Width);
        Assert.Equal(1, baseline.Bounds.Height);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryBarStandsInItsSlotAndFadesTowardsTheOldest(bool light)
    {
        var chart = ChartOf(Laid(Typical, light, PanelDensity.Normal));
        var view = OutputChart(Typical);
        var slot = chart.Bounds.Width / view.Columns.Count;
        var area = PanelMetrics.ChartHeightDip;
        var last = view.Columns.Count - 1;

        var drawn = view.Columns
            .Select((column, index) => (column, index))
            .Where(t => t.column.HasBar)
            .Zip(BarsOf(chart));

        foreach (var ((column, index), bar) in drawn)
        {
            var age = index / (double)last;
            var height = Math.Max(PanelMetrics.GraphMinBarHeightDip, (area - 4) * column.Height);

            Assert.Equal(index * slot + PanelMetrics.ChartBarInsetDip, bar.Bounds.X, 6);
            Assert.Equal(slot - 2 * PanelMetrics.ChartBarInsetDip, bar.Bounds.Width, 6);
            Assert.Equal(height, bar.Bounds.Height, 6);
            Assert.Equal(area, bar.Bounds.Bottom, 6);
            Assert.Equal(new CornerRadius(PanelMetrics.ChartBarRadiusDip), bar.CornerRadius);
            Assert.Equal(
                PanelMetrics.ChartOldestBarOpacity
                + (PanelMetrics.ChartNewestBarOpacity - PanelMetrics.ChartOldestBarOpacity) * age,
                bar.Opacity,
                6);
            Assert.Equal(Hex(index == last ? "AccentBg" : "Series1", light), Colour(bar.Background));
            Assert.Equal($"{column.CardFigure} · {column.CardCaption}", CardWords(bar));
        }
    }

    /// <summary>
    /// A bar's card is the panel's shared card in the Windows bar card's shape: the figure, large,
    /// with the caption Presentation built for it muted beneath.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ABarsCardIsTheSharedCardWithTheFigureOverItsCaption(bool light)
    {
        var columns = OutputChart(Typical).Columns.Where(c => c.HasBar);

        foreach (var (bar, column) in BarsOf(ChartOf(Laid(Typical, light, PanelDensity.Normal))).Zip(columns))
        {
            var card = CardOf(bar);
            var lines = card.GetLogicalDescendants().OfType<TextBlock>().ToList();

            Assert.Equal(Hex("TooltipBg", light), Colour(card.Background));
            Assert.Equal(Hex("TooltipBorder", light), Colour(card.BorderBrush));
            Assert.Equal(new CornerRadius(6), card.CornerRadius);
            Assert.Equal([column.CardFigure, column.CardCaption], lines.Select(t => t.Text));

            Assert.Equal(15, lines[0].FontSize);
            Assert.Equal(FontWeight.SemiBold, lines[0].FontWeight);
            Assert.Equal(Hex("TextPrimary", light), Colour(lines[0].Foreground));
            Assert.Equal(10, lines[1].FontSize);
            Assert.Equal(Hex("TextMuted", light), Colour(lines[1].Foreground));
        }
    }

    /// <summary>
    /// A card opens at the pointer while it has room right of its bar, and beside the bar on its
    /// left after that, so the early days follow the pointer and the late ones open inwards. No
    /// sample state has all 31 days, so this is laid out on one that does.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheEarlyDaysCardsFollowThePointerAndTheLateOnesOpenLeft(bool compact)
    {
        var chart = ChartOf(Laid(EveryDayRecorded(), false, compact ? PanelDensity.Compact : PanelDensity.Normal));
        var bars = BarsOf(chart);

        Assert.Equal(31, bars.Count);
        foreach (var bar in bars)
        {
            var card = CardOf(bar);
            card.Measure(Size.Infinity);
            var fitsRight = bar.Bounds.Right + ToolTip.GetHorizontalOffset(bar) + card.DesiredSize.Width <= chart.Bounds.Width;

            Assert.Equal(fitsRight ? PlacementMode.Pointer : PlacementMode.Left, ToolTip.GetPlacement(bar));
        }

        Assert.Equal(PlacementMode.Pointer, ToolTip.GetPlacement(bars[0]));
        Assert.Equal(PlacementMode.Left, ToolTip.GetPlacement(bars[^1]));
    }
    /// <summary>
    /// No bar's card is measured while the panel is built, only once the chart is arranged. On
    /// Ubuntu, a card measured that early made the panel's own table figures measure wider, and
    /// the longest ones wrapped in their cells.
    /// </summary>
    [Fact]
    public void NoBarsCardIsMeasuredUntilTheChartIsArranged()
    {
        UseRealFonts();
        var state = SampleStates.All.Single(s => s.Name == Typical);
        var panel = new PanelContent(new LinuxPanelTheme(false)) { Density = PanelDensity.Normal };
        panel.Populate(state.Snapshot, state.Stats, state.Account, state.Plan, state.Scope,
            SampleStates.Now, state.Boost, SampleStates.Zone, state.CachedUsage, state.View);

        Assert.All(BarsOf(ChartOf(panel)), bar => Assert.False(CardOf(bar).IsMeasureValid));

        PanelSampleRenderer.LayOut(panel);

        Assert.All(BarsOf(ChartOf(panel)), bar => Assert.True(CardOf(bar).IsMeasureValid));
    }

    /// <summary>A day before install has no bar and no zero-height stand-in for one (F15).</summary>
    [Fact]
    public void ADayWithoutABarDrawsNothingAtAll()
    {
        var chart = ChartOf(Laid(Typical, false, PanelDensity.Normal));
        var view = OutputChart(Typical);

        Assert.Contains(view.Columns, column => !column.Recorded);
        Assert.Equal(view.Columns.Count(column => column.HasBar), BarsOf(chart).Count);
        Assert.All(BarsOf(chart), bar => Assert.True(bar.Bounds.Height >= PanelMetrics.GraphMinBarHeightDip));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryWeekHasALineAndItsDateIsWrittenBesideIt(bool light)
    {
        var chart = ChartOf(Laid(Typical, light, PanelDensity.Normal));
        var view = OutputChart(Typical);
        var slot = chart.Bounds.Width / view.Columns.Count;
        var lines = Hairlines(chart).Where(line => line.Bounds.Width <= 1).ToList();

        Assert.Equal(view.WeekMarks.Count, lines.Count);

        foreach (var (mark, line) in view.WeekMarks.Zip(lines))
        {
            Assert.Equal(mark.Column * slot, line.Bounds.X, 0.51);
            Assert.Equal(0, line.Bounds.Y);
            Assert.Equal(1, line.Bounds.Width);
            Assert.Equal(PanelMetrics.ChartHeightDip, line.Bounds.Height);
            Assert.Equal(Hex("PanelBorder", light), Colour(line.Background));

            var date = Words(chart, mark.DateLabel);
            Assert.Equal(PanelMetrics.ChartWeekLabelFontSize, date.FontSize);
            Assert.Equal(Hex("TextMuted", light), Colour(date.Foreground));
            Assert.Null(date.RenderTransform);

            if (date.Opacity > 0)
            {
                Assert.Equal(mark.Column * slot + 3, date.Bounds.X, 0.51);
                Assert.True(date.Bounds.Y >= PanelMetrics.ChartHeightDip);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TodayEndsTheAxisInTheAccentInk(bool light)
    {
        var chart = ChartOf(Laid(Typical, light, PanelDensity.Normal));
        var today = Words(chart, TokenText.ChartTodayLabel);

        Assert.Equal(chart.Bounds.Width - 2, today.Bounds.Right, 1);
        Assert.Equal(PanelMetrics.ChartWeekLabelFontSize, today.FontSize);
        Assert.Equal(Hex("AccentInk", light), Colour(today.Foreground));
        Assert.Equal(1, today.Opacity);
    }

    /// <summary>
    /// The sample's window ends on a Tuesday, so its last Monday is the column before today: the
    /// real case of a week line with no room left for its date.
    /// </summary>
    [Fact]
    public void AMondayTooNearTheEndKeepsItsLineAndLosesItsDate()
    {
        var chart = ChartOf(Laid(Typical, false, PanelDensity.Normal));
        var view = OutputChart(Typical);
        var today = Words(chart, TokenText.ChartTodayLabel);
        var mark = view.WeekMarks[^1];

        Assert.Equal(view.Columns.Count - 2, mark.Column);

        var line = Hairlines(chart).Last(l => l.Bounds.Width <= 1);
        var date = Words(chart, mark.DateLabel);

        Assert.Equal(PanelMetrics.ChartHeightDip, line.Bounds.Height);
        Assert.Equal(0, date.Opacity);
        Assert.Equal(0, date.Bounds.Width);

        Assert.All(
            view.WeekMarks.SkipLast(1).Select(m => Words(chart, m.DateLabel)),
            earlier =>
            {
                Assert.Equal(1, earlier.Opacity);
                Assert.True(earlier.Bounds.Right <= today.Bounds.X - 6, $"[{earlier.Text}] runs into today");
            });
    }

    [Fact]
    public void NothingOlderThanTheDateAxisIsLeftOnThePanel()
    {
        var panel = Laid(Typical, false, PanelDensity.Normal);
        var chart = ChartOf(panel);
        var view = OutputChart(Typical);

        Assert.Empty(panel.GetVisualDescendants().OfType<Line>());
        Assert.DoesNotContain(
            panel.GetVisualDescendants().OfType<TextBlock>(), text => text.RenderTransform is RotateTransform);

        // The baseline, the bars, a line and a date per week, and today: nothing else stands here.
        Assert.Equal(
            1 + BarsOf(chart).Count + 2 * view.WeekMarks.Count + 1,
            chart.GetVisualChildren().Count());
    }

    /// <summary>The chart follows the switch: an Overall view draws all billed tokens per day, not the Output bars.</summary>
    [Fact]
    public void AnOverallStateDrawsTheOverallChart()
    {
        var state = SampleStates.All.First(s => s.View.Measure == PanelMeasure.Overall);
        var output = Tips(OutputChart(state.Name));
        var overall = Tips(PanelSections.LocalTotals(state.Stats, state.Snapshot, SampleStates.Zone, state.View).Chart);

        Assert.NotEqual(overall, output);
        Assert.Equal(overall, BarsOf(ChartOf(Laid(state.Name, false, PanelDensity.Normal))).Select(CardWords));
    }

    // ── the column ──────────────────────────────────────────────────────────────────

    public static TheoryData<string, bool, bool> EveryStateThemeAndDensity()
    {
        var data = new TheoryData<string, bool, bool>();
        foreach (var state in SampleStates.All.Where(s => s.View.Face == PanelFace.Totals))
        {
            foreach (var light in new[] { false, true })
            {
                data.Add(state.Name, light, false);
                data.Add(state.Name, light, true);
            }
        }

        return data;
    }

    /// <summary>
    /// The compact density's padding is smaller than the natural one, so its column is the wider
    /// of the two: a chart built to one fixed width would be wrong at one of them whichever it
    /// was. The bar's edge is read against the panel, because that is the edge that must not be
    /// crossed.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryStateThemeAndDensity))]
    public void TheChartFillsItsColumnAndNothingOnItCrossesThePanelOrIsTrimmed(string state, bool light, bool compact)
    {
        var density = compact ? PanelDensity.Compact : PanelDensity.Normal;
        var panel = Laid(state, light, density);

        if (OutputChart(state).Columns.Count == 0)
        {
            Assert.DoesNotContain(
                panel.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == TokenText.ChartTodayLabel);
            return;
        }

        var chart = ChartOf(panel);
        var inset = panel.BorderThickness.Left + panel.Padding.Left;
        var column = panel.Bounds.Width - 2 * inset;

        Assert.Equal(density.RootPadding, panel.Padding.Left);
        Assert.Equal(column, chart.Bounds.Width);
        Assert.Equal(inset, chart.TranslatePoint(default, panel)!.Value.X);

        foreach (var mark in chart.GetVisualChildren().OfType<Control>())
        {
            Assert.True(mark.Bounds.X >= 0, $"{Named(mark)} starts at {mark.Bounds.X:0.#} in {state}");
            Assert.True(mark.Bounds.Right <= column, $"{Named(mark)} ends at {mark.Bounds.Right:0.#} of {column:0.#} in {state}");
            Assert.True(mark.Bounds.Bottom <= chart.Bounds.Height, $"{Named(mark)} hangs below the axis band in {state}");

            if (mark is TextBlock { Opacity: > 0 } line)
            {
                Assert.False(line.TextLayout.TextLines.Any(l => l.HasCollapsed), $"[{line.Text}] was trimmed in {state}");
            }
        }
    }

    /// <summary>The same tree re-placed at a new width, as a window does, with nothing rebuilt.</summary>
    [Fact]
    public void TheChartFollowsItsColumnWhenTheColumnChanges()
    {
        var panel = Laid(Typical, false, PanelDensity.Normal);
        var chart = ChartOf(panel);

        panel.Width = 360;
        PanelSampleRenderer.LayOut(panel);

        var column = 360 - 2 * (panel.BorderThickness.Left + panel.Padding.Left);
        Assert.Same(chart, ChartOf(panel));
        Assert.Equal(column, chart.Bounds.Width);
        Assert.Equal(column - PanelMetrics.ChartBarInsetDip, BarsOf(chart)[^1].Bounds.Right, 6);
    }

    // ── building the panels ─────────────────────────────────────────────────────────

    private static PanelContent Laid(string name, bool light, PanelDensity density) =>
        Laid(SampleStates.All.Single(s => s.Name == name), light, density);

    internal static PanelContent Laid(SampleState state, bool light, PanelDensity density)
    {
        UseRealFonts();

        var panel = new PanelContent(new LinuxPanelTheme(light)) { Density = density };
        panel.Populate(state.Snapshot, state.Stats, state.Account, state.Plan, state.Scope,
            SampleStates.Now, state.Boost, SampleStates.Zone, state.CachedUsage, state.View);
        PanelSampleRenderer.LayOut(panel);
        return panel;
    }

    private static PanelLocalChartView OutputChart(string name)
    {
        var state = SampleStates.All.Single(s => s.Name == name);
        return PanelSections.LocalTotals(
            state.Stats, state.Snapshot, SampleStates.Zone, state.View with { Measure = PanelMeasure.Output }).Chart;
    }

    private static List<string> Tips(PanelLocalChartView chart) =>
        chart.Columns.Where(c => c.HasBar).Select(c => $"{c.CardFigure} · {c.CardCaption}").ToList();

    /// <summary>
    /// The typical state with every one of the 31 days recorded, each as busy as its busiest, so
    /// every bar is drawn and every card is the widest that state has.
    /// </summary>
    internal static SampleState EveryDayRecorded()
    {
        var typical = SampleStates.All.Single(s => s.Name == Typical);
        var busiest = typical.Stats.DailySeries.MaxBy(d => d.OutputTokens)!;
        var series = typical.Stats.DailySeries
            .Select(d => d with { PreInstall = false, OutputTokens = busiest.OutputTokens, BilledTokens = busiest.BilledTokens })
            .ToList();

        return typical with { Stats = typical.Stats with { DailySeries = series, RecordedDays = series.Count } };
    }
    /// <summary>The surface the marks stand on: the one parent <c>today</c> can have.</summary>
    internal static Control ChartOf(Control panel) =>
        Assert.IsAssignableFrom<Control>(Words(panel, TokenText.ChartTodayLabel).GetVisualParent());

    /// <summary>The bars, oldest first: the marks with a hover card.</summary>
    internal static List<Border> BarsOf(Control chart) =>
        chart.GetVisualChildren().OfType<Border>().Where(b => ToolTip.GetTip(b) is not null).ToList();

    /// <summary>A bar's card, as one line: its figure, then its caption.</summary>
    internal static string CardWords(Border bar) =>
        string.Join(" · ", CardOf(bar).GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text));

    internal static Border CardOf(Border bar) => Assert.IsType<Border>(ToolTip.GetTip(bar));

    /// <summary>The baseline, then the week lines.</summary>
    private static List<Border> Hairlines(Control chart) =>
        chart.GetVisualChildren().OfType<Border>().Where(b => ToolTip.GetTip(b) is null).ToList();

    private static TextBlock Words(Control root, string text) =>
        root.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == text);

    private static string Named(Control mark) => mark is TextBlock line ? $"[{line.Text}]" : mark.GetType().Name;

    private static Color Hex(string key, bool light) => Color.Parse(PanelPalette.Get(key, light));

    private static Color Colour(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    /// <summary>The font setup <see cref="PanelShortScreenTests"/> runs once per process, through its own lock.</summary>
    private static void UseRealFonts() =>
        typeof(PanelShortScreenTests)
            .GetMethod("UseRealFonts", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, null);
}
