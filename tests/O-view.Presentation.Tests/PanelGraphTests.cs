using OView.Core.Models;
using OView.Presentation.Panel;
using OView.Presentation.Text;

namespace OView.Presentation.Tests;

/// <summary>
/// The 31-day graph, built once for both heads.
///
/// <para>These assert what used to be decided inside the Windows panel and was therefore
/// missing from the Linux one: which week a day is shaded against, where a reset line falls
/// inside a column, which days draw no bar at all, and what every hover card says. The
/// numbers are fractions, never pixels — turning one into a height is the head's, and a test
/// that asserted pixels here would be asserting the Windows chart's size.</para>
/// </summary>
public class PanelGraphTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    /// <summary>
    /// A zone an hour ahead of UTC that observes DST, named the way the OS names it — a
    /// hard-coded IANA id finds nothing on Windows, and the placement these tests are about is
    /// exactly what a UTC-only test cannot assert.
    /// </summary>
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "W. Europe Standard Time" : "Europe/Berlin");

    private static readonly DateTimeOffset Now = new(2026, 7, 28, 10, 47, 0, TimeSpan.Zero);

    /// <summary>A Tuesday, a quarter of the way into its column rather than on the edge.</summary>
    private static readonly DateTimeOffset WeeklyReset = new(2026, 7, 28, 6, 28, 57, TimeSpan.Zero);

    private static UsageSnapshot WithReset(DateTimeOffset nextResetUtc) =>
        new(DataSource.Live, 25, 3, Now.AddHours(3), Now, nextResetUtc, TimeSpan.FromDays(7));

    /// <summary>No reported reset and no cadence: the Monday fallback.</summary>
    private static UsageSnapshot WithoutReset() => new(DataSource.Live, 25, 3, Now.AddHours(3), Now);

    private static PanelStatistics Stats(params DayUsage[] series) =>
        new(TokensToday: 0, EstTodayUsd: null,
            Tokens31Days: series.Sum(d => d.OutputTokens), Est31DaysUsd: null,
            RecordedDays: series.Count(d => !d.PreInstall), WindowDays: series.Length,
            DailySeries: series,
            CreditTokens31Days: 0, EstCredit31DaysUsd: null);

    private static DayUsage Day(int year, int month, int day, long tokens, bool preInstall = false) =>
        new(new DateOnly(year, month, day), tokens, preInstall);

    /// <summary>Ten days, Sunday 19 July to Tuesday 28 July 2026, with one clear peak.</summary>
    private static DayUsage[] TenDays() =>
    [
        Day(2026, 7, 19, 10_000),
        Day(2026, 7, 20, 20_000),
        Day(2026, 7, 21, 30_000),
        Day(2026, 7, 22, 40_000),
        Day(2026, 7, 23, 50_000),
        Day(2026, 7, 24, 60_000),
        Day(2026, 7, 25, 70_000),
        Day(2026, 7, 26, 80_000),
        Day(2026, 7, 27, 9_000),
        Day(2026, 7, 28, 3_000),
    ];

    // ── the columns ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// One column per day, in the series' own order, each dated. Both heads index the two
    /// lists against each other — a boundary names a column by number — so a view that
    /// dropped or reordered a day would move every gridline.
    /// </summary>
    [Fact]
    public void EveryDayGetsOneColumnInOrderWithItsDate()
    {
        var graph = PanelSections.Graph(Stats(TenDays()), WithReset(WeeklyReset), Utc);

        Assert.Equal(10, graph.Columns.Count);
        Assert.Equal("19 Jul", graph.Columns[0].DateLabel);
        Assert.Equal("28 Jul", graph.Columns[^1].DateLabel);
    }

    /// <summary>The heading names the window, and is the one both heads print.</summary>
    [Fact]
    public void TheHeadingNamesTheWindow()
    {
        var graph = PanelSections.Graph(Stats(TenDays()), WithReset(WeeklyReset), Utc);

        Assert.Equal(PanelText.UsageWindowHeading(10), graph.Heading);
    }

    /// <summary>
    /// Height is a fraction of the busiest day in the whole window, so the bars are
    /// comparable across the chart — it is the colour, not the height, that is normalised per
    /// week.
    /// </summary>
    [Fact]
    public void HeightIsAFractionOfTheBusiestDayInTheWindow()
    {
        var graph = PanelSections.Graph(Stats(TenDays()), WithReset(WeeklyReset), Utc);

        Assert.Equal(1.0, graph.Columns[7].Height, 6);       // 80,000 — the window's peak
        Assert.Equal(0.125, graph.Columns[0].Height, 6);     // 10,000 of 80,000
    }

    /// <summary>
    /// Intensity is normalised within the day's own plan week, so the busiest day of every
    /// week is the darkest one in that band (issue #5) — which is a different number from the
    /// height beside it. The reset falls on the 28th, so the 19th and 20th are one band, the
    /// 21st to the 27th the next, and the 28th a band of its own.
    /// </summary>
    [Fact]
    public void IntensityIsNormalisedWithinTheDaysOwnWeek()
    {
        var graph = PanelSections.Graph(Stats(TenDays()), WithReset(WeeklyReset), Utc);

        // 19 July: half of its own week's 20,000 …
        Assert.Equal(0.5, graph.Columns[0].Intensity, 6);
        // … but an eighth of the window's 80,000, which is what its height says.
        Assert.Equal(0.125, graph.Columns[0].Height, 6);

        // 28 July: the only day after the reset, so it is its own week's peak however small
        // it is against the window.
        Assert.Equal(1.0, graph.Columns[9].Intensity, 6);
        Assert.Equal(3_000 / 80_000.0, graph.Columns[9].Height, 6);

        // 27 July, in the big week: a tenth of the 26th's 80,000.
        Assert.Equal(9_000 / 80_000.0, graph.Columns[8].Intensity, 6);
    }

    /// <summary>
    /// The bar's hover card: the day's tokens, with the day written out in full under them —
    /// which the date label, squeezed under one of 31 columns, cannot be.
    /// </summary>
    [Fact]
    public void EveryBarCarriesItsFigureAndItsDayInFull()
    {
        var graph = PanelSections.Graph(Stats(TenDays()), WithReset(WeeklyReset), Utc);

        Assert.Equal(TokenText.DayBarTokens(30_000), graph.Columns[2].CardFigure);
        Assert.Equal("Tuesday 21 July", graph.Columns[2].CardCaption);
    }

    // ── the days with nothing to show ───────────────────────────────────────────────

    /// <summary>
    /// A day before the first recorded one is an empty column: no bar, nothing to hover, and
    /// its date drawn muted. A zero-height bar would claim a measured day of no usage on a day
    /// that was never measured (rule 6).
    /// </summary>
    [Fact]
    public void ADayBeforeTheFirstRecordedOneIsAnEmptyColumn()
    {
        var graph = PanelSections.Graph(
            Stats(Day(2026, 7, 27, 0, preInstall: true), Day(2026, 7, 28, 50_000)),
            WithReset(WeeklyReset),
            Utc);

        var before = graph.Columns[0];
        Assert.False(before.Recorded);
        Assert.False(before.HasBar);
        Assert.Equal(0, before.Height);
        Assert.Equal("", before.CardFigure);
        Assert.Equal("", before.CardCaption);
        Assert.Equal("27 Jul", before.DateLabel);
    }

    /// <summary>
    /// A recorded day that used nothing has no bar either — there is nothing to draw — but it
    /// is <b>not</b> marked unrecorded: the store was running, so the zero is a genuine one
    /// and its date is not dimmed (issue #142).
    /// </summary>
    [Fact]
    public void ARecordedDayWithNoUsageDrawsNoBarButIsStillRecorded()
    {
        var graph = PanelSections.Graph(
            Stats(Day(2026, 7, 27, 0), Day(2026, 7, 28, 50_000)), WithReset(WeeklyReset), Utc);

        Assert.True(graph.Columns[0].Recorded);
        Assert.False(graph.Columns[0].HasBar);
    }

    /// <summary>
    /// A store with no usage at all divides by nothing and draws nothing, rather than throwing
    /// on the way to a chart with no bars in it.
    /// </summary>
    [Fact]
    public void AWindowWithNoUsageHasColumnsAndNoBars()
    {
        var graph = PanelSections.Graph(
            Stats(Day(2026, 7, 27, 0, preInstall: true), Day(2026, 7, 28, 0, preInstall: true)),
            WithReset(WeeklyReset),
            Utc);

        Assert.Equal(2, graph.Columns.Count);
        Assert.All(graph.Columns, c => Assert.False(c.HasBar));
    }

    /// <summary>
    /// An empty series is a chart neither head draws — and not an error state, so the heading
    /// is still there for the banner above it to sit under.
    /// </summary>
    [Fact]
    public void AnEmptySeriesDrawsNoChartAtAll()
    {
        var graph = PanelSections.Graph(Stats(), WithReset(WeeklyReset), Utc);

        Assert.Empty(graph.Columns);
        Assert.Empty(graph.Boundaries);
        Assert.Equal(PanelText.UsageWindowHeading(0), graph.Heading);
    }

    // ── the week-boundary gridlines ─────────────────────────────────────────────────

    /// <summary>
    /// A reported reset gives a line inside the column it falls in, at its true position
    /// through the day — 06:28:57 of a 24-hour day, not snapped to either edge. Snapping would
    /// assert a midnight boundary the data does not have.
    /// </summary>
    [Fact]
    public void AReportedResetLandsAtItsTruePositionInsideItsColumn()
    {
        var graph = PanelSections.Graph(Stats(TenDays()), WithReset(WeeklyReset), Utc);

        var tuesday = graph.Boundaries.Single(b => b.Column == 9);
        Assert.Equal((6 * 3600 + 28 * 60 + 57) / 86_400.0, tuesday.FractionOfDay, 6);
    }

    /// <summary>
    /// Past boundaries are derived by stepping the cadence back, so the window is divided for
    /// its whole length rather than only from the first reset O-view was running for.
    /// </summary>
    [Fact]
    public void PastBoundariesAreSteppedBackThroughTheWholeWindow()
    {
        var graph = PanelSections.Graph(Stats(TenDays()), WithReset(WeeklyReset), Utc);

        // 21 July and 28 July: the two the ten-day window contains, and the 21st is a reset
        // O-view was not running for.
        Assert.Equal(new[] { 2, 9 }, graph.Boundaries.Select(b => b.Column).ToArray());
    }

    /// <summary>
    /// The plan line's hover card states the reset's own local time above what it marks. The
    /// caption is asserted against <see cref="PanelText"/> rather than re-typed, so rewording
    /// it does not fail here — only a line choosing the wrong sentence does.
    /// </summary>
    [Fact]
    public void ThePlanLineNamesItsResetTime()
    {
        var graph = PanelSections.Graph(Stats(TenDays()), WithReset(WeeklyReset), Utc);

        var tuesday = graph.Boundaries.Single(b => b.Column == 9);
        Assert.Equal("Tue 28 Jul · 06:28", tuesday.CardFigure);
        Assert.Equal(PanelText.WeeklyResetMarker, tuesday.CardCaption);
    }

    /// <summary>
    /// With no reset observed the lines fall back to local Mondays, land on a column edge
    /// because midnight is one, and <b>say on hover that they are a calendar reference</b> —
    /// the honest blank rather than a silent substitution (rule 6). They carry no time: there
    /// is no observed instant to state.
    /// </summary>
    [Fact]
    public void TheMondayFallbackSaysItIsOneAndStatesNoTime()
    {
        var graph = PanelSections.Graph(Stats(TenDays()), WithoutReset(), Utc);

        // 20 and 27 July, the two Mondays in range.
        Assert.Equal(new[] { 1, 8 }, graph.Boundaries.Select(b => b.Column).ToArray());
        Assert.All(graph.Boundaries, b =>
        {
            Assert.Equal(0, b.FractionOfDay);
            Assert.Equal("", b.CardFigure);
            Assert.Equal(PanelText.CalendarWeekMarker, b.CardCaption);
        });
    }

    /// <summary>
    /// A boundary on the chart's own left edge is dropped: a line there reads as a border
    /// rather than as a mark on the data. Only there — a midnight boundary anywhere else is a
    /// real column edge inside the chart and is drawn.
    /// </summary>
    [Fact]
    public void ABoundaryOnTheChartsLeftEdgeIsNotDrawn()
    {
        // 20 July 2026 is a Monday, so the fallback puts a boundary on the first column.
        var graph = PanelSections.Graph(
            Stats(Day(2026, 7, 20, 10_000), Day(2026, 7, 27, 10_000)), WithoutReset(), Utc);

        Assert.Equal(new[] { 1 }, graph.Boundaries.Select(b => b.Column).ToArray());
    }

    /// <summary>A boundary outside the plotted days has no column to sit in.</summary>
    [Fact]
    public void ABoundaryOutsideTheWindowIsNotDrawn()
    {
        var graph = PanelSections.Graph(
            Stats(Day(2026, 7, 27, 10_000), Day(2026, 7, 28, 10_000)),
            WithReset(new DateTimeOffset(2026, 8, 11, 6, 28, 57, TimeSpan.Zero)),
            Utc);

        // Stepping 7 days back from 11 August passes 4 August, which is past the last column
        // and so has none, and lands on 28 July, which is the second of the two.
        Assert.Equal(new[] { 1 }, graph.Boundaries.Select(b => b.Column).ToArray());
    }

    // ── the reader's own clock ──────────────────────────────────────────────────────

    /// <summary>
    /// The columns are local days, so everything drawn over them is placed in the same frame:
    /// the same instant lands in a different column, at a different fraction, in a zone an
    /// hour ahead. Positioning as though every column were 24 UTC hours is what drifts a
    /// gridline off the bar it annotates.
    /// </summary>
    [Fact]
    public void TheGridlineFollowsTheReadersOwnDay()
    {
        var midnightUtc = new DateTimeOffset(2026, 7, 28, 0, 0, 0, TimeSpan.Zero);
        var series = Stats(Day(2026, 7, 27, 10_000), Day(2026, 7, 28, 10_000));

        var utc = Assert.Single(PanelSections.Graph(series, WithReset(midnightUtc), Utc).Boundaries);
        Assert.Equal(1, utc.Column);
        Assert.Equal(0, utc.FractionOfDay);

        // 02:00 on the 28th in Berlin — a twelfth of the way into that day, not the start of it.
        var local = Assert.Single(PanelSections.Graph(series, WithReset(midnightUtc), Berlin).Boundaries);
        Assert.Equal(1, local.Column);
        Assert.Equal(2 / 24.0, local.FractionOfDay, 6);
    }

    /// <summary>
    /// The fraction is taken against the day's real length. On the 23-hour spring-forward day
    /// an instant 11 hours in is past halfway, which dividing by a flat 24 would deny — and
    /// would go on denying for every day after it.
    /// </summary>
    [Fact]
    public void AShortenedDayIsMeasuredAgainstItsOwnLength()
    {
        // 29 March 2026 is Berlin's spring-forward Sunday: 23 hours long, starting 23:00 UTC
        // on the 28th. 10:00 UTC is 11 hours into it.
        var graph = PanelSections.Graph(
            Stats(Day(2026, 3, 28, 10_000), Day(2026, 3, 29, 10_000)),
            WithReset(new DateTimeOffset(2026, 3, 29, 10, 0, 0, TimeSpan.Zero)),
            Berlin);

        var boundary = Assert.Single(graph.Boundaries);
        Assert.Equal(1, boundary.Column);
        Assert.Equal(11 / 23.0, boundary.FractionOfDay, 6);
    }
}
