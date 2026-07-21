namespace OView.Core.Providers.PlanHistory;

/// <summary>
/// Derives the next five-hour window reset from the sample series. The source never
/// reports reset times; a sharp decrease in `fh` marks one. Two observed resets were
/// measured 5.00014 h apart, so the cadence is anchored on the most recent drop and
/// extrapolated in 5-hour steps (ADR-0007, docs/findings/plan-usage-history.md).
/// </summary>
public static class ResetDetector
{
    /// <summary>The window length. Measured exact to within sampling jitter.</summary>
    public static readonly TimeSpan WindowLength = TimeSpan.FromHours(5);

    /// <summary>
    /// Minimum decrease in `fh` that counts as a reset. Guards against noise; a real
    /// reset need not land on 0 because new usage may begin in the fresh window
    /// immediately (an observed reset went 16% → 1%).
    /// </summary>
    public const int DropThreshold = 2;

    /// <summary>
    /// Time of the most recent observed reset, or null if the series contains none.
    /// Samples must be ordered by time and belong to a single org.
    /// </summary>
    public static DateTimeOffset? FindLastDrop(IReadOnlyList<PlanHistorySample> samples)
    {
        DateTimeOffset? lastDrop = null;
        for (var i = 1; i < samples.Count; i++)
        {
            if (samples[i - 1].FiveHourPercent - samples[i].FiveHourPercent >= DropThreshold)
            {
                lastDrop = samples[i].AtUtc;
            }
        }

        return lastDrop;
    }

    /// <summary>
    /// Predict the next reset strictly after <paramref name="utcNow"/> by stepping the
    /// 5-hour cadence forward from the anchor. Null when no drop has ever been
    /// observed — the reset time is then genuinely unknown, and stays unknown
    /// (CLAUDE.md rule 6: never guess).
    /// </summary>
    public static DateTimeOffset? PredictNextReset(DateTimeOffset? lastDrop, DateTimeOffset utcNow)
    {
        if (lastDrop is not { } anchor) return null;

        var next = anchor + WindowLength;
        if (next <= utcNow)
        {
            // Idle periods can span several boundaries; roll forward without looping
            // sample-by-sample. Re-anchoring happens naturally on the next observed drop.
            var missed = (long)Math.Floor((utcNow - next) / WindowLength) + 1;
            next += WindowLength * missed;
        }

        return next;
    }
}
