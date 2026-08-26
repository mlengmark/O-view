using OView.Core.Models;

namespace OView.Core.Tests;

/// <summary>
/// The day-boundary arithmetic every local-day figure rests on (issue #211).
///
/// <para>Every case names its zone. None of these may read <see cref="TimeZoneInfo.Local"/>:
/// the whole subject is what a zone does to a boundary, so a test that inherited the machine's
/// would assert nothing on a CI runner sitting in UTC.</para>
/// </summary>
public class LocalDaysTests
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "GMT Standard Time" : "Europe/London");

    private static readonly TimeZoneInfo PlusTwo =
        TimeZoneInfo.CreateCustomTimeZone("test-plus-2", TimeSpan.FromHours(2), "UTC+2", "UTC+2");

    private static DateTimeOffset At(string t) => DateTimeOffset.Parse(t);

    [Fact]
    public void DateOf_TakesTheDayTheReaderWasHaving()
    {
        Assert.Equal(new DateOnly(2026, 8, 25), LocalDays.DateOf(At("2026-08-25T23:26:00Z"), TimeZoneInfo.Utc));
        Assert.Equal(new DateOnly(2026, 8, 26), LocalDays.DateOf(At("2026-08-25T23:26:00Z"), PlusTwo));
    }

    [Fact]
    public void StartUtc_IsMidnightInTheZone()
    {
        Assert.Equal(At("2026-08-24T22:00:00Z"), LocalDays.StartUtc(new DateOnly(2026, 8, 25), PlusTwo));
        Assert.Equal(At("2026-08-25T00:00:00Z"), LocalDays.StartUtc(new DateOnly(2026, 8, 25), TimeZoneInfo.Utc));
    }

    /// <summary>
    /// The DST pair, and the reason none of this is 24-hour arithmetic. London's transitions
    /// are at 01:00 local, so the day that loses an hour is 23 hours long and the day that
    /// gains one is 25 — measured from the zone, not assumed.
    /// </summary>
    [Fact]
    public void ALocalDayIsNotAlways24Hours()
    {
        var spring = new DateOnly(2026, 3, 29);
        var autumn = new DateOnly(2026, 10, 25);

        Assert.Equal(TimeSpan.FromHours(23), LocalDays.EndUtc(spring, London) - LocalDays.StartUtc(spring, London));
        Assert.Equal(TimeSpan.FromHours(25), LocalDays.EndUtc(autumn, London) - LocalDays.StartUtc(autumn, London));
    }

    [Fact]
    public void EndUtc_IsExactlyWhereTheNextDayBegins()
    {
        var day = new DateOnly(2026, 10, 25);

        Assert.Equal(LocalDays.StartUtc(day.AddDays(1), London), LocalDays.EndUtc(day, London));
    }

    /// <summary>
    /// Consecutive days tile the timeline with no gap and no overlap, through both
    /// transitions. A gap loses usage; an overlap counts it twice — and neither raises
    /// anything, they just move a figure.
    /// </summary>
    [Theory]
    [InlineData("2026-03-25")]
    [InlineData("2026-10-21")]
    public void ConsecutiveDaysTileWithoutGapOrOverlap(string from)
    {
        var day = DateOnly.Parse(from);

        for (var i = 0; i < 10; i++, day = day.AddDays(1))
        {
            Assert.Equal(LocalDays.StartUtc(day.AddDays(1), London), LocalDays.EndUtc(day, London));
        }
    }

    /// <summary>
    /// The fraction is measured against the day's real length, which is what keeps a gridline
    /// over the bar it annotates. Midday on a 25-hour day is 12.5 hours in, not 12.
    /// </summary>
    [Fact]
    public void FractionThrough_UsesTheDaysRealLength()
    {
        var autumn = new DateOnly(2026, 10, 25);
        var start = LocalDays.StartUtc(autumn, London);

        Assert.Equal(0.5, LocalDays.FractionThrough(start.AddHours(12.5), autumn, London), 6);
        Assert.Equal(0.48, LocalDays.FractionThrough(start.AddHours(12), autumn, London), 2);

        var ordinary = new DateOnly(2026, 8, 25);
        Assert.Equal(
            0.5,
            LocalDays.FractionThrough(LocalDays.StartUtc(ordinary, London).AddHours(12), ordinary, London),
            6);
    }

    [Fact]
    public void FractionThrough_IsClampedToItsOwnDay()
    {
        var day = new DateOnly(2026, 8, 25);

        Assert.Equal(0, LocalDays.FractionThrough(LocalDays.StartUtc(day, London).AddHours(-1), day, London));
        Assert.Equal(1, LocalDays.FractionThrough(LocalDays.EndUtc(day, London).AddHours(1), day, London));
    }

    /// <summary>
    /// A zone whose clock jumps forward <i>over</i> midnight has no local 00:00 on that day.
    /// The day begins at the jump — and the point is that it begins at all: converting that
    /// non-existent time throws, which would take the panel down rather than shift a figure.
    /// </summary>
    [Fact]
    public void ADayWithNoLocalMidnight_StartsAtTheJump()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone(
            "test-midnight-jump",
            TimeSpan.FromHours(-3),
            "midnight jump",
            "standard",
            "daylight",
            [
                TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                    DateTime.MinValue.Date,
                    DateTime.MaxValue.Date,
                    TimeSpan.FromHours(1),
                    TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1), 10, 18),
                    TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1), 2, 15)),
            ]);

        var jumpDay = new DateOnly(2026, 10, 18);

        // Midnight does not exist here; the day starts one hour later on the wall clock.
        Assert.True(zone.IsInvalidTime(jumpDay.ToDateTime(TimeOnly.MinValue)));
        Assert.Equal(jumpDay, LocalDays.DateOf(LocalDays.StartUtc(jumpDay, zone), zone));
        Assert.Equal(
            LocalDays.StartUtc(jumpDay.AddDays(1), zone),
            LocalDays.EndUtc(jumpDay, zone));
    }
}
