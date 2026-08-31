using OView.Core.Models;
using OView.Core.Pricing;

namespace OView.Core.Tests;

/// <summary>
/// The dated rate card and the drift check over it (GitHub issues #255 and #257).
///
/// <para>Two failures produced this mechanism and they fail differently. The cache-write
/// multiplier was a published price collapsed into a constant, which no amount of checking the
/// table's <i>date</i> would have caught. The Sonnet 5 row was a forecast entered as a fact —
/// wrong on the day it was written, so an age check would not have caught that either. Hence a
/// date that is data <b>and</b> a check that compares values.</para>
/// </summary>
public class RateCardTests
{
    [Fact]
    public void TheBundledCardIsDatedAsData_NotAsAComment()
    {
        // The whole point: something in the app can read this, so something can say it.
        Assert.Equal(RateCardSource.Bundled, ModelCatalog.Bundled.Source);
        Assert.Equal(ModelCatalog.AsOf, ModelCatalog.Bundled.AsOf);
        Assert.NotEmpty(ModelCatalog.Bundled.Models);
    }

    [Fact]
    public void StalenessIsMeasuredFromTheCardsOwnDate()
    {
        var card = ModelCatalog.Bundled with { AsOf = new DateOnly(2026, 1, 1) };

        Assert.False(card.IsStaleOn(new DateOnly(2026, 3, 31)));   // 89 days
        Assert.True(card.IsStaleOn(new DateOnly(2026, 4, 2)));     // 91 days
    }

    [Fact]
    public void FindResolvesByLongestPrefix_ThroughTheCard()
    {
        Assert.Equal("Opus 4.8", ModelCatalog.Bundled.Find("claude-opus-4-8-20260101")?.DisplayName);
        Assert.Equal("Opus 5", ModelCatalog.Bundled.Find("claude-opus-5")?.DisplayName);
        Assert.Null(ModelCatalog.Bundled.Find("claude-brandnew-9"));
    }

    [Fact]
    public void RatesForFailsToUnknown_ForEveryUnresolvableCase()
    {
        var fast = new UsageModifiers(ModifierValue.Applied, ModifierValue.Standard);
        var card = ModelCatalog.Bundled;

        Assert.Null(card.RatesFor("claude-brandnew-9", UsageModifiers.Standard));
        Assert.Null(card.RatesFor("claude-opus-5", UsageModifiers.From("turbo", null)));
        Assert.Null(card.RatesFor("claude-haiku-4-5", fast));

        Assert.NotNull(card.RatesFor("claude-opus-5", fast));
    }

    // ── the drift check ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The published table's real shape, trimmed to four rows. Kept verbatim — including the
    /// parenthesised availability links and the padding — because those are exactly what a
    /// hand-written fixture would tidy away and a parser would then fail on.
    /// </summary>
    private const string PublishedMarkdown = """
        ## Model pricing

        The following table shows pricing for all Claude models:

        | Model                                                     | Base Input Tokens | 5m Cache Writes | 1h Cache Writes | Cache Hits & Refreshes | Output Tokens |
        | --------------------------------------------------------- | ----------------- | --------------- | --------------- | ---------------------- | ------------- |
        | Claude Fable 5                                            | $10 / MTok        | $12.50 / MTok   | $20 / MTok      | $1 / MTok              | $50 / MTok    |
        | Claude Mythos 5 ([limited availability](https://x/y))      | $10 / MTok        | $12.50 / MTok   | $20 / MTok      | $1 / MTok              | $50 / MTok    |
        | Claude Opus 5                                             | $5 / MTok         | $6.25 / MTok    | $10 / MTok      | $0.50 / MTok           | $25 / MTok    |
        | Claude Sonnet 5                                           | $2 / MTok         | $2.50 / MTok    | $4 / MTok       | $0.20 / MTok           | $10 / MTok    |

        ### Batch processing

        | Model         | Batch input | Batch output |
        | ------------- | ----------- | ------------ |
        | Claude Opus 5 | $2.50 / MTok | $12.50 / MTok |
        """;

    private static readonly DateOnly Today = new(2026, 8, 31);

    [Fact]
    public void ABundledCardThatMatchesThePublishedTableReportsNoDifferences()
    {
        var drift = PublishedRates.Compare(ModelCatalog.Bundled, PublishedMarkdown, Today);

        Assert.NotNull(drift);
        Assert.True(drift.Agrees, drift.Describe());
    }

    /// <summary>
    /// The case the check exists for, reconstructed: the table carrying the cancelled $3/$15
    /// forecast for Sonnet 5. Every column that moved is named, so a reader is told which
    /// figures were wrong rather than only that something was.
    /// </summary>
    [Fact]
    public void AWrongRateIsReportedPerColumn()
    {
        var forecast = ModelCatalog.Bundled with
        {
            Models = ModelCatalog.Bundled.Models
                .Select(e => e.DisplayName == "Sonnet 5"
                    ? e with { Rates = new ModelRates(3.00m, 15.00m, 3.75m, 6.00m, 0.30m) }
                    : e)
                .ToList(),
        };

        var drift = PublishedRates.Compare(forecast, PublishedMarkdown, Today);

        Assert.NotNull(drift);
        Assert.All(drift.Differences, d => Assert.Equal("Sonnet 5", d.Model));
        Assert.Equal(5, drift.Differences.Count);
        var input = drift.Differences.Single(d => d.Column == "input");
        Assert.Equal(3.00m, input.Ours);
        Assert.Equal(2.00m, input.Published);
    }

    /// <summary>
    /// The most important property here: a check that could not be made says so. Reporting
    /// agreement because the page did not parse is the one outcome that would make this
    /// mechanism worse than not having it (rule 6).
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("# Pricing\n\nSee claude.com/pricing.\n")]
    [InlineData("| Model | Batch input | Batch output |\n| --- | --- | --- |\n| Claude Opus 5 | $2.50 / MTok | $12.50 / MTok |")]
    public void APageThatDoesNotParseIsNull_NeverAnEmptyDifferenceList(string markdown)
    {
        Assert.Null(PublishedRates.Compare(ModelCatalog.Bundled, markdown, Today));
    }

    /// <summary>
    /// A model the page does not list is not a difference. The comparison matches on display
    /// name, so a rename upstream has to read as "could not check that row" rather than as a
    /// price change on every column of it.
    /// </summary>
    [Fact]
    public void AModelMissingFromThePublishedTableIsNotReportedAsADifference()
    {
        var drift = PublishedRates.Compare(ModelCatalog.Bundled, PublishedMarkdown, Today);

        Assert.NotNull(drift);
        Assert.True(drift.Agrees);
        // Haiku 4.5 is in the bundled card and not in this fixture's table.
        Assert.Contains(ModelCatalog.Bundled.Models, e => e.DisplayName == "Haiku 4.5");
    }
}
