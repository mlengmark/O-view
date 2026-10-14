using OView.Core.Models;
using OView.Core.Pricing;

namespace OView.Core.Tests;

/// <summary>
/// The catalog replaced three hand-maintained tables keyed on the same prefixes
/// (GitHub issue #56). These tests pin the properties that made keeping them in sync a
/// manual job — a row that is half-filled, or a caption that names a different set than
/// the classifier matches, must fail here rather than in front of a user.
/// </summary>
public class ModelCatalogTests
{
    [Theory]
    [InlineData("claude-opus-5", "Opus 5")]
    [InlineData("claude-opus-4-8", "Opus 4.8")]
    [InlineData("claude-fable-5", "Fable 5")]
    [InlineData("claude-haiku-4-5", "Haiku 4.5")]
    public void Find_ResolvesKnownModels(string model, string expectedName)
    {
        Assert.Equal(expectedName, ModelCatalog.Find(model)?.DisplayName);
    }

    [Fact]
    public void Find_MatchesDatedSnapshotsByPrefix()
    {
        // Transcripts carry dated ids; they must resolve to the base model's row.
        Assert.Equal("Opus 5", ModelCatalog.Find("claude-opus-5-20260501")?.DisplayName);
    }

    [Fact]
    public void Find_ReturnsNull_ForUnrecognisedModel()
    {
        // No row means no guess: the UI shows the raw id and names it as unpriced,
        // rather than inventing a name or a rate from the pattern (rule 6).
        Assert.Null(ModelCatalog.Find("claude-brandnew-9"));
        Assert.Null(ModelCatalog.Find("unknown"));
    }

    [Fact]
    public void Find_PrefersTheLongestMatchingPrefix_NotDeclarationOrder()
    {
        // The three predecessor tables matched with FirstOrDefault and relied on being
        // written "most-specific first", which made declaration order load-bearing
        // without saying so. Resolution is by prefix length now, so a row inserted in
        // the wrong place cannot shadow a more specific one.
        var entry = ModelCatalog.Find("claude-opus-4-8");

        Assert.Equal("claude-opus-4-8", entry?.Prefix);
        Assert.Equal("Opus 4.8", entry?.DisplayName);
    }

    [Fact]
    public void EveryEntry_IsFullySpecified()
    {
        // The failure the catalog exists to prevent: a model added to one table and
        // forgotten in another. One row now means it cannot be partially added, and
        // this asserts no row was left with a placeholder.
        foreach (var billing in new[] { BillingClass.Plan, BillingClass.Credit })
        {
            foreach (var entry in ModelCatalog.InClass(billing))
            {
                Assert.StartsWith("claude-", entry.Prefix, StringComparison.Ordinal);
                Assert.NotEmpty(entry.DisplayName);
                Assert.NotEqual(entry.Prefix, entry.DisplayName);
                // Every published column, not just the two the table used to carry. A row
                // half-filled in the new columns would price cache traffic at zero, which
                // is a confident wrong number rather than a visible gap (issue #255).
                Assert.True(entry.Rates.InputPerMTok > 0, $"{entry.Prefix} has no input rate");
                Assert.True(entry.Rates.OutputPerMTok > 0, $"{entry.Prefix} has no output rate");
                Assert.True(entry.Rates.CacheWrite5mPerMTok > 0, $"{entry.Prefix} has no 5m cache-write rate");
                Assert.True(entry.Rates.CacheWrite1hPerMTok > 0, $"{entry.Prefix} has no 1h cache-write rate");
                Assert.True(entry.Rates.CacheReadPerMTok > 0, $"{entry.Prefix} has no cache-read rate");
            }
        }
    }

    [Fact]
    public void EveryPricedModel_IsAlsoNameable()
    {
        // CostEstimator and ModelDisplayName read the same rows, so a model that can be
        // priced can always be named. Previously these were separate tables and either
        // could be missing while the other was present.
        foreach (var entry in ModelCatalog.InClass(BillingClass.Plan)
                     .Concat(ModelCatalog.InClass(BillingClass.Credit)))
        {
            Assert.NotNull(CostEstimator.EstimateUsd(
                entry.Prefix, TokenSplit.Empty with { Input = 1_000_000 }));
            Assert.Equal(entry.DisplayName, ModelDisplayName.For(entry.Prefix));
        }
    }

    // CreditDisplayList_* moved to PanelTextTests.cs — the caption they check is built in
    // Presentation now, from CreditBilledModels.Models.
}
