using OView.Core.Models;
using OView.Core.Pricing;
using OView.Core.Providers.Jsonl;
using OView.Core.Storage;

namespace OView.Core.Tests;

/// <summary>
/// The typed token counts and Est. figures <see cref="PanelStatistics"/>, <see cref="ModelSlice"/>
/// and <see cref="TokenComposition"/> now carry beside their long-standing plain ones. The plain
/// members are pinned elsewhere (<c>OView.Presentation.Tests.PanelStatisticsTests</c>) and are
/// unchanged by this file, these cases are only about the <see cref="UsageValueStatus"/> each
/// typed figure carries.
/// </summary>
public class PanelStatisticsTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private readonly string _dir = Directory.CreateTempSubdirectory("oview-core-tests-").FullName;
    private readonly RollupStore _store;

    public PanelStatisticsTests()
    {
        _store = new RollupStore(Path.Combine(_dir, "usage.db"));
    }

    public void Dispose()
    {
        _store.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private void Seed(string id, string date, long output, string model = "claude-opus-4-8") =>
        _store.Ingest([new TranscriptRecord(
            id, DateTimeOffset.Parse(date + "T10:00:00Z"), model,
            new TokenSplit(0, 0, 0, 0, 0, output), UsageModifiers.Standard)]);

    private void SeedSplit(string id, string date, TokenSplit tokens, string model = "claude-opus-4-8") =>
        _store.Ingest([new TranscriptRecord(
            id, DateTimeOffset.Parse(date + "T10:00:00Z"), model, tokens, UsageModifiers.Standard)]);

    [Fact]
    public void FullyPricedWindow_CountsAreMeasured_EstimatesAreEstimated()
    {
        Seed("r1", "2026-07-21", 1_000_000);

        var stats = PanelStatistics.Build(_store, Now, Utc);

        Assert.Equal(UsageValueStatus.Real, stats.TokensTodayCount.Status);
        Assert.Equal(stats.TokensToday, stats.TokensTodayCount.Value);
        Assert.Equal(UsageValueStatus.Real, stats.Tokens31DaysCount.Status);
        Assert.Equal(stats.Tokens31Days, stats.Tokens31DaysCount.Value);

        Assert.Equal(UsageValueStatus.Estimated, stats.EstTodayValue.Status);
        Assert.Equal(stats.EstTodayValue.Value, stats.EstTodayValue.Value);
        Assert.Equal(UsageValueStatus.Estimated, stats.Est31DaysValue.Status);
        Assert.Equal(stats.Est31DaysValue.Value, stats.Est31DaysValue.Value);
    }

    /// <summary>
    /// The case the slice exists for: a window with an unpriced model still says the tokens
    /// were measured, even though the window's Est. figure can only say what the priced
    /// portion was worth and names the rest as unpriced.
    /// </summary>
    [Fact]
    public void UnpricedModel_LeavesTheCountMeasured_ButNotTheEstimate()
    {
        Seed("r1", "2026-07-21", 1_000_000);
        Seed("r2", "2026-07-21", 1_000_000, model: "claude-hypothetical-9");

        var stats = PanelStatistics.Build(_store, Now, Utc);

        Assert.Equal(UsageValueStatus.Real, stats.TokensTodayCount.Status);
        Assert.Equal(2_000_000, stats.TokensTodayCount.Value);
        // The priced half is still reported, as a real Estimated figure, not unavailable.
        Assert.Equal(UsageValueStatus.Estimated, stats.EstTodayValue.Status);
        Assert.Equal(25.00m, stats.EstTodayValue.Value);
    }

    /// <summary>When nothing at all can be priced, the Est. figure says so, absent, not zero.</summary>
    [Fact]
    public void NothingPriceable_MakesTheEstimateUnavailable_NotTheCount()
    {
        Seed("a", "2026-07-21", 1_000_000, model: "claude-brand-new-9");

        var stats = PanelStatistics.Build(_store, Now, Utc);

        Assert.Equal(UsageValueStatus.Unavailable, stats.Est31DaysValue.Status);
        Assert.Null(stats.Est31DaysValue.Value);
        Assert.Equal(UsageValueStatus.Real, stats.Tokens31DaysCount.Status);
        Assert.Equal(1_000_000, stats.Tokens31DaysCount.Value);
    }

    [Fact]
    public void CreditBilledFigures_AreTypedTheSameWay()
    {
        Seed("r1", "2026-07-21", 1_000_000, "claude-opus-4-8");  // plan model, excluded
        Seed("r2", "2026-07-21", 1_000_000, "claude-fable-5");   // credit-billed, counted

        var stats = PanelStatistics.Build(_store, Now, Utc);

        Assert.Equal(UsageValueStatus.Real, stats.CreditTokens31DaysCount.Status);
        Assert.Equal(1_000_000, stats.CreditTokens31DaysCount.Value);
        Assert.Equal(UsageValueStatus.Estimated, stats.EstCredit31DaysValue.Status);
        Assert.Equal(stats.EstCredit31DaysValue.Value, stats.EstCredit31DaysValue.Value);
    }

    [Fact]
    public void TtlUnrecordedCacheWrites_IsTyped()
    {
        _store.Ingest([new TranscriptRecord(
            "legacy", DateTimeOffset.Parse("2026-07-21T11:00:00Z"), "claude-opus-5",
            new TokenSplit(0, 0, 0, 4_000, 0, 10), UsageModifiers.Standard)]);

        var stats = PanelStatistics.Build(_store, Now, Utc);

        Assert.Equal(UsageValueStatus.Real, stats.TtlUnrecordedCacheWritesCount.Status);
        Assert.Equal(4_000, stats.TtlUnrecordedCacheWritesCount.Value);
    }

    [Fact]
    public void PerModelSlices_CarryTheirOwnTypedFigures()
    {
        Seed("priced", "2026-07-21", 1_000_000, "claude-opus-4-8");
        Seed("unpriced", "2026-07-21", 500_000, "claude-hypothetical-9");

        var stats = PanelStatistics.Build(_store, Now, Utc);

        var priced = stats.Models31Days.Single(m => m.Model == "claude-opus-4-8");
        Assert.Equal(UsageValueStatus.Real, priced.TokensCount.Status);
        Assert.Equal(1_000_000, priced.TokensCount.Value);
        Assert.Equal(UsageValueStatus.Estimated, priced.EstUsdValue.Status);
        Assert.Equal(priced.EstUsdValue.Value, priced.EstUsdValue.Value);

        var unpriced = stats.Models31Days.Single(m => m.Model == "claude-hypothetical-9");
        Assert.Equal(UsageValueStatus.Real, unpriced.TokensCount.Status);
        Assert.Equal(500_000, unpriced.TokensCount.Value);
        Assert.Equal(UsageValueStatus.Unavailable, unpriced.EstUsdValue.Status);
        Assert.Null(unpriced.EstUsdValue.Value);
    }

    // ── the Output and Overall figures ──────────────────────────────────────────────

    [Fact]
    public void OutputEstimate_PricesOnlyOutput_WhereTheEstValueStillPricesEverything()
    {
        // Opus: $5/MTok in, $25/MTok out. 1M of each today, 1M of each again yesterday.
        var both = new TokenSplit(1_000_000, 0, 0, 0, 0, 1_000_000);
        SeedSplit("today", "2026-07-21", both);
        SeedSplit("yesterday", "2026-07-20", both);

        var stats = PanelStatistics.Build(_store, Now, Utc);

        Assert.Equal(30.00m, stats.EstTodayValue.Value);
        Assert.Equal(60.00m, stats.Est31DaysValue.Value);
        Assert.Equal(UsageValueStatus.Estimated, stats.EstTodayOutputValue.Status);
        Assert.Equal(25.00m, stats.EstTodayOutputValue.Value);
        Assert.Equal(UsageValueStatus.Estimated, stats.Est31DaysOutputValue.Status);
        Assert.Equal(50.00m, stats.Est31DaysOutputValue.Value);
    }

    [Fact]
    public void OutputEstimate_OfAPricedModelWithNoOutput_IsAKnownZero()
    {
        SeedSplit("reads", "2026-07-21", new TokenSplit(0, 0, 0, 0, 5_000_000, 0));

        var stats = PanelStatistics.Build(_store, Now, Utc);

        Assert.Equal(UsageValueStatus.Estimated, stats.EstTodayOutputValue.Status);
        Assert.Equal(0m, stats.EstTodayOutputValue.Value);
    }

    [Fact]
    public void OutputEstimate_WithNothingPriceable_IsUnavailable_NeverZero()
    {
        Seed("a", "2026-07-21", 1_000_000, model: "claude-brand-new-9");

        var stats = PanelStatistics.Build(_store, Now, Utc);

        Assert.Equal(UsageValueStatus.Unavailable, stats.EstTodayOutputValue.Status);
        Assert.Null(stats.EstTodayOutputValue.Value);
        Assert.Equal(UsageValueStatus.Unavailable, stats.Est31DaysOutputValue.Status);
        Assert.Null(stats.Est31DaysOutputValue.Value);
    }

    [Fact]
    public void OutputEstimate_WithAnUnpricedModelBesideAPricedOne_IsThePricedSubtotal()
    {
        Seed("r1", "2026-07-21", 1_000_000);
        Seed("r2", "2026-07-21", 1_000_000, model: "claude-hypothetical-9");

        var stats = PanelStatistics.Build(_store, Now, Utc);

        // The same rule as the full estimate: the priced half is reported, and the unpriced
        // model is named in UnpricedModels rather than counted as zero.
        Assert.Equal(UsageValueStatus.Estimated, stats.EstTodayOutputValue.Status);
        Assert.Equal(25.00m, stats.EstTodayOutputValue.Value);
        Assert.Equal(["claude-hypothetical-9"], stats.UnpricedModels);
    }

    [Fact]
    public void ModelSlices_CarryAllTokensAndAnOutputOnlyEstimate()
    {
        SeedSplit("priced", "2026-07-21", new TokenSplit(2_000_000, 0, 0, 0, 3_000_000, 1_000_000));
        SeedSplit("unpriced", "2026-07-21", new TokenSplit(0, 0, 0, 0, 500_000, 500_000), "claude-hypothetical-9");

        var stats = PanelStatistics.Build(_store, Now, Utc);

        var priced = stats.Models31Days.Single(m => m.Model == "claude-opus-4-8");
        Assert.Equal(1_000_000, priced.TokensCount.Value);
        Assert.Equal(6_000_000, priced.AllTokensCount.Value);
        Assert.Equal(UsageValueStatus.Real, priced.AllTokensCount.Status);
        Assert.Equal(UsageValueStatus.Estimated, priced.EstOutputUsdValue.Status);
        Assert.Equal(25.00m, priced.EstOutputUsdValue.Value);
        // Today's figures are unchanged: the full estimate still prices all of it.
        // $10 input + $1.50 cache read + $25 output.
        Assert.Equal(36.50m, priced.EstUsdValue.Value);

        var unpriced = stats.Models31Days.Single(m => m.Model == "claude-hypothetical-9");
        Assert.Equal(1_000_000, unpriced.AllTokensCount.Value);
        Assert.Equal(UsageValueStatus.Real, unpriced.AllTokensCount.Status);
        Assert.Equal(UsageValueStatus.Unavailable, unpriced.EstOutputUsdValue.Status);
        Assert.Null(unpriced.EstOutputUsdValue.Value);
    }

    [Fact]
    public void TodaysModelSlices_CarryTheNewFiguresForTodayOnly()
    {
        var split = new TokenSplit(1_000_000, 0, 0, 0, 0, 1_000_000);
        SeedSplit("today", "2026-07-21", split);
        SeedSplit("earlier", "2026-07-10", split);

        var stats = PanelStatistics.Build(_store, Now, Utc);

        var today = stats.ModelsToday.Single();
        Assert.Equal(2_000_000, today.AllTokensCount.Value);
        Assert.Equal(25.00m, today.EstOutputUsdValue.Value);
        Assert.Equal(4_000_000, stats.Models31Days.Single().AllTokensCount.Value);
    }

    [Fact]
    public void ADaysBilledTokens_AreEveryKindBilledOnIt_BesideItsOutput()
    {
        SeedSplit("a", "2026-07-21", new TokenSplit(100, 200, 300, 0, 400, 50));
        SeedSplit("b", "2026-07-21", new TokenSplit(0, 0, 0, 0, 1_000, 10), "claude-sonnet-5");
        SeedSplit("c", "2026-07-20", new TokenSplit(0, 0, 0, 0, 0, 7));

        var series = PanelStatistics.Build(_store, Now, Utc).DailySeries;

        var today = series[^1];
        Assert.Equal(new DateOnly(2026, 7, 21), today.Date);
        Assert.Equal(60, today.OutputTokens);
        Assert.Equal(1_000 + 10 + 100 + 200 + 300 + 400 + 50, today.BilledTokens);

        var yesterday = series[^2];
        Assert.Equal(7, yesterday.OutputTokens);
        Assert.Equal(7, yesterday.BilledTokens);
    }

    [Fact]
    public void ADayWithNoUsage_HasNoBilledTokens_AndPreInstallDaysStayFlagged()
    {
        Seed("a", "2026-07-21", 100);

        var series = PanelStatistics.Build(_store, Now, Utc).DailySeries;

        Assert.All(series.Where(d => d.PreInstall), d => Assert.Equal(0, d.BilledTokens));
        Assert.Contains(series, d => d.PreInstall);
        Assert.Equal(100, series[^1].BilledTokens);
    }

    [Fact]
    public void ASeriesBuiltWithoutBilledTokens_StillCompiles_AndReadsAsNone()
    {
        var day = new DayUsage(new DateOnly(2026, 7, 21), 42, PreInstall: false);

        Assert.Equal(0, day.BilledTokens);
        Assert.Equal(42, day.OutputTokens);
    }

    [Fact]
    public void TheViewEnums_StartOnOutputAndTheTotalsFace()
    {
        // A fresh install opens on Output, totals: default(T) is what an unset setting reads as.
        Assert.Equal(PanelMeasure.Output, default(PanelMeasure));
        Assert.Equal(PanelFace.Totals, default(PanelFace));
    }

    [Fact]
    public void TokenComposition_CountsAreMeasured_ValuesFollowWhatCouldBePriced()
    {
        Seed("priced", "2026-07-21", 1_000_000, "claude-opus-4-8");

        var stats = PanelStatistics.Build(_store, Now, Utc);
        var composition = stats.CompositionToday;

        Assert.Equal(UsageValueStatus.Real, composition.OutputCount.Status);
        Assert.Equal(composition.Output, composition.OutputCount.Value);
        Assert.Equal(UsageValueStatus.Estimated, composition.OutputValue.Status);
        Assert.Equal(composition.OutputValue.Value, composition.OutputValue.Value);
    }

    [Fact]
    public void TokenComposition_NothingPriceable_ValuesAreUnavailable_CountsStayMeasured()
    {
        Seed("unpriced", "2026-07-21", 1_000_000, "claude-brand-new-9");

        var stats = PanelStatistics.Build(_store, Now, Utc);
        var composition = stats.CompositionToday;

        Assert.Equal(UsageValueStatus.Real, composition.OutputCount.Status);
        Assert.Equal(1_000_000, composition.OutputCount.Value);
        Assert.Equal(UsageValueStatus.Unavailable, composition.OutputValue.Status);
        Assert.Null(composition.OutputValue.Value);
    }

    [Fact]
    public void Empty_CarriesMeasuredZeroCounts_AndUnavailableValues()
    {
        var empty = TokenComposition.Empty;

        Assert.Equal(UsageValueStatus.Real, empty.OutputCount.Status);
        Assert.Equal(0, empty.OutputCount.Value);
        Assert.Equal(UsageValueStatus.Unavailable, empty.OutputValue.Status);
        Assert.Null(empty.OutputValue.Value);
    }

    /// <summary>
    /// <see cref="PanelStatistics.WithDivergence"/> only prices the off-plan figure when the
    /// window actually is off-plan; either way the typed figure follows the same null/non-null
    /// split as the plain one it sits beside.
    /// </summary>
    [Fact]
    public void OffPlanEstimate_IsEstimatedWhenPriced_UnavailableOtherwise()
    {
        Seed("r1", "2026-07-21", 69_091, "claude-fable-5");
        var stats = PanelStatistics.Build(_store, Now, Utc);
        var windowStart = Now.AddHours(-5);

        // A flat meter under heavy load: DivergenceDetector reports off-plan.
        var diverging = stats.WithDivergence(_store, windowStart, [6, 6, 6, 6, 6, 6], TimeSpan.Zero);
        Assert.True(diverging.IsOffPlan);
        Assert.Equal(UsageValueStatus.Estimated, diverging.EstOffPlanValue.Status);
        Assert.Equal(diverging.EstOffPlanValue.Value, diverging.EstOffPlanValue.Value);
        Assert.NotNull(diverging.EstOffPlanValue.Value);

        // A meter tracking the work: not off-plan, so the figure is deliberately not priced.
        var consistent = stats.WithDivergence(_store, windowStart, [1, 4, 8, 12, 16], TimeSpan.Zero);
        Assert.False(consistent.IsOffPlan);
        Assert.Equal(UsageValueStatus.Unavailable, consistent.EstOffPlanValue.Status);
        Assert.Null(consistent.EstOffPlanValue.Value);
    }
}
