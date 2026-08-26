namespace OView.Core.Models;

/// <summary>
/// Where a local calendar day starts and stops, taken from the timezone rather than assumed.
///
/// <para><b>Why this is not arithmetic.</b> A local day is 23 or 25 hours twice a year, so
/// stepping back in 24-hour blocks from midnight drifts by an hour for the rest of the window
/// — and it drifts <i>silently</i>, moving usage between two adjacent bars rather than
/// producing anything that looks wrong. Every consumer of a local day boundary in this app
/// goes through here so that only one piece of code has to get it right: the rollup buckets,
/// the graph's columns, and the gridlines drawn over those columns
/// (<a href="https://github.com/mlengmark/O-view/issues/211">issue #211</a>).</para>
///
/// <para><b>The plan meters are not days and must not use this.</b> The five-hour window rolls
/// from first use and the weekly reset is a fixed instant Claude reports (ADR-0014). Neither
/// is a calendar day; both keep their own clocks.</para>
/// </summary>
public static class LocalDays
{
    /// <summary>The local calendar date an instant falls on.</summary>
    public static DateOnly DateOf(DateTimeOffset instant, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    /// <summary>
    /// The instant a local day begins.
    ///
    /// <para>Local midnight is not guaranteed to exist or to be unique. Where the clock jumps
    /// forward <i>over</i> midnight the day begins at the jump, so the first local time that
    /// exists is the answer. Where it falls back across midnight the hour is lived twice and
    /// the day begins at the <b>first</b> of them — the larger offset — because taking the
    /// second would leave that hour outside every day.</para>
    ///
    /// <para>Both cases are rare (a handful of zones, twice a year) and neither raises an
    /// error when got wrong: they move an hour of usage onto the wrong bar. That is why they
    /// are handled here rather than left to <see cref="TimeZoneInfo.ConvertTimeToUtc(DateTime,
    /// TimeZoneInfo)"/>, which throws on the first and silently picks the second on the
    /// other.</para>
    /// </summary>
    public static DateTimeOffset StartUtc(DateOnly day, TimeZoneInfo zone)
    {
        var midnight = day.ToDateTime(TimeOnly.MinValue);

        if (zone.IsInvalidTime(midnight))
        {
            // The gap is a few hours at most; a minute's resolution finds its far edge, which
            // is the instant the day actually starts. Bounded at one day so a pathological
            // rule cannot spin here.
            for (var minutes = 1; minutes <= 24 * 60; minutes++)
            {
                var candidate = midnight.AddMinutes(minutes);
                if (!zone.IsInvalidTime(candidate))
                {
                    return new DateTimeOffset(candidate, zone.GetUtcOffset(candidate)).ToUniversalTime();
                }
            }
        }

        var offset = zone.IsAmbiguousTime(midnight)
            ? zone.GetAmbiguousTimeOffsets(midnight).Max()
            : zone.GetUtcOffset(midnight);

        return new DateTimeOffset(midnight, offset).ToUniversalTime();
    }

    /// <summary>The instant a local day ends, which is the instant the next one begins.</summary>
    public static DateTimeOffset EndUtc(DateOnly day, TimeZoneInfo zone) =>
        StartUtc(day.AddDays(1), zone);

    /// <summary>
    /// How far through its local day an instant falls, as 0–1. The denominator is the day's
    /// real length, so a mark on a 23-hour day lands where it belongs rather than an hour
    /// off the column it annotates.
    /// </summary>
    public static double FractionThrough(DateTimeOffset instant, DateOnly day, TimeZoneInfo zone)
    {
        var start = StartUtc(day, zone);
        var length = EndUtc(day, zone) - start;

        return length <= TimeSpan.Zero
            ? 0
            : Math.Clamp((instant - start) / length, 0, 1);
    }
}
