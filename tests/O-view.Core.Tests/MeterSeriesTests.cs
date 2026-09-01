using OView.Core.Models;

namespace OView.Core.Tests;

public class MeterSeriesTests
{
    private static readonly TimeSpan SixMinutes = TimeSpan.FromMinutes(6);
    private static readonly TimeSpan TwelveMinutes = TimeSpan.FromMinutes(12);

    /// <summary>
    /// Issue #268, end to end through the detector. Plan history held one sample of a
    /// twelve-minute-old window; Claude Code's cache, six minutes newer, held the reading that
    /// showed where the meter had actually gone. Folded in, the window has two points and a
    /// 19-point rise — which is what the panel's own gauge was displaying at the time.
    /// </summary>
    [Fact]
    public void TheReportedReading_TurnsTheReportedCaseIntoAMeasurement()
    {
        var (percents, age) = MeterSeries.WithReportedReading(
            [5], TwelveMinutes, reportedPercent: 24, reportedAge: SixMinutes);

        Assert.Equal([5, 24], percents);
        Assert.Equal(SixMinutes, age);

        var result = DivergenceDetector.Evaluate(percents, outputTokensInWindow: 56_600, meterAge: age);

        Assert.Equal(DivergenceState.Consistent, result.State);
        Assert.Equal(19, result.PlanRisePoints);
        Assert.False(result.IsOffPlan);
    }

    /// <summary>
    /// The appended reading carries the series' age with it. Without this a fresh reading
    /// would be evaluated under the stale sample's age and could trip the
    /// <see cref="DivergenceState.MeterNotReporting"/> gate it disproves.
    /// </summary>
    [Fact]
    public void AppendingAlsoRefreshesTheAge()
    {
        var (_, age) = MeterSeries.WithReportedReading(
            [5], TimeSpan.FromMinutes(40), reportedPercent: 24, reportedAge: TimeSpan.FromMinutes(2));

        Assert.Equal(TimeSpan.FromMinutes(2), age);
    }

    [Fact]
    public void NoReportedReading_LeavesTheSeriesAlone()
    {
        var (percents, age) = MeterSeries.WithReportedReading(
            [5, 9], SixMinutes, reportedPercent: null, reportedAge: SixMinutes);

        Assert.Equal([5, 9], percents);
        Assert.Equal(SixMinutes, age);
    }

    /// <summary>
    /// Older than the newest sample is both useless and unsafe: the detector reads the series
    /// positionally, so an out-of-order value at the end would be read as the latest.
    /// </summary>
    [Fact]
    public void AnOlderReading_IsNotAppended()
    {
        var (percents, age) = MeterSeries.WithReportedReading(
            [5, 9], SixMinutes, reportedPercent: 7, reportedAge: TwelveMinutes);

        Assert.Equal([5, 9], percents);
        Assert.Equal(SixMinutes, age);
    }

    [Fact]
    public void AReadingOfTheSameAge_AddsNothingAndIsNotAppended()
    {
        var (percents, _) = MeterSeries.WithReportedReading(
            [5, 9], SixMinutes, reportedPercent: 11, reportedAge: SixMinutes);

        Assert.Equal([5, 9], percents);
    }

    /// <summary>
    /// Within a window the meter only rises, so a fresher-but-lower reading means a boundary
    /// fell between the two — the one thing the series must never span. Appending it would
    /// manufacture a negative rise, which reads as flat and reports as divergence: the exact
    /// false alarm this whole path exists to avoid.
    /// </summary>
    [Fact]
    public void ALowerReading_IsEvidenceOfAResetAndIsNotAppended()
    {
        var (percents, age) = MeterSeries.WithReportedReading(
            [64, 71], TwelveMinutes, reportedPercent: 3, reportedAge: SixMinutes);

        Assert.Equal([64, 71], percents);
        Assert.Equal(TwelveMinutes, age);
    }

    /// <summary>Equal is not lower — a meter that has genuinely held still still appends.</summary>
    [Fact]
    public void AnEqualReading_IsAppended()
    {
        var (percents, _) = MeterSeries.WithReportedReading(
            [6], TwelveMinutes, reportedPercent: 6, reportedAge: SixMinutes);

        Assert.Equal([6, 6], percents);
    }

    /// <summary>
    /// An empty series means no plan history for this window at all. One reported reading is
    /// still one point, so appending would only trade <c>InsufficientActivity</c> for
    /// <c>RiseNotMeasurable</c> — a different stated reason for the same silence.
    /// </summary>
    [Fact]
    public void AnEmptySeries_IsNotSeededFromTheReportedReading()
    {
        var (percents, age) = MeterSeries.WithReportedReading(
            [], TimeSpan.MaxValue, reportedPercent: 24, reportedAge: SixMinutes);

        Assert.Empty(percents);
        Assert.Equal(TimeSpan.MaxValue, age);
    }

    /// <summary>
    /// Genuine divergence survives the fold. A reported reading that agrees the meter has not
    /// moved supplies the second point and the verdict stands — this must not become a way to
    /// silence the detector.
    /// </summary>
    [Fact]
    public void AFlatReportedReading_StillDiverges()
    {
        var (percents, age) = MeterSeries.WithReportedReading(
            [6], TwelveMinutes, reportedPercent: 6, reportedAge: SixMinutes);

        var result = DivergenceDetector.Evaluate(percents, outputTokensInWindow: 69_091, meterAge: age);

        Assert.Equal(DivergenceState.Diverging, result.State);
        Assert.True(result.IsOffPlan);
    }
}
