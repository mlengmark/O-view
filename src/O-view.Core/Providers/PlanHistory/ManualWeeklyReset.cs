using System.Globalization;

namespace OView.Core.Providers.PlanHistory;

/// <summary>
/// A weekly reset the user entered, read off Claude's own Settings → Usage.
///
/// <para><b>Why this is allowed to beat a measurement.</b> Everywhere else in this project a
/// measured value outranks a stated one (rule 6, <c>verify-before-asserting</c>). This is the
/// exception, and it inverts for a specific reason: Anthropic documents the weekly reset as
/// "a fixed time each week that is <b>assigned to your account</b>", whose "reset day and
/// time stay the same regardless of when you start using Claude". So the user is not
/// guessing — they are reading the authoritative source directly, while O-view is inferring
/// it from when a percentage happened to fall between two samples.</para>
///
/// <para>And that inference is weak in a way that does not improve on its own. The drop lands
/// while Desktop is closed for the night, so the bracket is the whole overnight gap —
/// measured at ~10 hours on the development machine, leaving the derived answer 9h29m from
/// Desktop's. A number the user can read in five seconds beats one O-view needs weeks to get
/// within half a day of.</para>
///
/// <para><b>It is still not unfalsifiable.</b> An observed bracket that excludes this time is
/// proof it is wrong — the reset demonstrably happened inside that bracket, so it cannot also
/// have happened outside it. See <see cref="IsContradictedBy"/>: entry wins over inference,
/// never over evidence.</para>
/// </summary>
/// <param name="Day">Weekday of the reset, in the user's local time.</param>
/// <param name="LocalTime">Time of day of the reset, local.</param>
public sealed record ManualWeeklyReset(DayOfWeek Day, TimeOnly LocalTime)
{
    /// <summary>
    /// Slack allowed before an observation is treated as disproving the entry.
    ///
    /// <para>Absorbs the things that legitimately shift a wall-clock boundary without the
    /// entry being wrong: a daylight-saving transition between the observation and now,
    /// Desktop's ~15-minute sampling, and a user who typed the minute they saw rounded. A
    /// contradiction has to be larger than the noise before it is worth telling anyone
    /// about — a false alarm here trains people to ignore the real one.</para>
    /// </summary>
    public static readonly TimeSpan ContradictionSlack = TimeSpan.FromHours(2);

    /// <summary>
    /// Parses the persisted form, or null when unset or unreadable.
    ///
    /// <para>Stored as text rather than as a serialised <see cref="DayOfWeek"/> so a settings
    /// file remains legible and an enum's ordinal can never silently shift meaning. Invalid
    /// input yields "not set" rather than throwing — a corrupt preference must never stop the
    /// panel opening.</para>
    /// </summary>
    public static ManualWeeklyReset? Parse(string? day, string? time)
    {
        if (!Enum.TryParse<DayOfWeek>(day, ignoreCase: true, out var parsedDay))
        {
            return null;
        }

        return TimeOnly.TryParseExact(time, "HH:mm", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var parsedTime)
            ? new ManualWeeklyReset(parsedDay, parsedTime)
            : null;
    }

    /// <summary>The persisted weekday, round-tripping through <see cref="Parse"/>.</summary>
    public string DayText => Day.ToString();

    /// <summary>The persisted time, round-tripping through <see cref="Parse"/>.</summary>
    public string TimeText => LocalTime.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// The next occurrence strictly after <paramref name="utcNow"/>, as UTC.
    ///
    /// <para>Resolved in <paramref name="local"/> and converted, not computed in UTC: the
    /// user entered a wall-clock time, and a wall-clock time survives a daylight-saving
    /// change while a fixed UTC offset does not. Doing this the other way round is the bug
    /// where every countdown silently moves by an hour twice a year.</para>
    /// </summary>
    public DateTimeOffset NextAfter(DateTimeOffset utcNow, TimeZoneInfo local)
    {
        var nowLocal = TimeZoneInfo.ConvertTime(utcNow, local);
        var date = DateOnly.FromDateTime(nowLocal.DateTime);

        // At most 8 candidates: today, then each of the next seven days. The loop rather than
        // arithmetic because a DST transition makes "add N days" and "the next Monday"
        // different questions, and this asks the second one.
        for (var i = 0; i <= 7; i++)
        {
            var candidate = date.AddDays(i);
            if (candidate.DayOfWeek != Day)
            {
                continue;
            }

            var at = ToUtc(candidate, local);
            if (at > utcNow)
            {
                return at;
            }
        }

        // Only reachable when today is the day and the time has passed: take next week's.
        return ToUtc(date.AddDays(7 - ((int)date.DayOfWeek - (int)Day + 7) % 7), local);
    }

    /// <summary>
    /// Whether the reset instant Claude reported proves this entry wrong.
    ///
    /// <para>This used to take an <c>observation</c> — a bracket derived from a drop in Claude
    /// Desktop's sampled series, which said only that the reset fell <i>somewhere inside</i> a
    /// window that was routinely ten hours wide. ADR-0014 replaced that with the instant Claude
    /// reports outright, so the comparison is now exact on both sides: the entry either lands on
    /// the same weekly grid as the anchor or it does not.</para>
    ///
    /// <para><see cref="ContradictionSlack"/> still applies, because the two are expressed
    /// differently — the entry is a local wall-clock day and time, the anchor a UTC instant with
    /// sub-second precision — and a difference of a minute or two is transcription, not
    /// disagreement.</para>
    /// </summary>
    public bool IsContradictedBy(DateTimeOffset anchorUtc, TimeZoneInfo local)
    {
        // This entry's own boundary for the week the anchor falls in. Walking forward from
        // comfortably before it lands on the one boundary that could match, whatever weekday
        // the two disagree about.
        var boundary = NextAfter(anchorUtc.AddDays(-8), local);

        // Bounded: eight days of a seven-day cadence is at most two steps, and a guard beats
        // trusting the arithmetic of a value the user typed.
        for (var step = 0; step < 4 && boundary < anchorUtc - ContradictionSlack; step++)
        {
            boundary = NextAfter(boundary, local);
        }

        return (boundary - anchorUtc).Duration() > ContradictionSlack;
    }

    private DateTimeOffset ToUtc(DateOnly date, TimeZoneInfo local)
    {
        var unspecified = DateTime.SpecifyKind(date.ToDateTime(LocalTime), DateTimeKind.Unspecified);

        // A spring-forward transition can make the entered wall-clock time not exist. Taking
        // the offset of the following hour keeps the boundary real rather than throwing on
        // one day a year.
        var offset = local.IsInvalidTime(unspecified)
            ? local.GetUtcOffset(unspecified.AddHours(1))
            : local.GetUtcOffset(unspecified);

        return new DateTimeOffset(unspecified, offset).ToUniversalTime();
    }
}
