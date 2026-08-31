using OView.Core.Models;
using OView.Core.Providers.CachedUsage;

namespace OView.Core.Tests;

/// <summary>
/// The words on the boost chip and in the card behind it (issue #254).
///
/// <para>Shared copy, so both heads say the same thing — the reason
/// <see cref="PanelText"/> exists at all.</para>
/// </summary>
public class BoostChipTextTests
{
    /// <summary>2026-08-31T08:24:24Z, the real flag-cache fetch time.</summary>
    private static readonly DateTimeOffset Now = new(2026, 8, 31, 8, 24, 24, TimeSpan.Zero);

    /// <summary>
    /// UTC for the string assertions. A promo ends at the end of its last <i>local</i> day, so
    /// a zone that is not UTC would shift every countdown by its offset and make the expected
    /// values a puzzle rather than a specification.
    /// </summary>
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static BoostNotice Notice(int? percent, DateOnly? endsOn, string text = "…") =>
        new(BoostNotice.WeeklyBar, text, percent, endsOn);

    /// <summary>
    /// The shape the panel actually shows today: figure first, word immediately after, then the
    /// date and the countdown. The figure leads because the same row carries utilisation a
    /// hundred pixels to the right — a level, where this is a delta — and the word between them
    /// is what keeps the two from reading as the same kind of number.
    /// </summary>
    [Fact]
    public void LeadsWithTheFigureAndFollowsItWithTheWord()
    {
        Assert.Equal(
            "50% Boosted · until 31 Aug · ends in 15h",
            PanelText.BoostChip(Notice(50, new DateOnly(2026, 8, 31)), Now, Utc));
    }

    [Theory]
    // Both figures, several magnitudes.
    [InlineData(100, "2026-09-18", "100% Boosted · until 18 Sep · ends in 2w 4d 15h")]
    [InlineData(5, "2026-09-04", "5% Boosted · until 4 Sep · ends in 4d 15h")]
    // No magnitude parsed: the chip still names the state, and the message still shows.
    [InlineData(null, "2026-09-18", "Boosted · until 18 Sep · ends in 2w 4d 15h")]
    public void CarriesWhateverResolved(int? percent, string endsOn, string expected)
    {
        Assert.Equal(expected,
            PanelText.BoostChip(Notice(percent, DateOnly.Parse(endsOn)), Now, Utc));
    }

    /// <summary>
    /// The floor. With neither figure the chip is one word — and the hover card still relays
    /// Claude's sentence, which is the part that never depended on parsing.
    /// </summary>
    [Theory]
    [InlineData(50, "50% Boosted")]
    [InlineData(null, "Boosted")]
    public void WithoutADateThereIsNoCountdown(int? percent, string expected)
    {
        Assert.Equal(expected, PanelText.BoostChip(Notice(percent, endsOn: null), Now, Utc));
    }

    /// <summary>
    /// Empty leading units are dropped: a promo ending tonight reads <c>15h</c>, never
    /// <c>0w 0d 15h</c>.
    /// </summary>
    [Theory]
    [InlineData(0, 0, 14, "14h")]
    [InlineData(0, 4, 3, "4d 3h")]
    [InlineData(2, 4, 14, "2w 4d 14h")]
    // Once a larger unit is present the smaller ones stay, zero or not — "2w 0d 0h" reads as a
    // precise two weeks, where "2w" alone invites the reader to wonder what was rounded away.
    [InlineData(3, 0, 0, "3w 0d 0h")]
    public void CountsDownInWeeksDaysAndHours(int weeks, int days, int hours, string expected)
    {
        var left = TimeSpan.FromDays((weeks * 7) + days) + TimeSpan.FromHours(hours);

        Assert.Equal(expected, PanelText.BoostRemaining(left));
    }

    /// <summary>
    /// Hours are the floor because the source gives a <i>date</i>. Counting the last stretch in
    /// minutes would imply a precision "Aug 31" never carried.
    /// </summary>
    [Theory]
    [InlineData(40)]
    [InlineData(0)]
    [InlineData(-90)]
    public void TheLastStretchIsNotCountedInMinutes(int minutes)
    {
        Assert.Equal("under an hour", PanelText.BoostRemaining(TimeSpan.FromMinutes(minutes)));
    }

    /// <summary>
    /// The promo runs to the end of its last day in the reader's own zone, not to its start —
    /// a chip reading "ends in 0h" all through the final day would be wrong every hour of it.
    /// </summary>
    [Fact]
    public void APromoRunsToTheEndOfItsLastLocalDay()
    {
        var justBeforeMidnight = new DateTimeOffset(2026, 8, 31, 23, 0, 0, TimeSpan.Zero);

        Assert.Equal(
            "50% Boosted · until 31 Aug · ends in 1h",
            PanelText.BoostChip(Notice(50, new DateOnly(2026, 8, 31)), justBeforeMidnight, Utc));
    }

    /// <summary>
    /// The card carries Claude's sentence unedited, then says who reported it and when O-view
    /// read it. O-view cannot verify that this account is boosted — the payload is a
    /// feature-flag cache — so the provenance is what makes relaying it honest (rule 6).
    /// </summary>
    [Fact]
    public void TheCardRelaysTheMessageAndAttributesIt()
    {
        var notice = Notice(50, new DateOnly(2026, 8, 31),
            "+50% weekly limits promo through Aug 31 · clau.de/cc-50-promo");

        Assert.Equal(
            "+50% weekly limits promo through Aug 31 · clau.de/cc-50-promo\n\n"
            + "Ends Mon 31 Aug · reported by Claude Code, read 08:24",
            PanelText.BoostCard(notice, Now, Utc));
    }

    /// <summary>Without a date the card drops that clause rather than inventing one.</summary>
    [Fact]
    public void TheCardOmitsAnEndDateItDoesNotHave()
    {
        Assert.Equal(
            "Weekly limits boosted\n\nreported by Claude Code, read 08:24",
            PanelText.BoostCard(Notice(null, null, "Weekly limits boosted"), Now, Utc));
    }
}
