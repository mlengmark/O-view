using System.Globalization;
using OView.Core.Models;
using OView.Core.Storage;
using OView.Core.Pricing;

namespace OView.Core.Tests;

/// <summary>
/// A user reported the token tiles as inflated: <c>235.6M</c> where Claude's own UI showed
/// thousands (GitHub issue #169). The sum is right — ingestion de-duplicates on request id
/// and upserts by replacement — but roughly nine tenths of it is cached prompt re-reads, and
/// an unqualified "Tokens today" invited a comparison against a figure measuring something
/// else entirely.
///
/// <para>These pin the composition and the copy that explains it. The ratio is the load-
/// bearing fact, so it is measured here rather than left in an issue comment.</para>
/// </summary>
public class TokenCompositionTests
{
    /// <summary>
    /// One real UTC day from the dev machine, de-duplicated by request id: 7 distinct
    /// requests, 446,148 tokens, of which 398,121 were cache reads.
    /// </summary>
    private static readonly TokenComposition MeasuredDay = new(
        Input: 14, CacheCreation: 44_347, CacheRead: 398_121, Output: 3_666);

    private static DailyRollup Rollup(string model, long input, long cacheW, long cacheR, long output) =>
        new(new DateOnly(2026, 8, 23), model, input, cacheW, cacheR, output, RequestCount: 1);

    // ── the measurement ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The fact the whole issue turns on. 7 requests produced 446K tokens, ~89% of them
    /// cache reads — so a few thousand requests reaches hundreds of millions while the
    /// context-window figure a user compares against never leaves the thousands.
    /// </summary>
    [Fact]
    public void CacheReadsDominateARealDay()
    {
        Assert.Equal(446_148, MeasuredDay.Total);
        Assert.Equal(48_027, MeasuredDay.ExcludingCacheReads);

        // ~89%. Pinned as a band, not a point: the exact share moves with conversation
        // length, and a test asserting 0.892 exactly would be pinning this one sample
        // rather than the property that makes the tiles read in millions.
        Assert.InRange(MeasuredDay.CacheReadShare, 0.85, 0.95);
    }

    /// <summary>
    /// The composition explains the total; it must never compute a different one. If these
    /// two could drift, the panel would show a breakdown contradicting the figure above it.
    /// </summary>
    [Fact]
    public void TheCompositionSumsToTheSameTotalTheTilesShow()
    {
        var rollups = new[]
        {
            Rollup("claude-opus-5", 10, 30_000, 300_000, 2_000),
            Rollup("claude-sonnet-5", 4, 14_347, 98_121, 1_666),
        };

        var composition = TokenComposition.From(rollups);

        Assert.Equal(rollups.Sum(r => r.TotalTokens), composition.Total);

        // The token split, compared field by field rather than as a whole record: the record
        // now also carries per-kind Est. values, and asserting on those here would make this
        // case fail whenever a published rate changes — which is a different test's job.
        Assert.Equal(MeasuredDay.Input, composition.Input);
        Assert.Equal(MeasuredDay.CacheCreation, composition.CacheCreation);
        Assert.Equal(MeasuredDay.CacheRead, composition.CacheRead);
        Assert.Equal(MeasuredDay.Output, composition.Output);
    }

    /// <summary>
    /// <b>The four cards must add up to the Est. tile above them.</b> They are separate
    /// figures on screen at the same time, and a card that does not reconcile with its tile is
    /// exactly the "is this number right?" failure issue #169 was reported as.
    ///
    /// <para>Guaranteed structurally — both sides go through <see cref="CostEstimator"/> over
    /// the same rollups — and pinned here because the guarantee is one refactor away from
    /// becoming two pricing paths.</para>
    /// </summary>
    [Fact]
    public void ThePerKindValuesSumToTheEstimateTheTileShows()
    {
        var rollups = new[]
        {
            Rollup("claude-opus-5", 10, 30_000, 300_000, 2_000),
            Rollup("claude-sonnet-5", 4, 14_347, 98_121, 1_666),
        };

        var composition = TokenComposition.From(rollups);
        var tile = rollups.Sum(r => CostEstimator.EstimateUsd(
            r.Model, r.InputTokens, r.CacheCreationTokens, r.CacheReadTokens, r.OutputTokens) ?? 0);

        Assert.Equal(
            tile,
            composition.InputUsd + composition.CacheCreationUsd
                + composition.CacheReadUsd + composition.OutputUsd);
    }

    /// <summary>
    /// A model with no published rate yields unknown per kind, never a zero that would read
    /// as "this cost nothing" — the same rule the Est. tiles follow (rule 6).
    /// </summary>
    [Fact]
    public void AnUnpricedModelLeavesEveryKindUnknownRatherThanZero()
    {
        var composition = TokenComposition.From([Rollup("claude-not-a-model", 10, 20, 30, 40)]);

        Assert.Equal(100, composition.Total);
        Assert.Null(composition.OutputUsd);
        Assert.Null(composition.CacheReadUsd);
        Assert.All(composition.InDisplayOrder, s => Assert.Null(s.EstUsd));
    }

    [Fact]
    public void AnEmptyCompositionHasNothingToExplain()
    {
        Assert.False(TokenComposition.Empty.HasTokens);
        Assert.Equal(0, TokenComposition.Empty.Total);

        // Not NaN: the share of nothing is zero, and a division by zero here would reach
        // the UI as "NaN%" rather than as an error anyone would notice.
        Assert.Equal(0, TokenComposition.Empty.CacheReadShare);
    }

    [Fact]
    public void FromRollupsIsEmptyWhenThereAreNoRollups()
    {
        Assert.Equal(TokenComposition.Empty, TokenComposition.From([]));
    }

    // ── the copy ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The headline names its metric — the acceptance criterion #169 set, met by issue #253's
    /// different answer. It must NOT read <c>incl. cache</c> any more: the qualifier was true
    /// of a total these tiles no longer show, and a stale qualifier is the same rule-6 failure
    /// as a wrong figure (the lesson of issue #210's "(UTC)").
    /// </summary>
    [Fact]
    public void BothTokenLabelsNameOutputAndDropTheCacheQualifier()
    {
        Assert.Contains("Output tokens", PanelText.TokensTodayLabel, StringComparison.Ordinal);
        Assert.Contains("Output tokens", PanelText.Tokens31DaysLabel, StringComparison.Ordinal);

        Assert.DoesNotContain("incl. cache", PanelText.TokensTodayLabel, StringComparison.Ordinal);
        Assert.DoesNotContain("incl. cache", PanelText.Tokens31DaysLabel, StringComparison.Ordinal);
    }

    /// <summary>
    /// The bars are labelled differently from the tiles, on purpose. A bar totalling 446K
    /// under a tile reading "3.7K" is a contradiction unless something says the two count
    /// different things.
    /// </summary>
    [Fact]
    public void TheBarHeadingsDoNotReuseTheTileWording()
    {
        Assert.Contains("Tokens used", PanelText.TokensUsedTodayLabel, StringComparison.Ordinal);
        Assert.Contains("Tokens used", PanelText.TokensUsed31DaysLabel, StringComparison.Ordinal);

        Assert.NotEqual(PanelText.TokensTodayLabel, PanelText.TokensUsedTodayLabel);
        Assert.NotEqual(PanelText.TokensUsedTodayLabel, PanelText.TokensUsed31DaysLabel);
    }

    /// <summary>
    /// All four kinds are drawn, so the 89% is visible rather than merely asserted — and
    /// <b>output leads</b>, which is the whole of the ordering decision: at 1.1% of the track
    /// it is legible at the origin and a sliver anywhere else.
    /// </summary>
    [Fact]
    public void TheDisplayOrderLeadsWithOutputAndNamesAllFourKinds()
    {
        var order = MeasuredDay.InDisplayOrder;

        Assert.Equal(
            [TokenKind.Output, TokenKind.Input, TokenKind.CacheWrite, TokenKind.CacheRead],
            order.Select(s => s.Kind));

        Assert.Equal(
            ["output", "input", "cache write", "cache read"],
            order.Select(s => PanelText.TokenKindLabel(s.Kind)));

        Assert.Equal([3_666L, 14L, 44_347L, 398_121L], order.Select(s => s.Tokens));
    }

    /// <summary>
    /// Input runs at 0.003% of a real day. Rounded to two decimals that is "0.00%", which
    /// says it did not happen; the threshold says it is present and small.
    /// </summary>
    [Fact]
    public void AShareTooSmallToRoundIsFlooredRatherThanShownAsZero()
    {
        var input = MeasuredDay.InDisplayOrder.Single(s => s.Kind == TokenKind.Input);

        Assert.InRange(input.Share, 0, 0.0001);
        Assert.Equal("<0.01%", PanelText.TokenShare(input.Share));

        // A genuine zero still reads as zero — the threshold is for "small", not for "absent".
        Assert.Equal("0%", PanelText.TokenShare(0));
        Assert.Equal("100%", PanelText.TokenShare(1));
        Assert.Equal("89.24%", PanelText.TokenShare(MeasuredDay.CacheReadShare));
    }

    /// <summary>
    /// The panel pins its own presentation rather than inheriting the desktop's. Under a
    /// culture that formats percentages as "89 %", an uninvariant "P2" would silently
    /// produce copy this app never wrote.
    /// </summary>
    [Fact]
    public void TheShareIsFormattedInvariantlyWhateverTheMachineCultureIs()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
            Assert.Equal("89.24%", PanelText.TokenShare(MeasuredDay.CacheReadShare));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    /// <summary>
    /// The card names its window. Two bars with measurably different shapes sit one above the
    /// other, so a share with no window attached can be read against the wrong one.
    /// </summary>
    [Fact]
    public void TheCardCaptionNamesTheKindTheWindowAndKeepsTheEstPrefix()
    {
        var caption = PanelText.TokenCardCaption(
            TokenKind.CacheWrite, 0.10295, 0.87m, PanelText.TokenWindowToday);

        Assert.Equal("cache write · 10.30% of today · Est. $0.87", caption);
    }

    /// <summary>
    /// An unpriced window says so rather than showing <c>$0.00</c>, which would read as
    /// "this cost nothing" (rule 6). The same rule the Est. tiles follow.
    /// </summary>
    [Fact]
    public void AnUnpricedKindSaysUnknownRatherThanZero()
    {
        var caption = PanelText.TokenCardCaption(
            TokenKind.Output, 0.5, null, PanelText.TokenWindow31Days);

        Assert.Contains("value unknown", caption, StringComparison.Ordinal);
        Assert.DoesNotContain("$0.00", caption, StringComparison.Ordinal);
    }

    // ── the sum is not changed ──────────────────────────────────────────────────────

    /// <summary>
    /// Cache reads stay in the total. They are billed and priced, so dropping them would
    /// understate consumption and break the Est. value tiles — the fix for #169 is the
    /// label, never the arithmetic. This pins that nobody "fixes" it the other way later.
    /// </summary>
    [Fact]
    public void TheTotalStillIncludesCacheReads()
    {
        var rollup = Rollup("claude-opus-5", 14, 44_347, 398_121, 3_666);

        Assert.Equal(446_148, rollup.TotalTokens);
        Assert.Equal(rollup.TotalTokens, TokenComposition.From([rollup]).Total);
        Assert.NotEqual(rollup.TotalTokens, TokenComposition.From([rollup]).ExcludingCacheReads);
    }
}
