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

    [Fact]
    public void PredictNextReset_NoAnchor_ReturnsNull_NeverGuesses()
    {
        Assert.Null(ResetDetector.PredictNextReset(null, T0));
    }

    [Fact]
    public void PredictNextReset_IsAnchorPlusFiveHours()
    {
        var next = ResetDetector.PredictNextReset(T0, T0.AddHours(1));

        Assert.Equal(T0.AddHours(5), next);
    }

    [Fact]
    public void PredictNextReset_RollsForwardAcrossMissedWindows()
    {
        // Idle across two boundaries: anchor+5h and anchor+10h have both passed.
        var next = ResetDetector.PredictNextReset(T0, T0.AddHours(11));

        Assert.Equal(T0.AddHours(15), next);
    }

    [Fact]
    public void PredictNextReset_ExactlyOnBoundary_RollsToNextWindow()
    {
        var next = ResetDetector.PredictNextReset(T0, T0.AddHours(5));

        Assert.Equal(T0.AddHours(10), next);
    }
}
