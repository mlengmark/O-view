namespace OView.Core.Models;

/// <summary>
/// Assembles the meter series <see cref="DivergenceDetector"/> reads, from the two local
/// sources that report a five-hour percentage.
///
/// <para><b>Why two.</b> Claude Desktop writes <c>plan-usage-history.json</c> on a 15-minute
/// timer and Claude Code writes <c>~/.claude.json</c> whenever it talks to the API. The series
/// comes from the first, so the detector's view of the meter can be a quarter of an hour behind
/// a reading O-view has already read, already trusted, and is already displaying in the gauge
/// — which is how issue #268 produced a banner saying the meter had not moved beside a gauge
/// showing where it had moved to (5% at 16:07, 24% at 16:13, banner at 16:13).</para>
///
/// <para>Folding the second in is not a new source or a new trust decision. It is the same
/// reading the composite provider already ranks against plan history for the panel, brought to
/// the one comparison that was still ignoring it.</para>
/// </summary>
public static class MeterSeries
{
    /// <summary>
    /// The window's samples with Claude Code's cached reading appended, when that reading is
    /// newer than the newest sample and can be shown to belong to the same window.
    /// </summary>
    ///
    /// <remarks>
    /// <para>Three conditions, and each one is a way the append could otherwise corrupt the
    /// series it is trying to improve:</para>
    ///
    /// <list type="number">
    /// <item><b>Strictly newer than the newest sample.</b> This is what makes the reading worth
    /// appending at all, and it doubles as the proof that it belongs in the window: the last
    /// plan-history sample is inside the window by construction, so anything after it is too.
    /// An older reading would also land out of time order at the end of a series the detector
    /// reads positionally.</item>
    /// <item><b>Not below the newest sample.</b> Within a window the meter only rises, so a
    /// lower reading is evidence of a boundary between the two — the one thing the series must
    /// never span. The honest response is to leave the series alone rather than manufacture a
    /// negative rise, which <see cref="DivergenceDetector.FlatRiseTolerance"/> would read as
    /// flat and report as divergence.</item>
    /// <item><b>An existing sample to anchor to.</b> An empty series means no plan history for
    /// this window at all; one reported reading would still be one point, and turning
    /// "nothing to compare" into "one thing to compare" changes the stated reason for silence
    /// without changing the silence.</item>
    /// </list>
    ///
    /// <para>Everything the caller passes is already filtered by
    /// <c>CachedUtilizationProvider</c>: a percentage whose <c>resets_at</c> has passed, and an
    /// aged zero, both arrive here as null. This method adds no freshness rule of its own —
    /// the age it returns is handed to the detector, which owns that question.</para>
    /// </remarks>
    /// <param name="percents">Plan-history samples across the window, in time order.</param>
    /// <param name="meterAge">Age of the newest of them.</param>
    /// <param name="reportedPercent">Claude Code's cached five-hour reading, or null.</param>
    /// <param name="reportedAge">Age of that reading, or null when there is none.</param>
    /// <returns>The series to evaluate and the age of its newest member.</returns>
    public static (IReadOnlyList<int> Percents, TimeSpan MeterAge) WithReportedReading(
        IReadOnlyList<int> percents,
        TimeSpan meterAge,
        int? reportedPercent,
        TimeSpan? reportedAge)
    {
        if (percents.Count == 0 ||
            reportedPercent is not { } reported ||
            reportedAge is not { } age ||
            age >= meterAge ||
            reported < percents[^1])
        {
            return (percents, meterAge);
        }

        return ([.. percents, reported], age);
    }
}
