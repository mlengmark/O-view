namespace OView.Core.Providers.PlanHistory;

/// <summary>
/// When the current five-hour window began, bracketed by the two samples that straddle it.
///
/// <para>Same shape as <see cref="WeeklyResetObservation"/>, and for the same reason: Claude
/// Desktop samples every ~5 minutes and stops entirely when it is closed, so a boundary is
/// almost never seen at the instant it happens. What is known is that it fell <i>after</i>
/// one sample and <i>at or before</i> the next.</para>
/// </summary>
/// <param name="EarliestUtc">Last sample that belonged to the previous window, exclusive.</param>
/// <param name="LatestUtc">First sample that belonged to this one, inclusive.</param>
public sealed record SessionWindowStart(DateTimeOffset EarliestUtc, DateTimeOffset LatestUtc)
{
    public TimeSpan Uncertainty => LatestUtc - EarliestUtc;

    /// <summary>
    /// When this window ends. Predicted from the <b>upper</b> bound, matching ADR-0011's
    /// choice for the weekly window so the two do not disagree about which end of a bracket
    /// they forecast from.
    /// </summary>
    public DateTimeOffset ResetAtUtc => LatestUtc + ResetDetector.WindowLength;
}

/// <summary>
/// Derives the current five-hour window from the sample series. The source never reports
/// window boundaries; they have to be inferred from how <c>fh</c> moves.
///
/// <para><b>The window rolls from first use — it is not a grid</b> (GitHub issue #180). This
/// class used to anchor on the last observed drop and extrapolate in 5-hour steps
/// indefinitely, which agrees with reality only while usage is continuous. After an idle gap
/// the real window restarts whenever the user next works, at whatever time that is, while a
/// grid marches on from a stale anchor. Measured 2026-08-23: a two-day gap left the panel
/// predicting 22:47 where Desktop said 21:01, because the anchor was still Friday's drop
/// stepped forward ten boundaries. The rolling behaviour was already written down in
/// CLAUDE.md's build order and in <c>docs/findings/jsonl-schema.md</c>; the implementation
/// simply contradicted it.</para>
///
/// <para><b>A window start is not always a drop.</b> The old code assumed it was, and its
/// comment claimed "re-anchoring happens naturally on the next observed drop". It does not:
/// across a sampling gap the meter is already back at <c>0</c> when Desktop resumes, so the
/// reset leaves no decrease behind. The signal there is a run of zeros followed by a rise —
/// a different shape, the same event.</para>
/// </summary>
public static class ResetDetector
{
    /// <summary>The window length. Measured exact to within sampling jitter.</summary>
    public static readonly TimeSpan WindowLength = TimeSpan.FromHours(5);

    /// <summary>
    /// Minimum decrease in `fh` that counts as a boundary. Guards against noise; a real
    /// reset need not land on 0 because new usage may begin in the fresh window
    /// immediately (an observed reset went 16% → 1%).
    /// </summary>
    public const int DropThreshold = 2;

    /// <summary>
    /// Time of the most recent observed reset, or null if the series contains none.
    ///
    /// <para>Kept for callers that want the last <i>boundary</i> rather than the current
    /// window — the divergence detector's window floor is the one that does. Samples must be
    /// ordered by time and belong to a single org.</para>
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
    /// When the window that is running <i>now</i> began, or null when none is.
    ///
    /// <para>Null in three distinct situations, all of which are honestly "unknown" rather
    /// than a number worth guessing: the latest reading is <c>0</c>, so no window is running
    /// at all; the series never shows a boundary, so this window began before the data; or
    /// there is nothing to read.</para>
    /// </summary>
    public static SessionWindowStart? FindCurrentWindowStart(IReadOnlyList<PlanHistorySample> samples)
    {
        if (samples.Count == 0)
        {
            return null;
        }

        // fh == 0 means nothing has been used in the current window — which is to say no
        // window is running, because it starts on first use. Predicting a reset from a
        // boundary seen earlier would be describing a window that has not begun.
        if (samples[^1].FiveHourPercent == 0)
        {
            return null;
        }

        for (var i = samples.Count - 1; i >= 1; i--)
        {
            if (StartsNewWindow(samples[i - 1], samples[i]))
            {
                return new SessionWindowStart(samples[i - 1].AtUtc, samples[i].AtUtc);
            }
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="current"/> cannot belong to the same window as
    /// <paramref name="previous"/>.
    ///
    /// <para>Two shapes, one event. A <b>drop</b> is the window resetting while Desktop was
    /// watching — precise, and the only case the old code handled. A <b>rise from zero</b> is
    /// first use starting a fresh window, which is what a reset looks like when it happened
    /// while Desktop was closed: there is no decrease to see, because the meter had already
    /// gone back to 0 by the time sampling resumed.</para>
    /// </summary>
    private static bool StartsNewWindow(PlanHistorySample previous, PlanHistorySample current) =>
        previous.FiveHourPercent - current.FiveHourPercent >= DropThreshold
        || (previous.FiveHourPercent == 0 && current.FiveHourPercent > 0);

    /// <summary>
    /// The end of the window <paramref name="start"/> describes, or null when there is no
    /// window or its end has already passed.
    ///
    /// <para><b>Nothing is extrapolated.</b> A reset in the past means the window ended and a
    /// new one has not been observed starting — so the answer is unknown, and the panel says
    /// so. The previous implementation stepped a stale anchor forward until it landed in the
    /// future, which always produced a confident time and was wrong by up to five hours
    /// (CLAUDE.md rule 6: a monitoring tool that confidently displays a wrong number is worse
    /// than one that admits uncertainty).</para>
    /// </summary>
    public static DateTimeOffset? PredictNextReset(SessionWindowStart? start, DateTimeOffset utcNow) =>
        start is { } window && window.ResetAtUtc > utcNow ? window.ResetAtUtc : null;
}
