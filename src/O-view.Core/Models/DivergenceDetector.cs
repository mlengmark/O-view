using OView.Core.Providers.PlanHistory;

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

    /// <summary>
    /// The meter has stopped reporting, so a flat series is evidence of nothing.
    ///
    /// <para>Distinct from <see cref="InsufficientActivity"/>, which means the opposite
    /// problem: there the meter is live and the work is too small to move it.</para>
    /// </summary>
    MeterNotReporting,
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

    /// <summary>
    /// How old the newest meter sample may be and still support a claim about the work
    /// happening now.
    ///
    /// <para><b>This is the Live/Stale bound, deliberately.</b> The meter comes from Claude
    /// Desktop, which samples every ~5 minutes <i>while it is running</i> and not at all
    /// otherwise, so a series that has stopped is flat for the same reason whether the user is
    /// off-plan or simply working in Claude Code with Desktop closed. Reusing
    /// <see cref="PlanHistoryProvider.DefaultFreshness"/> makes one statement of what "the
    /// meter is current" means rather than two that can drift apart.</para>
    ///
    /// <para>This gate is why the detector exists in its cautious form. Without it, every CLI
    /// user with Desktop closed eventually sees "your usage is not drawing from your plan" —
    /// reported on 2026-08-24 against a meter that had not moved in 81 minutes because nothing
    /// was writing it, beside a banner naming extra-usage credits the account had disabled.
    /// A false "you're on credits" alarm destroys trust faster than a missed one, and this one
    /// was not merely false but unfalsifiable from the panel.</para>
    /// </summary>
    public static readonly TimeSpan MaxMeterAge = PlanHistoryProvider.DefaultFreshness;

    /// <param name="planPercentsInWindow">
    /// Meter samples across the window, in time order. Must not span a reset — the
    /// caller anchors the window on the last observed reset.
    /// </param>
    /// <param name="outputTokensInWindow">Deduplicated output tokens over the same span.</param>
    /// <param name="meterAge">
    /// Age of the newest meter sample. Required rather than optional, and with no default:
    /// every caller has to say whether the meter it is handing over is current, because the
    /// one that silently did not is the bug this parameter exists for.
    /// </param>
    public static DivergenceResult Evaluate(
        IReadOnlyList<int> planPercentsInWindow,
        long outputTokensInWindow,
        TimeSpan meterAge,
        long minOutputTokens = DefaultMinOutputTokens)
    {
        if (planPercentsInWindow.Count == 0)
        {
            return new DivergenceResult(DivergenceState.InsufficientActivity, outputTokensInWindow, 0);
        }

        var rise = planPercentsInWindow[^1] - planPercentsInWindow[0];

        // Before every other branch, including the exhausted-plan one. A meter that stopped
        // reporting cannot support "you are at your limit" either: the window it was pinned at
        // may have reset in the silence, and this detector's whole design is to prefer saying
        // nothing over saying something it cannot stand behind.
        if (meterAge > MaxMeterAge)
        {
            return new DivergenceResult(DivergenceState.MeterNotReporting, outputTokensInWindow, rise);
        }

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
