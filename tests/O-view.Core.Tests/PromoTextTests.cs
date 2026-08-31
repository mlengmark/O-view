using OView.Core.Providers.CachedUsage;

namespace OView.Core.Tests;

/// <summary>
/// The two extractors that read a promo notice's sentence (issue #254).
///
/// <para><b>These tables are the specification, and the rows that must NOT resolve are the
/// half that matters.</b> Both extractors are allowed to give up — the message is relayed
/// verbatim either way, so a miss costs a detail on a chip. What neither may ever do is produce
/// a confident wrong answer, which is why every ambiguous shape has a row here pinning it as
/// refused.</para>
/// </summary>
public class PromoTextTests
{
    /// <summary>
    /// The real payload, read from the development machine on 2026-08-31. Every other message
    /// below is a plausible variation on it; this one actually shipped.
    /// </summary>
    private const string RealNotice =
        "+50% weekly limits promo through Aug 31 · clau.de/cc-50-promo";

    /// <summary>The moment that payload's flag cache was fetched: 2026-08-31T08:24:24Z.</summary>
    private static readonly DateTimeOffset Anchor = new(2026, 8, 31, 8, 24, 24, TimeSpan.Zero);

    [Theory]
    // The real one, and the wording Claude's own web settings page uses.
    [InlineData(RealNotice, "2026-08-31")]
    [InlineData("Your weekly Claude Code limit is 50% higher through August 31", "2026-08-31")]
    [InlineData("Weekly limits +50% until Sep 4", "2026-09-04")]
    [InlineData("Boosted limits through 31 Aug", "2026-08-31")]
    [InlineData("Promo ends September 4, 2026", "2026-09-04")]
    [InlineData("Higher weekly limits valid through 2026-09-04", "2026-09-04")]
    // Year rollover: read in August, "Jan 2" is the NEXT January, not the one already past.
    [InlineData("+50% weekly limits promo through Jan 2", "2027-01-02")]
    // An explicit year is taken as written, even when it is behind the anchor — that is what
    // makes an expired promo detectable rather than silently re-dated into the future.
    [InlineData("Promo through Aug 3 2025", "2025-08-03")]
    public void ReadsAnEndDate(string text, string expected)
    {
        Assert.Equal(DateOnly.Parse(expected), PromoText.EndDate(text, Anchor));
    }

    [Theory]
    // Numeric M/d is 4 September to the writer and 9 April to a British reader, and nothing in
    // the payload says which. Refusing costs a countdown; guessing puts a wrong month on screen.
    [InlineData("Limits boosted through 9/4")]
    // No date in the sentence at all.
    [InlineData("+50% weekly limits for the next two weeks")]
    [InlineData("Weekly limits boosted this month")]
    // Not English. The message still shows; only the countdown is lost.
    [InlineData("Limites hebdomadaires +50% jusqu'au 31 août")]
    // A date with no lead-in word could be a start, a billing date, anything.
    [InlineData("Weekly limits boosted. Sep 4 update available")]
    // No year, and no candidate year lands inside the accepted window: read on 31 August,
    // "Mar 15" is 169 days behind or 196 ahead, and neither is a promo this notice is about.
    // An EXPLICIT year is a different matter and is taken as written — that is what makes
    // "Aug 3 2025" above read as expired rather than being re-dated into the future.
    [InlineData("Weekly limits boosted through Mar 15")]
    public void RefusesADateItCannotReadWithoutGuessing(string text)
    {
        Assert.Null(PromoText.EndDate(text, Anchor));
    }

    /// <summary>
    /// The year is inferred from the <b>anchor</b>, not from now, so a cache read long after it
    /// was written still resolves the year its author meant rather than sliding forward.
    /// </summary>
    [Fact]
    public void InfersTheYearFromTheAnchorRatherThanFromNow()
    {
        var lastYear = new DateTimeOffset(2025, 8, 31, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal(new DateOnly(2025, 8, 31), PromoText.EndDate(RealNotice, lastYear));
        Assert.Equal(new DateOnly(2026, 8, 31), PromoText.EndDate(RealNotice, Anchor));
    }

    [Theory]
    [InlineData(RealNotice, 50)]
    [InlineData("Your weekly Claude Code limit is 50% higher through August 31", 50)]
    [InlineData("+100% weekly limits promo through Sep 4", 100)]
    [InlineData("Extra 25% weekly limits through Sep 4", 25)]
    [InlineData("+5% weekly limits promo through Sep 4", 5)]
    public void ReadsAMagnitude(string text, int expected)
    {
        Assert.Equal(expected, PromoText.Percent(text));
    }

    [Theory]
    // Nothing to take — "doubled" is not a percentage, and inventing 100 would be fabrication.
    [InlineData("Weekly limits doubled through Sep 4")]
    // Two percentages: the sentence is doing something never observed, and picking one is a guess.
    [InlineData("Save 20% on Max and get 50% higher limits")]
    // The row this rule exists for. A sentence about CONSUMPTION must never become a boost
    // figure — the weekly bar already shows a percentage, and it means the opposite of this one.
    [InlineData("You have used 50% of your weekly limit")]
    [InlineData("Weekly limit 80% reached")]
    // Zero is not a boost.
    [InlineData("+0% weekly limits promo through Sep 4")]
    public void RefusesAMagnitudeThatIsNotPlainlyAnIncrease(string text)
    {
        Assert.Null(PromoText.Percent(text));
    }

    /// <summary>
    /// The real payload's URL carries <c>50</c> but not <c>50%</c>, so it does not trip the
    /// two-percentages rule. Checked against the shipped string rather than assumed — if a
    /// future promo's short link ever included a percent sign, the magnitude would correctly
    /// disappear rather than pick the wrong number.
    /// </summary>
    [Fact]
    public void AShortLinkContainingDigitsIsNotASecondPercentage()
    {
        Assert.Equal(50, PromoText.Percent(RealNotice));
        Assert.Null(PromoText.Percent("+50% weekly limits · clau.de/cc-50%-promo"));
    }

    [Fact]
    public void EmptyAndNullTextYieldNothing()
    {
        Assert.Null(PromoText.Percent(null));
        Assert.Null(PromoText.Percent(""));
        Assert.Null(PromoText.EndDate(null, Anchor));
        Assert.Null(PromoText.EndDate("", Anchor));
    }
}
