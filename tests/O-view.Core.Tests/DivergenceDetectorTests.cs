using OView.Core.Models;

namespace OView.Core.Tests;

public class DivergenceDetectorTests
{
    /// <summary>
    /// A meter that is still being written. Every case below states this explicitly, because
    /// the one that silently assumed it is the bug the parameter was added for.
    /// </summary>
    private static readonly TimeSpan LiveMeter = TimeSpan.Zero;

    // ── the meter has to be reporting before a flat meter means anything ───────

    /// <summary>
    /// Reported 2026-08-24. Claude Desktop had not sampled for 81 minutes — it was closed,
    /// and the user was working entirely in Claude Code — so the series was flat for a reason
    /// that has nothing to do with billing. The panel told them their usage was not drawing
    /// from their plan and named extra-usage credits, which their account had switched off.
    /// </summary>
    [Fact]
    public void AStoppedMeterProvesNothing_HoweverMuchWorkRan()
    {
        var result = DivergenceDetector.Evaluate(
            [5, 5, 5, 5], outputTokensInWindow: 126_900,
            meterAge: TimeSpan.FromMinutes(81));

        Assert.Equal(DivergenceState.MeterNotReporting, result.State);
        Assert.False(result.IsOffPlan);

        // The rise is still reported: it is an observation, and only the CONCLUSION drawn
        // from it was unfounded.
        Assert.Equal(0, result.PlanRisePoints);
    }

    /// <summary>
    /// The gate is the Live/Stale bound, so a meter one tick inside it is still evidence.
    /// Pinned in both directions — a gate that silently swallowed the real case would look
    /// exactly like a fixed false alarm.
    /// </summary>
    [Fact]
    public void TheGateIsTheLiveStaleBound()
    {
        var live = DivergenceDetector.Evaluate(
            [6, 6, 6], outputTokensInWindow: 69_091, meterAge: DivergenceDetector.MaxMeterAge);
        Assert.Equal(DivergenceState.Diverging, live.State);

        var stopped = DivergenceDetector.Evaluate(
            [6, 6, 6], outputTokensInWindow: 69_091,
            meterAge: DivergenceDetector.MaxMeterAge + TimeSpan.FromSeconds(1));
        Assert.Equal(DivergenceState.MeterNotReporting, stopped.State);
    }

    /// <summary>
    /// An exhausted plan is not exempt. A meter pinned at 100 and then left unwritten for
    /// hours may describe a window that has since reset, so "you are at your limit" is as
    /// unfounded as the divergence claim.
    /// </summary>
    [Fact]
    public void AStoppedMeterCannotClaimTheLimitEither()
    {
        var result = DivergenceDetector.Evaluate(
            [99, 100], outputTokensInWindow: 500, meterAge: TimeSpan.FromHours(6));

        Assert.Equal(DivergenceState.MeterNotReporting, result.State);
        Assert.False(result.IsOffPlan);
    }

    // ── the real-world case this exists for ────────────────────────────────────
    [Fact]
    public void FrozenMeterUnderHeavyLoad_IsDiverging()
    {
        // 2026-07-21: meter pinned at 6% while ~69K output tokens ran on Fable.
        var result = DivergenceDetector.Evaluate([6, 6, 6, 6, 6, 6], outputTokensInWindow: 69_091, meterAge: LiveMeter);

        Assert.Equal(DivergenceState.Diverging, result.State);
        Assert.True(result.IsOffPlan);
        Assert.Equal(0, result.PlanRisePoints);
    }

    [Fact]
    public void MeterTrackingActivity_IsConsistent()
    {
        // 2026-07-20: ~68K output tokens on Opus moved the meter 1% -> 16%.
        var result = DivergenceDetector.Evaluate([1, 4, 8, 12, 16], outputTokensInWindow: 67_966, meterAge: LiveMeter);

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
        var result = DivergenceDetector.Evaluate([12, 12, 12], outputTokensInWindow: 4_000, meterAge: LiveMeter);

        Assert.Equal(DivergenceState.InsufficientActivity, result.State);
        Assert.False(result.IsOffPlan);
    }

    [Fact]
    public void JustBelowThreshold_StaysSilent()
    {
        var result = DivergenceDetector.Evaluate([12, 12], outputTokensInWindow: 49_999, meterAge: LiveMeter);

        Assert.Equal(DivergenceState.InsufficientActivity, result.State);
    }

    [Fact]
    public void SinglePointRise_IsToleratedAsRounding()
    {
        // One point over a heavy window is still effectively flat — but the volume
        // means we should not claim consistency either; the tolerance treats it as
        // flat, which is the safe direction for a heavy window.
        var result = DivergenceDetector.Evaluate([6, 6, 7], outputTokensInWindow: 60_000, meterAge: LiveMeter);

        Assert.Equal(DivergenceState.Diverging, result.State);
    }

    [Fact]
    public void TwoPointRise_IsRealMovement()
    {
        var result = DivergenceDetector.Evaluate([6, 7, 8], outputTokensInWindow: 60_000, meterAge: LiveMeter);

        Assert.Equal(DivergenceState.Consistent, result.State);
    }

    // ── plan exhausted ────────────────────────────────────────────────────────
    [Fact]
    public void PinnedAtLimit_ReportsLimitReached_RegardlessOfVolume()
    {
        var result = DivergenceDetector.Evaluate([99, 100, 100], outputTokensInWindow: 500, meterAge: LiveMeter);

        Assert.Equal(DivergenceState.PlanLimitReached, result.State);
        Assert.True(result.IsOffPlan);
    }

    [Fact]
    public void ClimbingToLimit_ReportsLimitReached_NotConsistent()
    {
        // Rose legitimately but has now hit the ceiling — further work bills elsewhere.
        var result = DivergenceDetector.Evaluate([80, 90, 99], outputTokensInWindow: 60_000, meterAge: LiveMeter);

        Assert.Equal(DivergenceState.PlanLimitReached, result.State);
    }

    // ── degenerate input ──────────────────────────────────────────────────────
    [Fact]
    public void NoSamples_YieldsInsufficient_NeverThrows()
    {
        var result = DivergenceDetector.Evaluate([], outputTokensInWindow: 1_000_000, meterAge: LiveMeter);

        Assert.Equal(DivergenceState.InsufficientActivity, result.State);
        Assert.False(result.IsOffPlan);
    }

    [Fact]
    public void SingleSample_HasZeroRise_AndCanDiverge()
    {
        var result = DivergenceDetector.Evaluate([6], outputTokensInWindow: 60_000, meterAge: LiveMeter);

        Assert.Equal(DivergenceState.Diverging, result.State);
        Assert.Equal(0, result.PlanRisePoints);
    }

    [Fact]
    public void IdleWindow_WithFlatMeter_IsNotFlagged()
    {
        var result = DivergenceDetector.Evaluate([20, 20, 20, 20], outputTokensInWindow: 0, meterAge: LiveMeter);

        Assert.Equal(DivergenceState.InsufficientActivity, result.State);
    }
}
