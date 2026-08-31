using OView.Core.Models;
using OView.Core.Providers.CachedUsage;

namespace OView.Core.Tests;

/// <summary>
/// Reading Claude Code's cached promo notices, and deciding when one may be shown (issue #254).
///
/// <para>Fixtures follow the real shape of
/// <c>cachedGrowthBookFeatures.tengu_rate_limit_promo_notices</c> as measured on 2026-08-31.</para>
/// </summary>
public class BoostNoticeTests
{
    /// <summary>2026-08-31T08:24:24Z — the real <c>cachedGrowthBookFeaturesAt</c> that day.</summary>
    private static readonly DateTimeOffset Fetched = new(2026, 8, 31, 8, 24, 24, TimeSpan.Zero);

    private static readonly DateOnly Today = new(2026, 8, 31);

    private const string RealText =
        "+50% weekly limits promo through Aug 31 \\u00b7 clau.de/cc-50-promo";

    private static string Document(
        string bar = BoostNotice.WeeklyBar,
        string text = RealText,
        DateTimeOffset? fetchedAt = null) =>
        $$"""
        {
          "cachedGrowthBookFeatures": {
            "tengu_kairos_loop_dynamic": true,
            "tengu_rate_limit_promo_notices": [
              { "bar": "{{bar}}", "text": "{{text}}", "variant": "claude" }
            ]
          },
          "cachedGrowthBookFeaturesAt": {{(fetchedAt ?? Fetched).ToUnixTimeMilliseconds()}}
        }
        """;

    [Fact]
    public void ReadsTheNoticeAndBothFiguresOutOfIt()
    {
        var notices = BoostNotices.Parse(Document());

        Assert.NotNull(notices);
        Assert.Equal(Fetched, notices.FetchedAtUtc);

        var notice = Assert.Single(notices.Items);
        Assert.Equal(BoostNotice.WeeklyBar, notice.Bar);
        Assert.Equal(50, notice.Percent);
        Assert.Equal(new DateOnly(2026, 8, 31), notice.EndsOn);
    }

    /// <summary>
    /// The message is carried through exactly as written — it is the one part of this feature
    /// that never depends on parsing, and the hover card shows it unedited.
    /// </summary>
    [Fact]
    public void KeepsTheMessageVerbatim()
    {
        var notice = Assert.Single(BoostNotices.Parse(Document())!.Items);

        Assert.Equal(
            "+50% weekly limits promo through Aug 31 · clau.de/cc-50-promo",
            notice.Text);
    }

    /// <summary>
    /// No timestamp, no notices. An undated claim of unknown age is exactly the
    /// confidently-wrong statement rule 6 exists to prevent, and the same reason
    /// <see cref="CachedUtilization.Parse"/> refuses a block with no <c>fetchedAtMs</c>.
    /// </summary>
    [Fact]
    public void RefusesTheWholeBlockWithoutAFetchTime()
    {
        Assert.Null(BoostNotices.Parse("""
            { "cachedGrowthBookFeatures": { "tengu_rate_limit_promo_notices": [] } }
            """));
    }

    /// <summary>
    /// A file with the timestamp but no notices is a valid, empty answer — the ordinary state
    /// when no promo is running. It must read as "none", not as "unreadable", or every machine
    /// without a promo would look broken.
    /// </summary>
    [Theory]
    [InlineData("""{ "cachedGrowthBookFeaturesAt": 1788164664630 }""")]
    [InlineData("""{ "cachedGrowthBookFeatures": {}, "cachedGrowthBookFeaturesAt": 1788164664630 }""")]
    [InlineData("""
        { "cachedGrowthBookFeatures": { "tengu_rate_limit_promo_notices": [] },
          "cachedGrowthBookFeaturesAt": 1788164664630 }
        """)]
    public void NoNoticesIsAnAnswer(string json)
    {
        var notices = BoostNotices.Parse(json);

        Assert.NotNull(notices);
        Assert.Empty(notices.Items);
    }

    /// <summary>
    /// A notice missing either required field is dropped rather than defaulted. Guessing the bar
    /// would attach someone's promo to a meter the source never named.
    /// </summary>
    [Theory]
    [InlineData("""{ "text": "+50% weekly limits through Sep 4" }""")]
    [InlineData("""{ "bar": "seven_day" }""")]
    [InlineData("""{ "bar": "seven_day", "text": "" }""")]
    [InlineData("""{ "bar": 7, "text": "+50% through Sep 4" }""")]
    [InlineData("\"not an object\"")]
    public void DropsAnUnusableEntryWithoutLosingTheRest(string entry)
    {
        var notices = BoostNotices.Parse($$"""
            {
              "cachedGrowthBookFeatures": {
                "tengu_rate_limit_promo_notices": [
                  {{entry}},
                  { "bar": "seven_day", "text": "+25% weekly limits through Sep 4" }
                ]
              },
              "cachedGrowthBookFeaturesAt": {{Fetched.ToUnixTimeMilliseconds()}}
            }
            """);

        var kept = Assert.Single(notices!.Items);
        Assert.Equal(25, kept.Percent);
    }

    /// <summary>
    /// The whole point of extracting a date: on the day after the promo ends, the chip goes away
    /// because the machine's own calendar says so — not because Claude Code stopped running.
    /// </summary>
    [Fact]
    public void HidesAPromoOnceItsLastDayHasPassed()
    {
        var notices = BoostNotices.Parse(Document())!;

        Assert.NotNull(notices.For(BoostNotice.WeeklyBar, Today, Fetched));
        Assert.NotNull(notices.For(BoostNotice.WeeklyBar, Today, Fetched.AddDays(30)));
        Assert.Null(notices.For(BoostNotice.WeeklyBar, Today.AddDays(1), Fetched));
    }

    /// <summary>
    /// Without a date there is nothing to check against the clock, so the only remaining
    /// evidence is that Claude Code recently agreed the promo was running. That evidence
    /// expires.
    /// </summary>
    [Fact]
    public void AnUndatedNoticeSurvivesOnlyWhileTheFlagCacheIsFresh()
    {
        var notices = BoostNotices.Parse(Document(text: "Weekly limits boosted this month"))!;
        var notice = Assert.Single(notices.Items);
        Assert.Null(notice.EndsOn);

        Assert.NotNull(notices.For(BoostNotice.WeeklyBar, Today, Fetched));
        Assert.Null(notices.For(
            BoostNotice.WeeklyBar, Today, Fetched + BoostNotices.UndatedFreshness.Add(TimeSpan.FromMinutes(1))));
    }

    /// <summary>
    /// A notice is never shown against a bar it did not name. That covers the model-scoped keys
    /// (<c>seven_day_opus</c> and friends) — none has ever been observed populated, and drawing
    /// one on the weekly gauge would put a model's promo on an account-wide meter.
    /// </summary>
    [Theory]
    [InlineData("seven_day_opus")]
    [InlineData("seven_day_sonnet")]
    [InlineData("five_hour")]
    [InlineData("something_new")]
    public void ANoticeIsNeverShownAgainstABarItDidNotName(string bar)
    {
        var notices = BoostNotices.Parse(Document(bar: bar))!;

        Assert.Null(notices.For(BoostNotice.WeeklyBar, Today, Fetched));
    }

    /// <summary>
    /// Same trap as <see cref="CachedUtilization.TryReadAny"/>: Claude Code's state migration
    /// leaves a stub file behind, so resolution is by which candidate carries the freshest
    /// block, never by which exists first.
    /// </summary>
    [Fact]
    public void TheFreshestCandidateWins_NotTheFirstThatExists()
    {
        var dir = Directory.CreateTempSubdirectory("oview-tests-").FullName;
        try
        {
            var stale = Path.Combine(dir, "abandoned.json");
            File.WriteAllText(stale, Document(
                text: "+10% weekly limits through Sep 4", fetchedAt: Fetched.AddHours(-6)));

            var current = Path.Combine(dir, "current.json");
            File.WriteAllText(current, Document(text: "+90% weekly limits through Sep 4"));

            Assert.Equal(90, BoostNotices.TryReadAny([stale, current])?.Items[0].Percent);
            Assert.Equal(90, BoostNotices.TryReadAny([current, stale])?.Items[0].Percent);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// An unreadable or malformed candidate must not take the panel down, and must not blank a
    /// good one beside it — the contract every reader in this layer follows.
    /// </summary>
    [Fact]
    public void AMalformedOrMissingCandidateIsSkipped()
    {
        var dir = Directory.CreateTempSubdirectory("oview-tests-").FullName;
        try
        {
            var broken = Path.Combine(dir, "broken.json");
            File.WriteAllText(broken, "{ not json");

            var good = Path.Combine(dir, "good.json");
            File.WriteAllText(good, Document());

            Assert.Equal(50, BoostNotices.TryReadAny([broken, good])?.Items[0].Percent);
            Assert.Null(BoostNotices.TryReadAny([broken, Path.Combine(dir, "absent.json")]));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// The three candidate lists must stay the same list. The account badge, the percentages and
    /// the promo notice all come out of one file, and three readers disagreeing about which
    /// <c>.claude.json</c> is in effect is a class of bug this repo has already had.
    /// </summary>
    [Fact]
    public void LooksWhereTheAccountAndUsageReadersLook()
    {
        Assert.Equal(ClaudeAccount.Candidates(), BoostNotices.Candidates());
        Assert.Equal(CachedUtilization.Candidates(), BoostNotices.Candidates());
    }
}
