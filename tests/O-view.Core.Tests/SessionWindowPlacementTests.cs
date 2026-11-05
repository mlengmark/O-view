using OView.Core.Providers.PlanHistory;

namespace OView.Core.Tests;

public class SessionWindowPlacementTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 15, 14, 0, TimeSpan.Zero);

    /// <summary>What plan history alone would have said: a start five hours back.</summary>
    private static readonly DateTimeOffset FromPlanHistory = Now - ResetDetector.WindowLength;

    [Fact]
    public void AReportedResetAheadOfNow_PlacesTheWindowOneWindowBeforeIt()
    {
        // 2026-09-07, the reported case: reset 19:40Z, so the window began at 14:40Z.
        var reset = new DateTimeOffset(2026, 9, 7, 19, 40, 0, TimeSpan.Zero);

        var start = SessionWindowPlacement.Place(Now, reset, FromPlanHistory);

        Assert.Equal(new DateTimeOffset(2026, 9, 7, 14, 40, 0, TimeSpan.Zero), start);
    }

    [Fact]
    public void NoReportedReset_LeavesThePlanHistoryStart()
    {
        Assert.Equal(FromPlanHistory, SessionWindowPlacement.Place(Now, null, FromPlanHistory));
    }

    [Fact]
    public void AReportedResetInThePast_IsNeverUsed()
    {
        Assert.Equal(FromPlanHistory, SessionWindowPlacement.Place(Now, Now.AddMinutes(-1), FromPlanHistory));
    }

    [Fact]
    public void AReportedResetExactlyNow_HasPassed()
    {
        Assert.Equal(FromPlanHistory, SessionWindowPlacement.Place(Now, Now, FromPlanHistory));
    }

    [Fact]
    public void AReportedResetMoreThanOneWindowAhead_IsNeverUsed()
    {
        // It would put the start in the future.
        var reset = Now + ResetDetector.WindowLength + TimeSpan.FromMinutes(1);

        Assert.Equal(FromPlanHistory, SessionWindowPlacement.Place(Now, reset, FromPlanHistory));
    }

    [Fact]
    public void AReportedResetExactlyOneWindowAhead_PlacesTheStartAtNow()
    {
        var reset = Now + ResetDetector.WindowLength;

        Assert.Equal(Now, SessionWindowPlacement.Place(Now, reset, FromPlanHistory));
    }

    // ── the series never spans a reset ──────────────────────────────────────────────

    private static PlanHistorySample Sample(int minutesAgo, int fh) =>
        new(Now.AddMinutes(-minutesAgo), "org-a", fh, 5);

    [Fact]
    public void ASeriesWithoutADrop_IsLeftAlone()
    {
        PlanHistorySample[] samples = [Sample(30, 0), Sample(15, 4), Sample(0, 9)];

        Assert.Equal(samples, SessionWindowPlacement.SinceLastDrop(samples));
    }

    [Fact]
    public void ASeriesWithADrop_StartsAtTheSampleTheDropLandsOn()
    {
        PlanHistorySample[] samples = [Sample(45, 80), Sample(30, 3), Sample(15, 4), Sample(0, 9)];

        Assert.Equal(samples[1..], SessionWindowPlacement.SinceLastDrop(samples));
    }

    [Fact]
    public void WithSeveralDrops_OnlyTheLastCounts()
    {
        PlanHistorySample[] samples = [Sample(60, 90), Sample(45, 10), Sample(30, 40), Sample(15, 2), Sample(0, 6)];

        Assert.Equal(samples[3..], SessionWindowPlacement.SinceLastDrop(samples));
    }

    [Fact]
    public void ADropBelowTheThreshold_IsNoise()
    {
        PlanHistorySample[] samples = [Sample(15, 10), Sample(0, 9)];

        Assert.Equal(samples, SessionWindowPlacement.SinceLastDrop(samples));
    }

    [Fact]
    public void AnEmptySeries_StaysEmpty()
    {
        Assert.Empty(SessionWindowPlacement.SinceLastDrop([]));
    }
}
