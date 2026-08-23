using OView.Core.Providers.PlanHistory;

namespace OView.Core.Tests;

public class ResetDetectorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 20, 8, 0, 0, TimeSpan.Zero);
    private const string Org = "org-a";

    private static PlanHistorySample At(int minutes, int fh) =>
        new(T0.AddMinutes(minutes), Org, fh, 0);

    [Fact]
    public void NoDrop_ReturnsNull()
    {
        var samples = new[] { At(0, 3), At(5, 5), At(10, 8), At(15, 8) };

        Assert.Null(ResetDetector.FindLastDrop(samples));
    }

    [Fact]
    public void EmptyAndSingleSampleSeries_ReturnNull()
    {
        Assert.Null(ResetDetector.FindLastDrop([]));
        Assert.Null(ResetDetector.FindLastDrop([At(0, 50)]));
    }

    [Fact]
    public void DropOfTwoPoints_IsDetected()
    {
        var samples = new[] { At(0, 16), At(5, 14) };

        Assert.Equal(T0.AddMinutes(5), ResetDetector.FindLastDrop(samples));
    }

    [Fact]
    public void DropOfOnePoint_IsNoise_NotDetected()
    {
        var samples = new[] { At(0, 16), At(5, 15) };

        Assert.Null(ResetDetector.FindLastDrop(samples));
    }

    [Fact]
    public void ResetNotReachingZero_IsStillDetected()
    {
        // Observed in the real file: 16% -> 1% because new usage began immediately.
        var samples = new[] { At(0, 16), At(5, 1) };

        Assert.Equal(T0.AddMinutes(5), ResetDetector.FindLastDrop(samples));
    }

    [Fact]
    public void MultipleDrops_LastOneWins()
    {
        var samples = new[] { At(0, 16), At(5, 1), At(300, 31), At(305, 0) };

        Assert.Equal(T0.AddMinutes(305), ResetDetector.FindLastDrop(samples));
    }

    // ── the current window ──────────────────────────────────────────────────────────

    [Fact]
    public void PredictNextReset_NoWindow_ReturnsNull_NeverGuesses()
    {
        Assert.Null(ResetDetector.PredictNextReset(null, T0));
    }

    /// <summary>A drop seen while Desktop was sampling: the window starts at the drop.</summary>
    [Fact]
    public void AnObservedDropStartsTheWindow()
    {
        var samples = new[] { At(0, 60), At(5, 1), At(10, 4) };

        var start = ResetDetector.FindCurrentWindowStart(samples);

        Assert.NotNull(start);
        Assert.Equal(T0.AddMinutes(5), start.LatestUtc);
        Assert.Equal(T0.AddMinutes(5).AddHours(5), start.ResetAtUtc);
    }

    /// <summary>
    /// The reported case (GitHub issue #180). A drop, then a long sampling gap, then a run
    /// that begins at zero and climbs. There is <b>no drop</b> in the new run — the meter had
    /// already reset while Desktop was closed — so the old detector stayed anchored on the
    /// pre-gap drop and stepped it forward on a five-hour grid.
    /// </summary>
    [Fact]
    public void AWindowThatBeganDuringASamplingGapIsFoundWithoutADrop()
    {
        var samples = new[]
        {
            At(0, 78),
            At(15, 0),                       // the old window resets, then Desktop closes
            At(2 * 24 * 60, 0),              // two days later: back, still nothing used
            At(2 * 24 * 60 + 15, 7),         // first use — THIS starts the current window
            At(2 * 24 * 60 + 30, 16),
            At(2 * 24 * 60 + 45, 26),
        };

        var start = ResetDetector.FindCurrentWindowStart(samples);

        Assert.NotNull(start);
        Assert.Equal(T0.AddMinutes(2 * 24 * 60 + 15), start.LatestUtc);

        // Not the pre-gap drop stepped forward, which is what produced 22:47 against
        // Desktop's 21:01 on the machine this was reported from.
        var reset = ResetDetector.PredictNextReset(start, T0.AddMinutes(2 * 24 * 60 + 45));
        Assert.Equal(T0.AddMinutes(2 * 24 * 60 + 15).AddHours(5), reset);
    }

    /// <summary>
    /// The bracket: the start fell after the last zero and at or before the first use, so it
    /// is known to one sampling interval and no better. Rendering it to the minute would be
    /// a fabricated number (rule 6).
    /// </summary>
    [Fact]
    public void AGapInferredStartIsBracketedByTheSamplesStraddlingIt()
    {
        var samples = new[] { At(0, 0), At(45, 9) };

        var start = ResetDetector.FindCurrentWindowStart(samples);

        Assert.NotNull(start);
        Assert.Equal(T0, start.EarliestUtc);
        Assert.Equal(T0.AddMinutes(45), start.LatestUtc);
        Assert.Equal(TimeSpan.FromMinutes(45), start.Uncertainty);
    }

    // ── what must NOT happen ────────────────────────────────────────────────────────

    /// <summary>
    /// The regression itself. A window whose end has passed yields <c>unknown</c>, never a
    /// grid-stepped guess — the old code always produced a confident time and was wrong by
    /// up to five hours.
    /// </summary>
    [Fact]
    public void AnExpiredWindowIsUnknown_NotSteppedForward()
    {
        var start = new SessionWindowStart(T0, T0.AddMinutes(5));

        Assert.Null(ResetDetector.PredictNextReset(start, T0.AddHours(11)));
        Assert.Null(ResetDetector.PredictNextReset(start, T0.AddHours(6)));
    }

    /// <summary>
    /// <c>fh = 0</c> means nothing has been used, and the window starts on first use — so no
    /// window is running and there is no reset to predict. Reporting one would describe a
    /// window that has not begun.
    /// </summary>
    [Fact]
    public void NoWindowIsRunningWhileTheMeterReadsZero()
    {
        var samples = new[] { At(0, 60), At(5, 0), At(10, 0) };

        Assert.Null(ResetDetector.FindCurrentWindowStart(samples));
    }

    /// <summary>A series that only ever rises began before the data; that is unknown, not zero.</summary>
    [Fact]
    public void ASeriesWithNoBoundaryYieldsNoWindow()
    {
        var samples = new[] { At(0, 12), At(5, 20), At(10, 31) };

        Assert.Null(ResetDetector.FindCurrentWindowStart(samples));
    }

    [Fact]
    public void EmptyAndSingleSampleSeries_YieldNoWindow()
    {
        Assert.Null(ResetDetector.FindCurrentWindowStart([]));
        Assert.Null(ResetDetector.FindCurrentWindowStart([At(0, 50)]));
    }

    // ── the common case is untouched ────────────────────────────────────────────────

    /// <summary>
    /// Continuous usage across a boundary still behaves exactly as before: the drop is the
    /// start, and the reset is five hours after it. The fix must not retune the case that
    /// was already right.
    /// </summary>
    [Fact]
    public void ContinuousUsageAcrossABoundaryIsUnchanged()
    {
        var samples = new[] { At(0, 20), At(295, 96), At(300, 2), At(305, 9) };

        var start = ResetDetector.FindCurrentWindowStart(samples);
        var reset = ResetDetector.PredictNextReset(start, T0.AddMinutes(310));

        Assert.Equal(T0.AddMinutes(300).AddHours(5), reset);
    }

    /// <summary>The latest boundary wins, not the first — several can sit in one series.</summary>
    [Fact]
    public void TheMostRecentBoundaryWins()
    {
        var samples = new[] { At(0, 40), At(5, 1), At(300, 88), At(305, 3), At(310, 11) };

        var start = ResetDetector.FindCurrentWindowStart(samples);

        Assert.NotNull(start);
        Assert.Equal(T0.AddMinutes(305), start.LatestUtc);
    }
}
