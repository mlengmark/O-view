using OView.Core.Models;
using OView.Core.Providers.PlanHistory;

namespace OView.Core.Tests;

/// <summary>
/// The weekly reset is the one figure where a stated value outranks a measured one, and the
/// reason is specific: Anthropic assigns it as "a fixed time each week that is assigned to
/// your account", which the user reads directly from Settings → Usage. O-view can only infer
/// it from when a percentage fell between two samples — and that fall lands overnight while
/// Desktop is closed, leaving a ~10 hour bracket and a figure measured 9h29m from Desktop's
/// (GitHub issue #186).
///
/// <para>Precedence over inference, never over evidence. These pin both halves.</para>
/// </summary>
public class ManualWeeklyResetTests
{
    /// <summary>UTC+2 with real DST rules, so the wall-clock arithmetic is actually exercised.</summary>
    private static readonly TimeZoneInfo Berlin =
        TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");

    private static readonly ManualWeeklyReset MondayLate = new(DayOfWeek.Monday, new TimeOnly(22, 59));

    // ── parsing ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParsesAndRoundTrips()
    {
        var parsed = ManualWeeklyReset.Parse("Monday", "22:59");

        Assert.Equal(MondayLate, parsed);
        Assert.Equal("Monday", parsed!.DayText);
        Assert.Equal("22:59", parsed.TimeText);
        Assert.Equal(parsed, ManualWeeklyReset.Parse(parsed.DayText, parsed.TimeText));
    }

    /// <summary>
    /// Unset or unreadable yields "not set" rather than throwing. A corrupt preference must
    /// never be able to stop the panel opening.
    /// </summary>
    [Theory]
    [InlineData("", "")]
    [InlineData("Monday", "")]
    [InlineData("", "22:59")]
    [InlineData("Someday", "22:59")]
    [InlineData("Monday", "10:59 PM")]
    [InlineData("Monday", "25:00")]
    [InlineData(null, null)]
    public void UnreadableInputIsNotSetRatherThanAnError(string? day, string? time)
    {
        Assert.Null(ManualWeeklyReset.Parse(day, time));
    }

    // ── the next occurrence ─────────────────────────────────────────────────────────

    [Fact]
    public void FindsTheNextOccurrenceStrictlyAfterNow()
    {
        // Sunday 2026-08-23 20:00 local -> the following Monday 22:59 local.
        var now = new DateTimeOffset(2026, 8, 23, 18, 0, 0, TimeSpan.Zero);

        var next = MondayLate.NextAfter(now, Berlin);

        Assert.Equal(new DateTimeOffset(2026, 8, 24, 20, 59, 0, TimeSpan.Zero), next);
    }

    /// <summary>
    /// On the day itself, after the time has passed, the answer is next week's — not today's,
    /// which is in the past and would make the countdown run backwards.
    /// </summary>
    [Fact]
    public void OnTheDayItselfAfterTheTimeItRollsToNextWeek()
    {
        var justAfter = new DateTimeOffset(2026, 8, 24, 21, 30, 0, TimeSpan.Zero);   // Mon 23:30 local

        var next = MondayLate.NextAfter(justAfter, Berlin);

        Assert.Equal(new DateTimeOffset(2026, 8, 31, 20, 59, 0, TimeSpan.Zero), next);
    }

    /// <summary>
    /// The entry is a <b>wall-clock</b> time, so it must survive a daylight-saving change.
    /// Computing in UTC instead would silently move every countdown by an hour twice a year.
    /// </summary>
    [Fact]
    public void TheWallClockTimeSurvivesADaylightSavingChange()
    {
        // Europe moves off summer time on 2026-10-25. A Monday either side of that.
        var beforeShift = MondayLate.NextAfter(
            new DateTimeOffset(2026, 10, 19, 6, 0, 0, TimeSpan.Zero), Berlin);
        var afterShift = MondayLate.NextAfter(
            new DateTimeOffset(2026, 10, 27, 6, 0, 0, TimeSpan.Zero), Berlin);

        // Same wall clock on both sides...
        Assert.Equal(22, TimeZoneInfo.ConvertTime(beforeShift, Berlin).Hour);
        Assert.Equal(22, TimeZoneInfo.ConvertTime(afterShift, Berlin).Hour);

        // ...which means a DIFFERENT UTC hour. That difference is the point.
        Assert.NotEqual(beforeShift.UtcDateTime.Hour, afterShift.UtcDateTime.Hour);
    }

    /// <summary>
    /// A spring-forward transition can make an entered wall-clock time not exist. It must
    /// still yield a real instant rather than throwing on one day a year.
    /// </summary>
    [Fact]
    public void ATimeThatDoesNotExistOnADstNightStillResolves()
    {
        // 2026-03-29 02:30 local does not exist in Berlin. Nearest Sunday entry to it.
        var skipped = new ManualWeeklyReset(DayOfWeek.Sunday, new TimeOnly(2, 30));

        var next = skipped.NextAfter(new DateTimeOffset(2026, 3, 27, 0, 0, 0, TimeSpan.Zero), Berlin);

        Assert.True(next > new DateTimeOffset(2026, 3, 27, 0, 0, 0, TimeSpan.Zero));
    }

    // ── contradiction: entry loses to evidence ──────────────────────────────────────


    /// <summary>
    /// The reported instant sits on the same weekly grid as the entry, so the two agree. This
    /// is the normal case for a correct entry and must not raise a false alarm — a warning
    /// people learn to ignore is worse than none.
    /// </summary>
    [Fact]
    public void AnAnchorOnTheSameGridAsTheEntryIsNotAContradiction()
    {
        // Claude reports Mon 20:59 UTC = Mon 22:59 Berlin; the entry says Mon 22:59.
        var anchor = new DateTimeOffset(2026, 8, 24, 20, 59, 59, TimeSpan.Zero);

        Assert.False(MondayLate.IsContradictedBy(anchor, Berlin));
    }

    /// <summary>
    /// A different weekday cannot be the same schedule. Under ADR-0014 both sides are exact,
    /// so this is a straight comparison rather than the "is it inside the bracket?" question
    /// the derived observations used to pose.
    /// </summary>
    [Fact]
    public void AnAnchorOnADifferentDayIsAContradiction()
    {
        // Claude reports Wednesday; the entry claims Monday.
        var anchor = new DateTimeOffset(2026, 8, 26, 6, 0, 0, TimeSpan.Zero);

        Assert.True(MondayLate.IsContradictedBy(anchor, Berlin));
    }

    /// <summary>
    /// Slack absorbs what legitimately separates the two without the entry being wrong: the
    /// anchor is a UTC instant with sub-second precision, the entry a local wall-clock minute
    /// a user read off a screen and rounded.
    /// </summary>
    [Fact]
    public void SmallDisagreementsAreWithinSlackAndDoNotFlag()
    {
        // Mon 22:00 Berlin against an entry of Mon 22:59 — 59 minutes apart, inside slack.
        var anchor = new DateTimeOffset(2026, 8, 24, 20, 0, 0, TimeSpan.Zero);

        Assert.True(ManualWeeklyReset.ContradictionSlack >= TimeSpan.FromHours(1));
        Assert.False(MondayLate.IsContradictedBy(anchor, Berlin));
    }

    // ── the copy ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The notice must say what Claude reported and what to do, and must not pick a culprit —
    /// a plan change, a typo and a genuine schedule change look identical from here.
    /// </summary>
    [Fact]
    public void TheConflictNoticeStatesTheReportedTimeAndTheRemedy()
    {
        var text = PanelText.WeeklyResetConflict(
            new DateTimeOffset(2026, 8, 26, 6, 0, 0, TimeSpan.Zero), Berlin);

        Assert.Contains("Wed 08:00", text, StringComparison.Ordinal);
        Assert.Contains("Re-enter", text, StringComparison.Ordinal);
    }
}
