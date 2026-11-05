namespace OView.Core.Providers.PlanHistory;

/// <summary>Which source put the window's start where it is.</summary>
public enum WindowPlacedBy
{
    /// <summary>Claude Code's reported reset minus one window length.</summary>
    ReportedReset,

    /// <summary>A window plan history located: a drop, or a rise from zero, between two samples.</summary>
    PlanHistory,

    /// <summary>
    /// Plan history had no window to locate, so the last drop, the start of the series or the
    /// five-hour bound stood in, or there were no samples at all.
    /// </summary>
    PlanHistoryFallback,
}

/// <summary>
/// The window's start, and the two answers it was chosen from.
/// </summary>
/// <param name="StartUtc">The start the off-plan comparison reads from.</param>
/// <param name="PlacedBy">Which source chose <paramref name="StartUtc"/>.</param>
/// <param name="ReportedStartUtc">
/// Claude Code's reset minus one window length, or null when it reported none a running window
/// could have.
/// </param>
/// <param name="Inferred">
/// The bracket plan history located, or null when it located none. A fallback is not a
/// location, so it never appears here.
/// </param>
/// <param name="Disagrees">
/// Both sources spoke and the reported start falls outside the inferred bracket, see
/// <see cref="SessionWindowPlacement.Disagrees"/>.
/// </param>
public sealed record WindowPlacement(
    DateTimeOffset StartUtc,
    WindowPlacedBy PlacedBy,
    DateTimeOffset? ReportedStartUtc,
    SessionWindowStart? Inferred,
    bool Disagrees);

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
        ReportedStart(utcNow, reportedResetUtc) ?? fromPlanHistory;

    /// <summary>
    /// <see cref="Place"/> with its reasoning kept: the chosen start, which source chose it, and
    /// whether the two sources contradict each other.
    /// </summary>
    /// <param name="inferred">The bracket plan history located, or null when it located none.</param>
    /// <param name="samples">The series <paramref name="inferred"/> was found in.</param>
    public static WindowPlacement Describe(
        DateTimeOffset utcNow,
        DateTimeOffset? reportedResetUtc,
        SessionWindowStart? inferred,
        DateTimeOffset fromPlanHistory,
        IReadOnlyList<PlanHistorySample> samples)
    {
        var reported = ReportedStart(utcNow, reportedResetUtc);

        // A window the reported one began after the end of is a rollover, not a contradiction:
        // see `Disagrees`. It is no inference about the window running now.
        if (reported is { } reportedStart && inferred is { } found && IsSupersededBy(found, reportedStart))
        {
            inferred = null;
        }

        var placedBy = reported is not null ? WindowPlacedBy.ReportedReset
            : inferred is not null ? WindowPlacedBy.PlanHistory
            : WindowPlacedBy.PlanHistoryFallback;

        return new WindowPlacement(
            reported ?? fromPlanHistory,
            placedBy,
            reported,
            inferred,
            reported is { } start && Disagrees(start, inferred, samples));
    }

    /// <summary>
    /// Whether plan history says the window began somewhere the reported start cannot be.
    ///
    /// <para><b>Only a located window can disagree.</b> The last drop, the start of the series
    /// and the five-hour bound are answers plan history gives when it has none, and a series
    /// that opens on <c>fh=0</c> after Desktop was off all night, the morning issue #274 was,
    /// always differs from the reported start. Reporting that would log a disagreement every
    /// morning on a machine with one account.</para>
    ///
    /// <para><b>The test is the samples, not a margin.</b> The reported start is exact, the
    /// bracket is two samples wide, so it can sit anywhere inside the bracket and nowhere after
    /// it, a drop already seen by then would have been inside the window. It can also sit
    /// <i>before</i> the bracket, but only one way: Claude's percentage is a whole number, so a
    /// first use too small to reach 1% leaves the meter at 0 and the rise from zero is seen a
    /// sample or several late. That is told apart from a different account by what the meter
    /// read in between, zeros all the way and the two agree, anything else and they do not.
    /// Nothing in this depends on how often Desktop samples (CLAUDE.md).</para>
    /// </summary>
    public static bool Disagrees(
        DateTimeOffset reportedStartUtc, SessionWindowStart? inferred, IReadOnlyList<PlanHistorySample> samples)
    {
        if (inferred is not { } bracket)
        {
            return false;
        }

        if (IsSupersededBy(bracket, reportedStartUtc))
        {
            return false;
        }

        if (reportedStartUtc > bracket.LatestUtc)
        {
            return true;
        }

        return reportedStartUtc < bracket.EarliestUtc
            && samples.Any(s => s.AtUtc >= reportedStartUtc && s.AtUtc <= bracket.EarliestUtc && s.FiveHourPercent != 0);
    }

    /// <summary>
    /// Whether the window <paramref name="bracket"/> locates had already ended when
    /// <paramref name="reportedStartUtc"/> began. It began between the bracket's two edges and
    /// lasts one window, so at the earliest edge plus a window it is over, whatever else is true.
    /// A reported start from there on is the next window, which Claude Code can report before
    /// Desktop's next sample catches up: nothing disagrees, there is simply nothing to compare.
    /// </summary>
    private static bool IsSupersededBy(SessionWindowStart bracket, DateTimeOffset reportedStartUtc) =>
        reportedStartUtc >= bracket.EarliestUtc + ResetDetector.WindowLength;

    private static DateTimeOffset? ReportedStart(DateTimeOffset utcNow, DateTimeOffset? reportedResetUtc) =>
        reportedResetUtc is { } reset && reset > utcNow && reset - utcNow <= ResetDetector.WindowLength
            ? reset - ResetDetector.WindowLength
            : null;

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
