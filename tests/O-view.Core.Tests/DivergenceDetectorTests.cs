using OView.Core.Models;

namespace OView.Core.Tests;

public class DivergenceDetectorTests
{
    // ── the real-world case this exists for ────────────────────────────────────
    [Fact]
    public void FrozenMeterUnderHeavyLoad_IsDiverging()
    {
        // 2026-07-21: meter pinned at 6% while ~69K output tokens ran on Fable.
        var result = DivergenceDetector.Evaluate([6, 6, 6, 6, 6, 6], outputTokensInWindow: 69_091);

        Assert.Equal(DivergenceState.Diverging, result.State);
        Assert.True(result.IsOffPlan);
        Assert.Equal(0, result.PlanRisePoints);
    }

    [Fact]
    public void MeterTrackingActivity_IsConsistent()
    {
        // 2026-07-20: ~68K output tokens on Opus moved the meter 1% -> 16%.
        var result = DivergenceDetector.Evaluate([1, 4, 8, 12, 16], outputTokensInWindow: 67_966);

        Assert.Equal(DivergenceState.Consistent, result.State);
        Assert.False(result.IsOffPlan);
        Assert.Equal(15, result.PlanRisePoints);
    }

    // ── the rounding floor: the thing that would cause false alarms ────────────
    [Fact]
    public void LightActivityWithFlatMeter_IsNotFlagged()
    {
        // ~4K tokens is well under one percentage point at the observed median —
        // a flat meter here is rounding, not divergence.
        var result = DivergenceDetector.Evaluate([12, 12, 12], outputTokensInWindow: 4_000);

        Assert.Equal(DivergenceState.InsufficientActivity, result.State);
        Assert.False(result.IsOffPlan);
    }

    [Fact]
    public void JustBelowThreshold_StaysSilent()
    {
        var result = DivergenceDetector.Evaluate([12, 12], outputTokensInWindow: 49_999);

        Assert.Equal(DivergenceState.InsufficientActivity, result.State);
    }

    [Fact]
    public void SinglePointRise_IsToleratedAsRounding()
    {
        // One point over a heavy window is still effectively flat — but the volume
        // means we should not claim consistency either; the tolerance treats it as
        // flat, which is the safe direction for a heavy window.
        var result = DivergenceDetector.Evaluate([6, 6, 7], outputTokensInWindow: 60_000);

        Assert.Equal(DivergenceState.Diverging, result.State);
    }

    [Fact]
    public void TwoPointRise_IsRealMovement()
    {
        var result = DivergenceDetector.Evaluate([6, 7, 8], outputTokensInWindow: 60_000);

        Assert.Equal(DivergenceState.Consistent, result.State);
    }

    // ── plan exhausted ────────────────────────────────────────────────────────
    [Fact]
    public void PinnedAtLimit_ReportsLimitReached_RegardlessOfVolume()
    {
        var result = DivergenceDetector.Evaluate([99, 100, 100], outputTokensInWindow: 500);

        Assert.Equal(DivergenceState.PlanLimitReached, result.State);
        Assert.True(result.IsOffPlan);
    }

    [Fact]
    public void ClimbingToLimit_ReportsLimitReached_NotConsistent()
    {
        // Rose legitimately but has now hit the ceiling — further work bills elsewhere.
        var result = DivergenceDetector.Evaluate([80, 90, 99], outputTokensInWindow: 60_000);

        Assert.Equal(DivergenceState.PlanLimitReached, result.State);
    }

    // ── degenerate input ──────────────────────────────────────────────────────
    [Fact]
    public void NoSamples_YieldsInsufficient_NeverThrows()
    {
        var result = DivergenceDetector.Evaluate([], outputTokensInWindow: 1_000_000);

        Assert.Equal(DivergenceState.InsufficientActivity, result.State);
        Assert.False(result.IsOffPlan);
    }

    [Fact]
    public void SingleSample_HasZeroRise_AndCanDiverge()
    {
        var result = DivergenceDetector.Evaluate([6], outputTokensInWindow: 60_000);

        Assert.Equal(DivergenceState.Diverging, result.State);
        Assert.Equal(0, result.PlanRisePoints);
    }

    [Fact]
    public void IdleWindow_WithFlatMeter_IsNotFlagged()
    {
        var result = DivergenceDetector.Evaluate([20, 20, 20, 20], outputTokensInWindow: 0);

        Assert.Equal(DivergenceState.InsufficientActivity, result.State);
    }
}
