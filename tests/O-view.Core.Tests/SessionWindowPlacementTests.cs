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

    // ── a disagreement between the reported start and the located window ────────────

    private static PlanHistorySample At(string time, int fh) =>
        new(DateTimeOffset.Parse(time + "Z"), "org-a", fh, 5);

    /// <summary>A drop between 12:00 (60%) and 12:15 (3%): the window began in between.</summary>
    private static readonly PlanHistorySample[] ADrop =
        [At("2026-09-07T11:45:00", 58), At("2026-09-07T12:00:00", 60), At("2026-09-07T12:15:00", 3)];

    private static bool Disagrees(string reportedStart, params PlanHistorySample[] samples) =>
        SessionWindowPlacement.Disagrees(
            DateTimeOffset.Parse(reportedStart + "Z"), ResetDetector.FindCurrentWindowStart(samples), samples);

    [Theory]
    [InlineData("2026-09-07T12:00:01")]
    [InlineData("2026-09-07T12:07:30")]
    [InlineData("2026-09-07T12:15:00")]
    public void AReportedStartInsideTheBracket_Agrees(string reportedStart) =>
        Assert.False(Disagrees(reportedStart, ADrop));

    [Theory]
    [InlineData("2026-09-07T11:59:00")]
    [InlineData("2026-09-07T12:15:01")]
    public void AReportedStartOutsideTheBracket_Disagrees(string reportedStart) =>
        Assert.True(Disagrees(reportedStart, ADrop));

    [Fact]
    public void AWindowNoSampleLocated_NeverDisagrees()
    {
        // A series that opens on zero has no boundary: the placement falls back, and there is
        // no inference for the reported start to contradict.
        PlanHistorySample[] samples = [At("2026-09-05T13:16:00", 0), At("2026-09-07T14:44:00", 0)];

        Assert.False(Disagrees("2026-09-07T14:40:00", samples));
    }

    /// <summary>
    /// The window began with a first use too small to move a whole-number meter: the reported
    /// start is before the rise from zero, and every sample in between reads 0.
    /// </summary>
    [Fact]
    public void AFirstUseTooSmallToMoveTheMeter_Agrees()
    {
        PlanHistorySample[] samples =
            [At("2026-09-07T14:00:00", 0), At("2026-09-07T14:15:00", 0), At("2026-09-07T14:30:00", 0),
             At("2026-09-07T14:45:00", 2)];

        // Began 14:02, three samples before the rise was seen.
        Assert.False(Disagrees("2026-09-07T14:02:00", samples));
    }

    [Fact]
    public void AReportedStartBeforeANonZeroReading_Disagrees()
    {
        PlanHistorySample[] samples =
            [At("2026-09-07T13:45:00", 9), At("2026-09-07T14:00:00", 0), At("2026-09-07T14:15:00", 0),
             At("2026-09-07T14:30:00", 2)];

        // 13:45 read 9% after the reported start: the other source's window was still running.
        Assert.True(Disagrees("2026-09-07T13:40:00", samples));
    }

    [Fact]
    public void Describe_NamesTheSourceThatPlacedTheWindow()
    {
        var reset = DateTimeOffset.Parse("2026-09-07T17:10:00Z");
        var now = DateTimeOffset.Parse("2026-09-07T12:30:00Z");
        var inferred = ResetDetector.FindCurrentWindowStart(ADrop);

        var reported = SessionWindowPlacement.Describe(now, reset, inferred, now.AddHours(-5), ADrop);
        var located = SessionWindowPlacement.Describe(now, null, inferred, now.AddHours(-5), ADrop);
        var fallback = SessionWindowPlacement.Describe(now, null, null, now.AddHours(-5), ADrop);

        Assert.Equal(WindowPlacedBy.ReportedReset, reported.PlacedBy);
        Assert.Equal(DateTimeOffset.Parse("2026-09-07T12:10:00Z"), reported.StartUtc);
        Assert.False(reported.Disagrees);
        Assert.Equal(WindowPlacedBy.PlanHistory, located.PlacedBy);
        Assert.Null(located.ReportedStartUtc);
        Assert.Equal(WindowPlacedBy.PlanHistoryFallback, fallback.PlacedBy);
        Assert.Null(fallback.Inferred);
    }

    [Fact]
    public void AReportedResetThatIsIgnored_CannotDisagree()
    {
        var now = DateTimeOffset.Parse("2026-09-07T12:30:00Z");
        var past = now.AddMinutes(-1);

        Assert.False(SessionWindowPlacement.Describe(
            now, past, ResetDetector.FindCurrentWindowStart(ADrop), now.AddHours(-5), ADrop).Disagrees);
    }

    // ── a rollover is not a disagreement ────────────────────────────────────────────

    /// <summary>
    /// One account, Desktop running. The window that began at 10:32 ends at 15:32, and the next
    /// first use at 15:33 is reported before Desktop's 15:45 sample has seen the rollover.
    /// </summary>
    private static readonly PlanHistorySample[] Rollover =
        [At("2026-09-07T10:30:00", 40), At("2026-09-07T10:45:00", 2), At("2026-09-07T15:30:00", 50)];

    [Fact]
    public void TheNextWindowReportedBeforeDesktopSeesIt_IsNotADisagreement()
    {
        var samples = Rollover;
        var now = DateTimeOffset.Parse("2026-09-07T15:40:00Z");
        var reportedStart = DateTimeOffset.Parse("2026-09-07T15:33:00Z");
        var bracket = ResetDetector.FindCurrentWindowStart(samples);

        var window = SessionWindowPlacement.Describe(
            now, reportedStart + ResetDetector.WindowLength, bracket, now.AddHours(-5), samples);

        Assert.False(SessionWindowPlacement.Disagrees(reportedStart, bracket, samples));
        Assert.Null(window.Inferred);
        Assert.False(window.Disagrees);
        Assert.Equal(WindowPlacedBy.ReportedReset, window.PlacedBy);
    }

    [Fact]
    public void ADropSeenAfterTheRollover_AgreesOnceDesktopHasCaughtUp()
    {
        PlanHistorySample[] samples = [.. Rollover, At("2026-09-07T15:45:00", 3)];

        Assert.False(Disagrees("2026-09-07T15:33:00", samples));
    }

    [Theory]
    [InlineData("2026-09-07T10:46:00", true)]   // after the bracket, well inside its window
    [InlineData("2026-09-07T15:29:59", true)]   // the last moment the old window can still run
    [InlineData("2026-09-07T15:30:00", false)]  // its earliest edge plus a window: over
    public void TheRealSignalSurvivesUntilTheLocatedWindowHasEnded(string reportedStart, bool disagrees) =>
        Assert.Equal(disagrees, Disagrees(reportedStart, Rollover[..2]));

    /// <summary>
    /// Desktop off from 22:00 with the meter on 40%, Claude Code starting the window at 09:10,
    /// Desktop's first sample 09:20 reading 3%: a bracket wider than a window, holding the
    /// reported start. The two agree and the bracket is shown.
    /// </summary>
    [Fact]
    public void AReportedStartInsideABracketWiderThanAWindow_KeepsTheBracket()
    {
        PlanHistorySample[] samples =
            [At("2026-09-06T21:45:00", 38), At("2026-09-06T22:00:00", 40), At("2026-09-07T09:20:00", 3)];
        var now = DateTimeOffset.Parse("2026-09-07T09:30:00Z");
        var reportedStart = DateTimeOffset.Parse("2026-09-07T09:10:00Z");
        var bracket = ResetDetector.FindCurrentWindowStart(samples);

        var window = SessionWindowPlacement.Describe(
            now, reportedStart + ResetDetector.WindowLength, bracket, now.AddHours(-5), samples);

        Assert.Equal(bracket, window.Inferred);
        Assert.False(window.Disagrees);
    }
}
