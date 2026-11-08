using OView.Core.Models;
using OView.Presentation.Panel;
using OView.Presentation.Samples;
using OView.Presentation.Text;

namespace OView.Presentation.Tests;

/// <summary>
/// The local block's totals face and footer. Each test pins one of the four decisions the
/// Output | Overall switch turns on, which total, which estimate, which chart series and which
/// wording, so a head that picked its own would have nothing here to agree with.
/// </summary>
public class PanelLocalTotalsTests
{
    private static readonly PanelViewChoice Output = default;
    private static readonly PanelViewChoice Overall = new(PanelMeasure.Overall);

    private static SampleState State(string name) => SampleStates.All.Single(s => s.Name == name);

    private static PanelLocalTotalsView Totals(string name, PanelViewChoice view) =>
        Totals(State(name).Stats, State(name).Snapshot, view);

    private static PanelLocalTotalsView Totals(PanelStatistics stats, UsageSnapshot snapshot, PanelViewChoice view) =>
        PanelSections.LocalTotals(stats, snapshot, SampleStates.Zone, view);

    private static EstimatedUsd Priced(decimal? usd) =>
        new(usd, usd is null ? UsageValueStatus.Unavailable : UsageValueStatus.Estimated);

    // ── which total ─────────────────────────────────────────────────────────────────

    [Fact]
    public void OutputCountsOutputTokensAlone()
    {
        var totals = Totals("typical", Output);

        Assert.Equal("last 31 days · 6.8M output", totals.Subtitle);
        Assert.Equal(["", "Today", "31 days"], totals.Table.Headers);

        var row = totals.Table.Rows[0];
        Assert.Equal(PanelLocalRowKind.Figure, row.RowKind);
        Assert.Equal(TokenText.OutputTokensRowLabel, row.Label);
        Assert.Equal(["128.6K", "6.8M"], row.Cells);
    }

    [Fact]
    public void OverallCountsEverythingBilledWithAShareForEachWindow()
    {
        var totals = Totals("typical", Overall);

        Assert.Equal("last 31 days · 684.6M tokens", totals.Subtitle);
        Assert.Equal(["Kind", "Today", "Share", "31 days", "Share"], totals.Table.Headers);
        Assert.Equal(
            ["output", "input", "cache write", "cache read", "Total"],
            totals.Table.Rows.Where(r => r.RowKind != PanelLocalRowKind.Value).Select(r => r.Label));

        var output = totals.Table.Rows[0];
        Assert.Equal(TokenKind.Output, output.Kind);
        Assert.Equal(4, output.Cells.Count);
        Assert.Equal("128.6K", output.Cells[0]);
        Assert.Equal("6.8M", output.Cells[2]);
    }

    [Fact]
    public void TheOverallTotalRowIsOneHundredPercentOfEachWindow()
    {
        var total = Totals("typical", Overall).Table.Rows.Single(r => r.RowKind == PanelLocalRowKind.Total);

        Assert.Equal(["12.7M", "100%", "684.6M", "100%"], total.Cells);
        Assert.Null(total.Kind);
    }

    // ── which estimate ──────────────────────────────────────────────────────────────

    [Fact]
    public void OutputPricesTheOutputAloneAndOverallPricesEverything()
    {
        var stats = State("typical").Stats;

        var output = Totals("typical", Output).Table.Rows.Single(r => r.RowKind == PanelLocalRowKind.Value);
        var overall = Totals("typical", Overall).Table.Rows.Single(r => r.RowKind == PanelLocalRowKind.Value);

        Assert.Equal(["$3.22", "$169.28"], output.Cells);
        Assert.Equal(["$9.36", "$492.52"], overall.Cells);
        Assert.Equal(UsageFormatter.Usd(stats.EstTodayOutputValue.Value), output.Cells[0]);
        Assert.Equal(UsageFormatter.Usd(stats.EstTodayValue.Value), overall.Cells[0]);
    }

    [Fact]
    public void TheOverallValueIsOnePillAcrossEachPairOfColumns()
    {
        var overall = Totals("typical", Overall).Table.Rows.Single(r => r.RowKind == PanelLocalRowKind.Value);
        var output = Totals("typical", Output).Table.Rows.Single(r => r.RowKind == PanelLocalRowKind.Value);

        Assert.Equal(2, overall.CellSpan);
        Assert.Equal(1, output.CellSpan);
    }

    [Fact]
    public void AnUnpricedFigureReadsUnknownAndNeverZero()
    {
        var stats = State("typical").Stats with
        {
            EstTodayOutputValue = Priced(null),
            Est31DaysOutputValue = Priced(null),
            EstTodayValue = Priced(null),
            Est31DaysValue = Priced(null),
        };

        foreach (var view in new[] { Output, Overall })
        {
            var value = Totals(stats, State("typical").Snapshot, view).Table.Rows
                .Single(r => r.RowKind == PanelLocalRowKind.Value);

            Assert.Equal(["unknown", "unknown"], value.Cells);
        }
    }

    // ── which wording ───────────────────────────────────────────────────────────────

    [Fact]
    public void ValueReadsEstValueWhileOnPlanInBothViews()
    {
        Assert.Equal("Est. value of output", ValueLabel("typical", Output));
        Assert.Equal("Est. value", ValueLabel("typical", Overall));
    }

    [Fact]
    public void ValueReadsEstSpendWhileOffPlanInBothViews()
    {
        // F19: the relabel the tiles make while the window is off-plan follows the figure
        // into the table, or the same money would be a value in one place and spend in another.
        Assert.Equal("Est. spend on output", ValueLabel("offplan-on", Output));
        Assert.Equal("Est. spend", ValueLabel("offplan-on", Overall));
    }

    private static string ValueLabel(string state, PanelViewChoice view) =>
        Totals(state, view).Table.Rows.Single(r => r.RowKind == PanelLocalRowKind.Value).Label;

    [Fact]
    public void TheSwitchLabelsAreOutputAndOverall()
    {
        Assert.Equal("Output", TokenText.MeasureLabel(PanelMeasure.Output));
        Assert.Equal("Overall", TokenText.MeasureLabel(PanelMeasure.Overall));
    }

    // ── which chart series ──────────────────────────────────────────────────────────

    private static PanelStatistics TwoDays() => State("typical").Stats with
    {
        DailySeries =
        [
            new DayUsage(new DateOnly(2026, 7, 27), 10, PreInstall: false, BilledTokens: 100),
            new DayUsage(new DateOnly(2026, 7, 28), 40, PreInstall: false, BilledTokens: 50),
        ],
    };

    [Fact]
    public void TheChartBarsFollowTheMeasure()
    {
        var snapshot = State("typical").Snapshot;

        var output = Totals(TwoDays(), snapshot, Output).Chart.Columns;
        var overall = Totals(TwoDays(), snapshot, Overall).Chart.Columns;

        Assert.Equal([0.25, 1.0], output.Select(c => c.Height));
        Assert.Equal([1.0, 0.5], overall.Select(c => c.Height));
    }

    [Fact]
    public void TheHoverFigureCountsWhatTheBarCounts()
    {
        var snapshot = State("typical").Snapshot;

        Assert.Equal("10 tokens", Totals(TwoDays(), snapshot, Output).Chart.Columns[0].CardFigure);
        Assert.Equal("100 tokens", Totals(TwoDays(), snapshot, Overall).Chart.Columns[0].CardFigure);
    }

    [Fact]
    public void DaysBeforeInstallDrawNoBarInEitherView()
    {
        foreach (var view in new[] { Output, Overall })
        {
            var columns = Totals("typical", view).Chart.Columns;

            Assert.Equal(21, columns.Count(c => !c.Recorded));
            Assert.All(columns.Where(c => !c.Recorded), c => Assert.False(c.HasBar));
        }
    }

    [Fact]
    public void WeekMarksStandOnEveryMondayWithTheDateOfItsColumn()
    {
        var s = State("typical");
        var graph = PanelSections.Graph(s.Stats, s.Snapshot, SampleStates.Zone);
        var chart = Totals("typical", Output).Chart;

        var mondays = s.Stats.DailySeries
            .Select((d, i) => (d, i)).Where(t => t.d.Date.DayOfWeek == DayOfWeek.Monday).Select(t => t.i);

        Assert.NotEmpty(chart.WeekMarks);
        Assert.Equal(mondays, chart.WeekMarks.Select(m => m.Column));
        Assert.All(chart.WeekMarks, m => Assert.Equal(graph.Columns[m.Column].DateLabel, m.DateLabel));
        Assert.Equal("today", chart.TodayLabel);
    }

    [Fact]
    public void WeekMarksStayOnMondaysWhenThePlansWeeklyResetIsNotOne()
    {
        // The sample's reset is a Tuesday morning: the graph draws its lines there, this
        // chart still draws calendar weeks.
        var s = State("typical");
        Assert.NotEqual(DayOfWeek.Monday, s.Snapshot.WeeklyResetAtUtcValue.Value!.Value.DayOfWeek);
        Assert.NotEmpty(PanelSections.Graph(s.Stats, s.Snapshot, SampleStates.Zone).Boundaries);

        var chart = Totals("typical", Output).Chart;

        Assert.All(chart.WeekMarks, m =>
            Assert.Equal(DayOfWeek.Monday, s.Stats.DailySeries[m.Column].Date.DayOfWeek));
        Assert.Equal(
            s.Stats.DailySeries.Count(d => d.Date.DayOfWeek == DayOfWeek.Monday), chart.WeekMarks.Count);
    }

    [Fact]
    public void TheNoDataStatesReadZeroAndUnknownInTheOutputRows()
    {
        Assert.Equal(["$0.00", "$0.00"], OutputValue("no-local-transcripts"));
        Assert.Equal(["unknown", "unknown"], OutputValue("no-data"));
    }

    private static IReadOnlyList<string> OutputValue(string state) =>
        Totals(state, Output).Table.Rows.Single(r => r.RowKind == PanelLocalRowKind.Value).Cells;

    [Fact]
    public void TheChartKeepsItsWeekLinesWhateverTheMeasure()
    {
        Assert.Equal(
            Totals("typical", Output).Chart.WeekMarks,
            Totals("typical", Overall).Chart.WeekMarks);
    }

    [Fact]
    public void TheOutputViewOfTheChartIsTheGraphThePanelAlreadyDraws()
    {
        var s = State("typical");

        Assert.Equal(
            PanelSections.Graph(s.Stats, s.Snapshot, SampleStates.Zone).Columns,
            Totals("typical", Output).Chart.Columns);
    }

    // ── caveats ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void ThePartialHistoryCaveatIsTheWindowsAndIsTheSameInBothViews()
    {
        Assert.Equal("10 of 31 days recorded", Totals("typical", Output).WindowCaveat);
        Assert.Equal("10 of 31 days recorded", Totals("typical", Overall).WindowCaveat);
    }

    // ── the footer ──────────────────────────────────────────────────────────────────

    [Fact]
    public void TheFooterNamesTheSurfacesTheScanFound()
    {
        Assert.Equal(
            "Claude Code transcripts only; chat, cloud and Cowork sessions not counted.",
            PanelSections.LocalFooter(State("cli-only").Scope, Output).Caveat);

        Assert.Equal(
            "Claude Code and Cowork transcripts only; chat and cloud sessions not counted.",
            PanelSections.LocalFooter(State("typical").Scope, Output).Caveat);
    }

    [Fact]
    public void TheFooterClaimsNothingAboutSourcesItNeverFound()
    {
        Assert.Equal(
            "Chat and cloud sessions not counted.",
            PanelSections.LocalFooter(State("no-local-transcripts").Scope, Output).Caveat);
        Assert.Equal("Chat and cloud sessions not counted.", PanelSections.LocalFooter(null, Output).Caveat);
    }

    [Fact]
    public void TheSwitchMarksTheCurrentMeasureAndGivesBackTheRestOfTheView()
    {
        var current = new PanelViewChoice(PanelMeasure.Output, PanelFace.Models, BannerHidden: true);

        var options = PanelSections.LocalFooter(null, current).Options;

        Assert.Equal(["Output", "Overall"], options.Select(o => o.Label));
        Assert.Equal([true, false], options.Select(o => o.Selected));
        Assert.Equal(
            new PanelViewChoice(PanelMeasure.Overall, PanelFace.Models, BannerHidden: true),
            options[1].Choice);
    }

    [Fact]
    public void AFreshInstallOpensOnOutputTotalsWithTheBannerShown()
    {
        var fresh = default(PanelViewChoice);

        Assert.Equal(PanelMeasure.Output, fresh.Measure);
        Assert.Equal(PanelFace.Totals, fresh.Face);
        Assert.False(fresh.BannerHidden);
    }
}
