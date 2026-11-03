using OView.Core.Models;
using OView.Presentation.Text;

namespace OView.Presentation.Tests;

/// <summary>
/// The panel's session-reset line and the tray tooltip describe the same instant, so they must
/// print the same clock time and carry the same <c>~</c> (issue #317).
/// </summary>
public class SessionResetConsistencyTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 11, 0, 0, TimeSpan.Zero);

    private static UsageSnapshot Snapshot(DateTimeOffset reset, TimeSpan? uncertainty) =>
        new(
            DataSource.Live,
            UsageSnapshot.Percent(40),
            UsageSnapshot.Percent(26),
            UsageSnapshot.Instant(reset),
            UsageSnapshot.Instant(Now),
            UsageSnapshot.Instant(null),
            SessionResetUncertainty: uncertainty);

    /// <summary>The clock part of the panel's line, after the " · " that follows the countdown.</summary>
    private static string PanelClock(UsageSnapshot s) =>
        PanelText.SessionReset(s.SessionResetAtUtcValue.Value, Now, Utc, s.SessionResetUncertainty)
            .Split(" · ")[^1];

    /// <summary>The clock part of the tooltip's session segment.</summary>
    private static string TooltipClock(UsageSnapshot s) =>
        TooltipFormatter.Format(s, Utc).Split(" · ")
            .Single(p => p.StartsWith("resets ", StringComparison.Ordinal))["resets ".Length..];

    public static TheoryData<long, string> Instants => new()
    {
        // hh:59:59.6 and hh:00:00.2 sit either side of a minute boundary, the 13:59 / 14:00
        // flip in the report. Truncation keeps them a minute apart; rounding would make the
        // first read 14:00.
        { new DateTimeOffset(2026, 11, 3, 13, 59, 59, 600, TimeSpan.Zero).Ticks, "13:59" },
        { new DateTimeOffset(2026, 11, 3, 14, 0, 0, 200, TimeSpan.Zero).Ticks, "14:00" },
        { new DateTimeOffset(2026, 11, 3, 13, 59, 40, TimeSpan.Zero).Ticks, "13:59" },
    };

    [Theory]
    [MemberData(nameof(Instants))]
    public void PanelAndTooltip_PrintTheSameMinute(long ticks, string expected)
    {
        var s = Snapshot(new DateTimeOffset(ticks, TimeSpan.Zero), TimeSpan.Zero);

        Assert.Equal(expected, PanelClock(s));
        Assert.Equal(expected, TooltipClock(s));
    }

    [Theory]
    [MemberData(nameof(Instants))]
    public void PanelAndTooltip_AgreeInAZoneWithAHalfHourOffset(long ticks, string _)
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("test+5:30", TimeSpan.FromMinutes(330), "t", "t");
        var s = Snapshot(new DateTimeOffset(ticks, TimeSpan.Zero), TimeSpan.Zero);

        var panel = PanelText.SessionReset(s.SessionResetAtUtcValue.Value, Now, zone, s.SessionResetUncertainty)
            .Split(" · ")[^1];
        var tooltip = TooltipFormatter.Format(s, zone).Split(" · ")[1]["resets ".Length..];

        Assert.Equal(panel, tooltip);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(0, false)]
    [InlineData(15, false)]
    [InlineData(16, true)]
    [InlineData(115, true)]
    public void TheTooltipCarriesTheTildeExactlyWhenThePanelDoes(int? uncertaintyMinutes, bool approximate)
    {
        var reset = new DateTimeOffset(2026, 11, 3, 13, 59, 30, TimeSpan.Zero);
        var s = Snapshot(reset, uncertaintyMinutes is { } m ? TimeSpan.FromMinutes(m) : null);

        var panel = PanelClock(s);
        var tooltip = TooltipClock(s);

        Assert.Equal(approximate, panel.StartsWith('~'));
        Assert.Equal(panel, tooltip);
    }

    [Fact]
    public void AnApproximateResetStillPrintsTheSameMinuteInBoth()
    {
        var reset = new DateTimeOffset(2026, 11, 3, 13, 59, 59, 600, TimeSpan.Zero);
        var s = Snapshot(reset, TimeSpan.FromMinutes(115));

        Assert.Equal("~13:59", PanelClock(s));
        Assert.Equal("~13:59", TooltipClock(s));
    }
}
