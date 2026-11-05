namespace OView.Core.Providers.PlanHistory;

/// <summary>
/// Where the five-hour window the off-plan comparison reads began, and which source says so.
///
/// <para><b>Two sources know, and one of them can be silent.</b> Claude Code reports the
/// instant the running window ends (<c>resets_at</c>), and the window is five hours long, so
/// that instant minus five hours <i>is</i> the start, exactly, with no sampling gap in it.
/// Plan history only brackets the start between two samples, and has nothing at all while
/// Claude Desktop is closed. Issue #274 was that silence: Desktop was closed from 09-05 17:31Z
/// until 09-07 14:44Z, resumed with <c>fh=0</c>, and the window was anchored on a drop two days
/// old while Claude Code was reporting, correctly, a reset at 19:40Z, a window that began at
/// 14:40Z.</para>
///
/// <para>This type is the one place that chooses between them, so the next source added or
/// demoted is a change to <see cref="Place"/> and nowhere else.</para>
/// </summary>
public static class SessionWindowPlacement
{
    /// <summary>
    /// The window's start: the reported reset minus one window length when that reset is one a
    /// running window could have, otherwise <paramref name="fromPlanHistory"/>.
    ///
    /// <para><b>A reset is usable only while it is ahead of now and within one window of it.</b>
    /// One already past describes a window that has ended, and stepping it forward would rebuild
    /// the grid issue #180 removed. One more than five hours ahead belongs to no window that
    /// could be running, since the window rolls from first use, and would put the start in the
    /// future. Neither is repaired, both are ignored, and the plan-history answer stands.</para>
    /// </summary>
    /// <param name="utcNow">The instant the window is placed for.</param>
    /// <param name="reportedResetUtc">Claude Code's reported reset, or null when there is none.</param>
    /// <param name="fromPlanHistory">
    /// The start plan history supports, already held to being inside a window.
    /// </param>
    public static DateTimeOffset Place(
        DateTimeOffset utcNow, DateTimeOffset? reportedResetUtc, DateTimeOffset fromPlanHistory) =>
        reportedResetUtc is { } reset && reset > utcNow && reset - utcNow <= ResetDetector.WindowLength
            ? reset - ResetDetector.WindowLength
            : fromPlanHistory;

    /// <summary>
    /// The samples since the last drop among them, which is what keeps a series inside one
    /// window whatever start was chosen.
    ///
    /// <para>The detector reads a negative rise as a flat meter
    /// (<see cref="Models.DivergenceDetector.FlatRiseTolerance"/>), so a single sample from the
    /// window before a reset, falling just inside a start taken from Claude Code, would read as
    /// a meter that had not moved. A drop of <see cref="ResetDetector.DropThreshold"/> or more
    /// is a boundary; the sample it lands on is the first of the new window and is kept.</para>
    /// </summary>
    public static IReadOnlyList<PlanHistorySample> SinceLastDrop(IReadOnlyList<PlanHistorySample> samples)
    {
        for (var i = samples.Count - 1; i >= 1; i--)
        {
            if (samples[i - 1].FiveHourPercent - samples[i].FiveHourPercent >= ResetDetector.DropThreshold)
            {
                return [.. samples.Skip(i)];
            }
        }

        return samples;
    }
}
