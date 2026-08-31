using OView.Core.Pricing;

namespace OView.Core.Tests;

public class CostEstimatorTests
{
    [Fact]
    public void OpusRates_MatchPublishedPricing()
    {
        // 1M input at $5 + 1M output at $25 = $30.
        var cost = CostEstimator.EstimateUsd("claude-opus-4-8", 1_000_000, 0, 0, 1_000_000);

        Assert.Equal(30.00m, cost);
    }

    [Fact]
    public void CacheMultipliers_Apply()
    {
        // Opus input $5/MTok: writes ×1.25 → $6.25, reads ×0.1 → $0.50.
        Assert.Equal(6.25m, CostEstimator.EstimateUsd("claude-opus-4-8", 0, 1_000_000, 0, 0));
        Assert.Equal(0.50m, CostEstimator.EstimateUsd("claude-opus-4-8", 0, 0, 1_000_000, 0));
    }

    [Fact]
    public void UnknownModel_ReturnsNull_NeverGuesses()
    {
        Assert.Null(CostEstimator.EstimateUsd("claude-hypothetical-9", 1_000_000, 0, 0, 0));
        Assert.Null(CostEstimator.EstimateUsd("unknown", 1_000_000, 0, 0, 0));
    }

    [Fact]
    public void TierPrefixes_ResolveToDistinctRates()
    {
        var opus = CostEstimator.EstimateUsd("claude-opus-4-8", 1_000_000, 0, 0, 0);
        var sonnet = CostEstimator.EstimateUsd("claude-sonnet-5", 1_000_000, 0, 0, 0);
        var haiku = CostEstimator.EstimateUsd("claude-haiku-4-5", 1_000_000, 0, 0, 0);
        var fable = CostEstimator.EstimateUsd("claude-fable-5", 1_000_000, 0, 0, 0);

        Assert.Equal(5.00m, opus);
        Assert.Equal(2.00m, sonnet);
        Assert.Equal(1.00m, haiku);
        Assert.Equal(10.00m, fable);
    }

    [Fact]
    public void Opus5_IsPriced_AtPublishedRate()
    {
        // claude-opus-5 appeared in real transcripts but had no entry, so every "Est.
        // value" tile that included it blanked. Published rate: $5 in / $25 out per MTok.
        Assert.Equal(5.00m, CostEstimator.EstimateUsd("claude-opus-5", 1_000_000, 0, 0, 0));
        Assert.Equal(25.00m, CostEstimator.EstimateUsd("claude-opus-5", 0, 0, 0, 1_000_000));
        // A dated snapshot of the same model must resolve via the prefix.
        Assert.Equal(5.00m, CostEstimator.EstimateUsd("claude-opus-5-20260501", 1_000_000, 0, 0, 0));
    }

    [Fact]
    public void Sonnet5_IsPriced_AtPublishedRate()
    {
        // The table recorded $3/$15 — a price increase that had been scheduled for
        // 2026-09-01 when the table was written, and was later cancelled. It was a
        // forecast, not a stale rate, so no freshness check would have caught it; this
        // test pins the published figure so a future edit has to be deliberate
        // (GitHub issue #256). Published rate: $2 in / $10 out per MTok.
        Assert.Equal(2.00m, CostEstimator.EstimateUsd("claude-sonnet-5", 1_000_000, 0, 0, 0));
        Assert.Equal(10.00m, CostEstimator.EstimateUsd("claude-sonnet-5", 0, 0, 0, 1_000_000));
        Assert.Equal(2.00m, CostEstimator.EstimateUsd("claude-sonnet-5-20260501", 1_000_000, 0, 0, 0));
    }

    [Fact]
    public void Sonnet5_DoesNotCollideWithSonnet4Prefixes()
    {
        // The two rows no longer share a rate, so the prefix boundary is now
        // load-bearing for money rather than only for the display name.
        Assert.Equal(3.00m, CostEstimator.EstimateUsd("claude-sonnet-4-6", 1_000_000, 0, 0, 0));
        Assert.Equal(3.00m, CostEstimator.EstimateUsd("claude-sonnet-4-5", 1_000_000, 0, 0, 0));
        Assert.Equal(2.00m, CostEstimator.EstimateUsd("claude-sonnet-5", 1_000_000, 0, 0, 0));
    }

    [Fact]
    public void Opus5_DoesNotCollideWithOpus4Prefixes()
    {
        Assert.Equal(5.00m, CostEstimator.EstimateUsd("claude-opus-4-8", 1_000_000, 0, 0, 0));
        Assert.Equal(5.00m, CostEstimator.EstimateUsd("claude-opus-5", 1_000_000, 0, 0, 0));
    }

    // The <synthetic> case that used to live here has moved to JsonlIngestionTests, where
    // the behaviour actually is: TranscriptReader drops those records at parse time, so
    // they never reach the store and never reach this class. The branch tested here was
    // unreachable in production and disagreed with the reader on case sensitivity
    // (GitHub issue #57).
}
