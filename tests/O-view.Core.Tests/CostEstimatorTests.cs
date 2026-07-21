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
        Assert.Equal(3.00m, sonnet);
        Assert.Equal(1.00m, haiku);
        Assert.Equal(10.00m, fable);
    }
}
