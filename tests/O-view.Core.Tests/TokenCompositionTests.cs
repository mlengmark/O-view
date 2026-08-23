using System.Globalization;
using OView.Core.Models;
using OView.Core.Storage;

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
        Assert.Equal(MeasuredDay, composition);
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

    /// <summary>The headline must carry its own definition — acceptance criterion for #169.</summary>
    [Fact]
    public void BothTokenLabelsStateThatCacheIsIncluded()
    {
        Assert.Contains("incl. cache", PanelText.TokensTodayLabel, StringComparison.Ordinal);
        Assert.Contains("incl. cache", PanelText.Tokens31DaysLabel, StringComparison.Ordinal);
    }

    /// <summary>All four kinds are named, so the 89% is visible rather than merely asserted.</summary>
    [Fact]
    public void TheCompositionLineNamesAllFourKinds()
    {
        var line = PanelText.TokenCompositionLine(MeasuredDay, PanelText.TokenCompositionTodayScope);

        Assert.StartsWith("Today:", line, StringComparison.Ordinal);
        Assert.Contains("input 14", line, StringComparison.Ordinal);
        Assert.Contains("cache write 44.3K", line, StringComparison.Ordinal);
        Assert.Contains("cache read 398.1K", line, StringComparison.Ordinal);
        Assert.Contains("output 3.7K", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The sentence the issue comes down to: it must name the comparison being warned
    /// against, because the reader it exists for has already made it.
    /// </summary>
    [Fact]
    public void TheHintNamesTheShareAndTheComparisonItWarnsAgainst()
    {
        var hint = PanelText.TokenCompositionHint(MeasuredDay);

        Assert.Contains("89%", hint, StringComparison.Ordinal);
        Assert.Contains("context", hint, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("48.0K", hint, StringComparison.Ordinal);   // excluding cache reads
    }

    /// <summary>
    /// The panel pins its own presentation rather than inheriting the desktop's. Under a
    /// culture that formats percentages as "89 %", an uninvariant "P0" would silently
    /// produce copy this app never wrote.
    /// </summary>
    [Fact]
    public void TheShareIsFormattedInvariantlyWhateverTheMachineCultureIs()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
            Assert.Contains("89%", PanelText.TokenCompositionHint(MeasuredDay), StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
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
