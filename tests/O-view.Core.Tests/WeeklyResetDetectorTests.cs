using OView.Core.Providers.PlanHistory;

namespace OView.Core.Tests;

public class WeeklyResetDetectorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 14, 0, 0, 0, TimeSpan.Zero);

    private static PlanHistorySample At(TimeSpan offset, int sd) =>
        new(T0 + offset, "org", 0, sd);

    // ── clean-drop detection with the restart-snap guard ───────────────────────
    [Fact]
    public void InCadenceDrop_IsAReset()
    {
        var samples = new[]
        {
            At(TimeSpan.FromMinutes(0), 9),
            At(TimeSpan.FromMinutes(5), 0),   // 5-min gap — in cadence
        };

        Assert.Single(WeeklyResetDetector.FindResets(samples));
    }

    [Fact]
    public void DropAcrossADesktopGap_IsNotTrusted()
    {
        // The real 2026-07-21 case: sd 9%->0% but across a 10-hour Desktop-closed gap.
        var samples = new[]
        {
            At(TimeSpan.FromHours(0), 9),
            At(TimeSpan.FromHours(10), 0),    // huge gap — a re-fetch snap, not a reset
        };

        Assert.Empty(WeeklyResetDetector.FindResets(samples));
    }

    [Fact]
    public void SinglePointWobble_IsNotAReset()
    {
        var samples = new[] { At(TimeSpan.FromMinutes(0), 9), At(TimeSpan.FromMinutes(5), 8) };

        Assert.Empty(WeeklyResetDetector.FindResets(samples));
    }

    // ── period is measured from two resets, never assumed ──────────────────────
    [Fact]
    public void FewerThanTwoResets_YieldsNoPrediction()
    {
        Assert.Null(WeeklyResetDetector.PredictNextReset([], T0));
        Assert.Null(WeeklyResetDetector.PredictNextReset([T0], T0));
    }

    [Fact]
    public void TwoResets_MeasureThePeriod_AndPredictForward()
    {
        var first = T0;
        var second = T0.AddDays(7);   // a 7-day period, measured — not hard-coded

        var next = WeeklyResetDetector.PredictNextReset([first, second], second.AddHours(1));

        Assert.Equal(second.AddDays(7), next);
    }

    [Fact]
    public void SeventyTwoHourPeriod_IsHonoured_NotForcedTo7Days()
    {
        // If the window were 72h, the measured period must be respected, not overridden.
        var first = T0;
        var second = T0.AddHours(72);

        var next = WeeklyResetDetector.PredictNextReset([first, second], second.AddHours(1));

        Assert.Equal(second.AddHours(72), next);
    }

    [Fact]
    public void PredictionRollsForwardAcrossMissedPeriods()
    {
        var first = T0;
        var second = T0.AddDays(7);
        // Now is well past several periods.
        var next = WeeklyResetDetector.PredictNextReset([first, second], second.AddDays(20));

        Assert.True(next > second.AddDays(20));
        Assert.Equal(0, (next!.Value - second).TotalDays % 7);  // still on the 7-day grid
    }

    [Fact]
    public void UnorderedInput_IsSorted()
    {
        var next = WeeklyResetDetector.PredictNextReset([T0.AddDays(7), T0], T0.AddDays(8));

        Assert.Equal(T0.AddDays(14), next);
    }
}
