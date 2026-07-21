namespace OView.Core.Providers.PlanHistory;

/// <summary>
/// Derives the weekly (7-day) reset from `sd` drops — the same idea as
/// <see cref="ResetDetector"/> for the 5-hour window, but the data forces two
/// differences (GitHub issue #6):
///
///   • The weekly window's PERIOD is undocumented (disputed 7-day vs 72-hour), so it
///     is not assumed. It is MEASURED from two observed resets — exactly as the
///     5-hour window's 5.00014 h was measured. With fewer than two, the next reset is
///     genuinely unknown and reported as null (never guessed — CLAUDE.md rule 6).
///   • Weekly resets are rare, and plan-history retention is only ~1.5 days, so two
///     of them are never in one file — the observed resets must be persisted over
///     time (see the reset log). And a drop seen across a Desktop-restart gap is far
///     more likely a re-fetch snap than a real reset, so only IN-CADENCE drops are
///     trusted.
/// </summary>
public static class WeeklyResetDetector
{
    public const int DropThreshold = 2;

    /// <summary>Largest sample gap still treated as continuous sampling (~3× the 5-min cadence).</summary>
    public static readonly TimeSpan MaxInCadenceGap = TimeSpan.FromMinutes(15);

    /// <summary>Timestamps of clean, in-cadence `sd` drops within one sample series.</summary>
    public static IReadOnlyList<DateTimeOffset> FindResets(IReadOnlyList<PlanHistorySample> samples)
    {
        var resets = new List<DateTimeOffset>();
        for (var i = 1; i < samples.Count; i++)
        {
            var gap = samples[i].AtUtc - samples[i - 1].AtUtc;
            if (gap <= MaxInCadenceGap &&
                samples[i - 1].SevenDayPercent - samples[i].SevenDayPercent >= DropThreshold)
            {
                resets.Add(samples[i].AtUtc);
            }
        }
        return resets;
    }

    /// <summary>
    /// Predict the next weekly reset after <paramref name="utcNow"/> from the full set
    /// of observed resets (persisted across time), or null when the period cannot yet
    /// be measured (fewer than two observations). Period = the interval between the
    /// last two observed resets — measured, not assumed.
    /// </summary>
    public static DateTimeOffset? PredictNextReset(IReadOnlyList<DateTimeOffset> observedResets, DateTimeOffset utcNow)
    {
        if (observedResets.Count < 2)
        {
            return null;
        }

        var ordered = observedResets.OrderBy(t => t).ToList();
        var period = ordered[^1] - ordered[^2];
        if (period <= TimeSpan.Zero)
        {
            return null;
        }

        var next = ordered[^1] + period;
        if (next <= utcNow)
        {
            var missed = (long)Math.Floor((utcNow - next) / period) + 1;
            next += period * missed;
        }
        return next;
    }
}

/// <summary>
/// Persists observed weekly resets across time, since retention keeps only ~1.5 days
/// of plan-history and two weekly resets are days apart. Implemented by the rollup
/// store. Recording is idempotent — the reset timestamp is the key.
/// </summary>
public interface IWeeklyResetLog
{
    void RecordResets(IEnumerable<DateTimeOffset> resets);
    IReadOnlyList<DateTimeOffset> GetResets();
}
