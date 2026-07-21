namespace OView.Core.Models;

/// <summary>What the plan meter and local activity say about each other.</summary>
public enum DivergenceState
{
    /// <summary>Not enough activity in the window to expect visible meter movement.</summary>
    InsufficientActivity,

    /// <summary>Activity and meter movement are consistent — usage is drawing from the plan.</summary>
    Consistent,

    /// <summary>Substantial activity with a flat meter — usage is not drawing from the plan window.</summary>
    Diverging,

    /// <summary>The plan window is exhausted, so further usage necessarily bills elsewhere.</summary>
    PlanLimitReached,
}

/// <param name="OutputTokensInWindow">Deduplicated output tokens observed since the window start.</param>
/// <param name="PlanRisePoints">Percentage points the plan meter rose across the same window.</param>
public sealed record DivergenceResult(
    DivergenceState State,
    long OutputTokensInWindow,
    int PlanRisePoints)
{
    /// <summary>True when local work is happening that the plan meter is not accounting for.</summary>
    public bool IsOffPlan => State is DivergenceState.Diverging or DivergenceState.PlanLimitReached;
}

/// <summary>
/// Detects usage that bypasses the plan window — the failure mode found on 2026-07-21,
/// where the tray read a comfortable 6% while ~€86 of credit usage was billed
/// (docs/findings/credit-usage-divergence.md).
///
/// The meter reports whole percentages, so small genuine movement is invisible and a
/// naive detector would mistake rounding for divergence. Thresholds below are
/// calibrated from 20 observed rise events on this account: median 2,523 output
/// tokens per percentage point, worst case 5,793. The default floor is set roughly
/// 10x the worst case so that a flat meter is unambiguous rather than marginal —
/// deliberately biased toward silence, since a false "you're on credits" alarm would
/// destroy trust faster than a missed one.
/// </summary>
public static class DivergenceDetector
{
    /// <summary>
    /// Output tokens in the window below which a flat meter proves nothing.
    /// At the observed worst case this implies ~8 points of expected movement, and at
    /// the median ~20 — either way, zero movement at this volume is anomalous.
    /// </summary>
    public const long DefaultMinOutputTokens = 50_000;

    /// <summary>Rise tolerated while still calling the meter flat, absorbing rounding at window edges.</summary>
    public const int FlatRiseTolerance = 1;

    /// <summary>Meter value at or above which the plan window counts as exhausted.</summary>
    public const int LimitReachedPercent = 99;

    /// <param name="planPercentsInWindow">
    /// Meter samples across the window, in time order. Must not span a reset — the
    /// caller anchors the window on the last observed reset.
    /// </param>
    /// <param name="outputTokensInWindow">Deduplicated output tokens over the same span.</param>
    public static DivergenceResult Evaluate(
        IReadOnlyList<int> planPercentsInWindow,
        long outputTokensInWindow,
        long minOutputTokens = DefaultMinOutputTokens)
    {
        if (planPercentsInWindow.Count == 0)
        {
            return new DivergenceResult(DivergenceState.InsufficientActivity, outputTokensInWindow, 0);
        }

        var rise = planPercentsInWindow[^1] - planPercentsInWindow[0];

        // A pinned meter means the plan allowance is spent; anything still running is
        // billing somewhere else by definition, so report it regardless of volume.
        if (planPercentsInWindow[^1] >= LimitReachedPercent)
        {
            return new DivergenceResult(DivergenceState.PlanLimitReached, outputTokensInWindow, rise);
        }

        if (outputTokensInWindow < minOutputTokens)
        {
            return new DivergenceResult(DivergenceState.InsufficientActivity, outputTokensInWindow, rise);
        }

        return new DivergenceResult(
            rise <= FlatRiseTolerance ? DivergenceState.Diverging : DivergenceState.Consistent,
            outputTokensInWindow,
            rise);
    }
}
