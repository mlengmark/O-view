using OView.Core.Models;
using OView.Core.Pricing;

namespace OView.Core.Tests;

public class CostEstimatorTests
{
    private static TokenSplit Input(long tokens) => TokenSplit.Empty with { Input = tokens };

    private static TokenSplit Output(long tokens) => TokenSplit.Empty with { Output = tokens };

    [Fact]
    public void OpusRates_MatchPublishedPricing()
    {
        // 1M input at $5 + 1M output at $25 = $30.
        var cost = CostEstimator.EstimateUsd(
            "claude-opus-4-8", TokenSplit.Empty with { Input = 1_000_000, Output = 1_000_000 });

        Assert.Equal(30.00m, cost);
    }

    /// <summary>
    /// <b>Both cache-write TTLs, pinned separately</b> (GitHub issue #255).
    ///
    /// <para>The estimator priced every cache write at 1.25× input — the 5-minute rate — while
    /// the transcripts it was reading were almost entirely 1-hour, which Anthropic publishes at
    /// 2×. A fixture of pure-5m writes and one of pure-1h writes are both here so neither rate
    /// can silently become the other: replacing one published price with the other, in either
    /// direction, fails one of these.</para>
    /// </summary>
    [Theory]
    // Opus input $5/MTok. Published: 5m write $6.25, 1h write $10, cache hit $0.50.
    [InlineData(1_000_000, 0, 0, 6.25)]
    [InlineData(0, 1_000_000, 0, 10.00)]
    [InlineData(0, 0, 1_000_000, 6.25)]
    public void CacheWrites_ArePricedAtTheirOwnPublishedRate(
        long write5m, long write1h, long unrecorded, double expected)
    {
        var tokens = TokenSplit.Empty with
        {
            CacheWrite5m = write5m,
            CacheWrite1h = write1h,
            CacheWriteTtlUnrecorded = unrecorded,
        };

        Assert.Equal((decimal)expected, CostEstimator.EstimateUsd("claude-opus-4-8", tokens));
    }

    [Fact]
    public void CacheReads_ArePricedAtTheirOwnPublishedRate()
    {
        Assert.Equal(0.50m, CostEstimator.EstimateUsd(
            "claude-opus-4-8", TokenSplit.Empty with { CacheRead = 1_000_000 }));
    }

    /// <summary>
    /// The migration bucket takes the 5-minute rate, and says so by matching it exactly. Rows
    /// ingested before the TTL split existed carry a write total with no attribution; pricing
    /// them at the cheaper of the two understates rather than overstates, and the panel names
    /// the assumption (see <see cref="PanelTextTests"/>).
    /// </summary>
    [Fact]
    public void TtlUnrecordedWrites_TakeTheFiveMinuteRate_AndTheCheaperOne()
    {
        var unrecorded = CostEstimator.EstimateUsd(
            "claude-opus-4-8", TokenSplit.Empty with { CacheWriteTtlUnrecorded = 1_000_000 });
        var write5m = CostEstimator.EstimateUsd(
            "claude-opus-4-8", TokenSplit.Empty with { CacheWrite5m = 1_000_000 });
        var write1h = CostEstimator.EstimateUsd(
            "claude-opus-4-8", TokenSplit.Empty with { CacheWrite1h = 1_000_000 });

        Assert.Equal(write5m, unrecorded);
        Assert.True(unrecorded < write1h, "the unrecorded bucket must understate, never overstate");
    }

    [Fact]
    public void UnknownModel_ReturnsNull_NeverGuesses()
    {
        Assert.Null(CostEstimator.EstimateUsd("claude-hypothetical-9", Input(1_000_000)));
        Assert.Null(CostEstimator.EstimateUsd("unknown", Input(1_000_000)));
    }

    [Fact]
    public void TierPrefixes_ResolveToDistinctRates()
    {
        var opus = CostEstimator.EstimateUsd("claude-opus-4-8", Input(1_000_000));
        var sonnet = CostEstimator.EstimateUsd("claude-sonnet-5", Input(1_000_000));
        var haiku = CostEstimator.EstimateUsd("claude-haiku-4-5", Input(1_000_000));
        var fable = CostEstimator.EstimateUsd("claude-fable-5", Input(1_000_000));

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
        Assert.Equal(5.00m, CostEstimator.EstimateUsd("claude-opus-5", Input(1_000_000)));
        Assert.Equal(25.00m, CostEstimator.EstimateUsd("claude-opus-5", Output(1_000_000)));
        // A dated snapshot of the same model must resolve via the prefix.
        Assert.Equal(5.00m, CostEstimator.EstimateUsd("claude-opus-5-20260501", Input(1_000_000)));
    }

    [Fact]
    public void Sonnet5_IsPriced_AtPublishedRate()
    {
        // The table recorded $3/$15 — a price increase that had been scheduled for
        // 2026-09-01 when the table was written, and was later cancelled. It was a
        // forecast, not a stale rate, so no freshness check would have caught it; this
        // test pins the published figure so a future edit has to be deliberate
        // (GitHub issue #256). Published rate: $2 in / $10 out per MTok.
        Assert.Equal(2.00m, CostEstimator.EstimateUsd("claude-sonnet-5", Input(1_000_000)));
        Assert.Equal(10.00m, CostEstimator.EstimateUsd("claude-sonnet-5", Output(1_000_000)));
        Assert.Equal(2.00m, CostEstimator.EstimateUsd("claude-sonnet-5-20260501", Input(1_000_000)));
    }

    [Fact]
    public void Sonnet5_DoesNotCollideWithSonnet4Prefixes()
    {
        // The two rows no longer share a rate, so the prefix boundary is now
        // load-bearing for money rather than only for the display name.
        Assert.Equal(3.00m, CostEstimator.EstimateUsd("claude-sonnet-4-6", Input(1_000_000)));
        Assert.Equal(3.00m, CostEstimator.EstimateUsd("claude-sonnet-4-5", Input(1_000_000)));
        Assert.Equal(2.00m, CostEstimator.EstimateUsd("claude-sonnet-5", Input(1_000_000)));
    }

    [Fact]
    public void Opus5_DoesNotCollideWithOpus4Prefixes()
    {
        Assert.Equal(5.00m, CostEstimator.EstimateUsd("claude-opus-4-8", Input(1_000_000)));
        Assert.Equal(5.00m, CostEstimator.EstimateUsd("claude-opus-5", Input(1_000_000)));
    }

    // ── modifiers ───────────────────────────────────────────────────────────────────

    [Fact]
    public void FastMode_UsesItsOwnPublishedRateRow_WhereOneExists()
    {
        var fast = new UsageModifiers(ModifierValue.Applied, ModifierValue.Standard);

        // Published fast-mode pricing for Opus 5 and Opus 4.8: $10 in / $50 out.
        Assert.Equal(10.00m, CostEstimator.EstimateUsd("claude-opus-5", Input(1_000_000), fast));
        Assert.Equal(50.00m, CostEstimator.EstimateUsd("claude-opus-4-8", Output(1_000_000), fast));
    }

    /// <summary>
    /// <b>An unpriceable modifier yields a labelled unknown, never a fallback to standard
    /// rates</b> (GitHub issue #257). Fast mode has no published price on Sonnet, so a request
    /// reporting it must return null and be named in the caveat — silently charging the
    /// standard, cheaper rate is the failure mode the whole rate card exists to prevent.
    /// </summary>
    [Fact]
    public void FastMode_OnAModelWithNoFastRow_IsUnknownRatherThanStandard()
    {
        var fast = new UsageModifiers(ModifierValue.Applied, ModifierValue.Standard);

        Assert.Null(CostEstimator.EstimateUsd("claude-sonnet-5", Input(1_000_000), fast));
        Assert.Null(CostEstimator.EstimateUsd("claude-opus-4-7", Input(1_000_000), fast));

        // And it is specifically not the standard figure, which is what a fallback would give.
        Assert.Equal(2.00m, CostEstimator.EstimateUsd("claude-sonnet-5", Input(1_000_000)));
    }

    [Fact]
    public void AnUnrecognisedModifierValue_IsUnknownRatherThanStandard()
    {
        var odd = UsageModifiers.From("turbo", "not_available");
        var oddGeo = UsageModifiers.From("standard", "eu");

        Assert.Null(CostEstimator.EstimateUsd("claude-opus-5", Input(1_000_000), odd));
        Assert.Null(CostEstimator.EstimateUsd("claude-opus-5", Input(1_000_000), oddGeo));
    }

    [Fact]
    public void UsPinnedInference_AppliesThePublishedMultiplierToEveryCategory()
    {
        var us = new UsageModifiers(ModifierValue.Standard, ModifierValue.Applied);
        var tokens = TokenSplit.Empty with
        {
            Input = 1_000_000,
            CacheWrite1h = 1_000_000,
            CacheRead = 1_000_000,
            Output = 1_000_000,
        };

        var standard = CostEstimator.EstimateUsd("claude-opus-5", tokens)!.Value;

        // $5 + $10 + $0.50 + $25 = $40.50, then ×1.1.
        Assert.Equal(40.50m, standard);
        Assert.Equal(44.55m, CostEstimator.EstimateUsd("claude-opus-5", tokens, us));
    }

    // ── calibration ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The check that found issue #256, run against the sample that found it: Claude Code
    /// reported $56.50 for these Sonnet 5 tokens, and the cancelled-forecast $3/$15 row priced
    /// them at $84.71 — 50% high. At the published $2/$10 the same comparison lands within a
    /// rounding error of the reported figure.
    ///
    /// <para>The cache writes are attributed to the 5-minute bucket because that is the
    /// attribution under which the reported total reconciles — the issue solved for a single
    /// input rate assuming a 1.25× write, and got $2.001 against a published $2. Attributing
    /// the same writes to the 1-hour bucket puts this 9% out, which is the point of the method
    /// taking a whole rate row rather than solving for one column: a wrong TTL mix and a wrong
    /// rate are both visible here, and solving hides the first.</para>
    /// </summary>
    [Fact]
    public void RelativeError_MeasuresTheGapAgainstAReportedFigure()
    {
        var tokens = new TokenSplit(449_800, 3_400_000, 0, 0, 190_600_000, 895_200);
        var published = ModelCatalog.Find("claude-sonnet-5")!.Rates;
        var forecast = published with
        {
            InputPerMTok = 3.00m,
            OutputPerMTok = 15.00m,
            CacheWrite5mPerMTok = 3.75m,
            CacheWrite1hPerMTok = 6.00m,
            CacheReadPerMTok = 0.30m,
        };

        Assert.InRange(CostEstimator.RelativeError(published, tokens, 56.50m), -0.02m, 0.02m);
        Assert.InRange(CostEstimator.RelativeError(forecast, tokens, 56.50m), 0.45m, 0.55m);
    }

    [Fact]
    public void RelativeError_IsZero_WhenThereIsNothingToCompareAgainst()
    {
        var rates = ModelCatalog.Find("claude-opus-5")!.Rates;

        Assert.Equal(0m, CostEstimator.RelativeError(rates, Input(1_000_000), 0m));
    }

    // The <synthetic> case that used to live here has moved to JsonlIngestionTests, where
    // the behaviour actually is: TranscriptReader drops those records at parse time, so
    // they never reach the store and never reach this class. The branch tested here was
    // unreachable in production and disagreed with the reader on case sensitivity
    // (GitHub issue #57).
}
