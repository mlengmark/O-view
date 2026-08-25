namespace OView.Core.Providers.PlanHistory;

/// <summary>
/// A predicted weekly reset. <see cref="Uncertainty"/> survives because the session reset
/// shares this shape and genuinely has one; for the weekly window it is always
/// <see cref="TimeSpan.Zero"/> now that both sources are exact (ADR-0014).
/// </summary>
/// <param name="Period">
/// The cadence this steps by. Carried on the forecast so anything drawing past boundaries —
/// the usage graph's week gridlines — steps by the same cadence the countdown does instead of
/// re-deriving it and drifting.
/// </param>
public sealed record WeeklyResetForecast(DateTimeOffset AtUtc, TimeSpan Uncertainty, TimeSpan Period)
{
    public bool IsPrecise => Uncertainty <= WeeklyWindow.PreciseBracket;
}

/// <summary>
/// The weekly window's arithmetic — and, since ADR-0014, nothing else.
///
/// <para>This replaces <c>WeeklyResetDetector</c>, which inferred the reset from drops in
/// Claude Desktop's sampled <c>sd</c> series. That inference is gone: the reset is reported
/// exactly by Claude Code and is a <b>static weekly grid</b>, so there is nothing left to
/// detect. Measured across five weeks on one account, the exact instant projected backwards
/// in whole weeks landed inside all five brackets the old detector had independently
/// observed — while the detector's own prediction was 11.5 hours late and on the wrong
/// day.</para>
///
/// <para>What survives is the part that was never in doubt: the window is seven days, and the
/// graph needs to know where past boundaries fell.</para>
/// </summary>
public static class WeeklyWindow
{
    /// <summary>
    /// Seven days. Measured, not assumed — <c>sd</c> climbed 2 → 70 monotonically across
    /// seven days of continuous sampling, which disproves the rival 72-hour hypothesis
    /// outright (ADR-0011's one finding that outlived it).
    /// </summary>
    public static readonly TimeSpan Length = TimeSpan.FromDays(7);

    /// <summary>
    /// Uncertainty at or below which a reset time renders without the <c>~</c> that marks an
    /// approximation.
    ///
    /// <para>Kept for the <b>session</b> reset, which is still bracketed by Desktop's sampling
    /// cadence and still earns the marker. Every weekly reset now carries zero uncertainty, so
    /// on that row the comparison is always false — see ADR-0014's consequences for why the
    /// now-unreachable rendering path is removed separately rather than here.</para>
    /// </summary>
    public static readonly TimeSpan PreciseBracket = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The first grid point strictly after <paramref name="utcNow"/>, projected from
    /// <paramref name="anchorUtc"/> in whole weeks.
    ///
    /// <para><b>An anchor in the past is projected forward, and that is the point.</b> The old
    /// rule refused to step a passed <c>resets_at</c> forward, on the grounds that doing so
    /// dressed an inference in a reported value's zero uncertainty. For the five-hour window
    /// that is right — it rolls from first use and is not a grid (issue #180). For this one it
    /// is wrong: the schedule is fixed to the account, so the same weekday and time of day
    /// recur exactly, and projecting is arithmetic rather than inference.</para>
    ///
    /// <para>Works for an anchor on either side of now, so a freshly reported instant that has
    /// not yet arrived is returned as-is rather than pushed a week out.</para>
    /// </summary>
    public static DateTimeOffset NextAfter(DateTimeOffset anchorUtc, DateTimeOffset utcNow)
    {
        var periods = Math.Floor((utcNow - anchorUtc) / Length) + 1;
        var next = anchorUtc + Length * periods;

        // Guards the exact-boundary case, where the division can land precisely on an integer
        // and leave `next` equal to now rather than after it.
        while (next <= utcNow)
        {
            next += Length;
        }

        return next;
    }

    /// <summary>
    /// Every reset boundary falling in <c>[fromUtc, toUtc]</c>, obtained by stepping the
    /// cadence backwards from <paramref name="nextResetUtc"/>.
    ///
    /// <para>Past boundaries are derived rather than looked up: the graph covers 31 days and
    /// most of those boundaries fell before O-view was installed. Since the cadence is exact,
    /// stepping back reconstructs the boundaries the user actually experienced.</para>
    /// </summary>
    public static IReadOnlyList<DateTimeOffset> BoundariesWithin(
        DateTimeOffset nextResetUtc, TimeSpan period, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        if (period <= TimeSpan.Zero || toUtc < fromUtc)
        {
            return [];
        }

        // Jump straight to the first boundary at or before the end of the range rather than
        // walking there — the prediction can be a long way past a short window.
        var overshoot = Math.Max(0, Math.Ceiling((nextResetUtc - toUtc) / period));
        var cursor = nextResetUtc - period * overshoot;

        var boundaries = new List<DateTimeOffset>();
        while (cursor >= fromUtc)
        {
            if (cursor <= toUtc)
            {
                boundaries.Add(cursor);
            }
            cursor -= period;
        }

        boundaries.Reverse();
        return boundaries;
    }
}
