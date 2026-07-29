using OView.Core.Models;

namespace OView.Core.Tests;

/// <summary>
/// The per-model tile breakdown (GitHub issue #37).
///
/// Two constraints are expressed as code here and both are load-bearing:
///   • colour follows the MODEL, from one panel-wide slot order — never the segment's
///     rank within a tile, which made the same model change colour between tiles;
///   • the slot count tiers at three, because no validated neutral separates from the
///     third chromatic slot for deuteranopes.
/// </summary>
public class ModelBreakdownTests
{
    private static ModelSlice Slice(string model, long tokens, decimal? usd) =>
        new(model, ModelDisplayName.For(model), tokens, usd);

    private static readonly ModelSlice[] Window =
    [
        Slice("claude-opus-4-8", 257_000_000, 188.50m),
        Slice("claude-opus-5", 148_900_000, 92.75m),
        Slice("claude-fable-5", 40_000_000, 80.94m),
        Slice("claude-haiku-4-5", 5_000_000, 1.20m),
    ];

    // ── colour order ──────────────────────────────────────────────────────────

    [Fact]
    public void ColourOrder_RanksByTokens_AndKeepsTwoSlotsWhenARemainderIsNeeded()
    {
        var order = ModelBreakdown.ColourOrder(Window);

        // Four models, so the third chromatic slot is given up for the neutral.
        Assert.Equal(["claude-opus-4-8", "claude-opus-5"], order);
    }

    [Fact]
    public void ColourOrder_ThreeModels_KeepAllThreeSlots()
    {
        var order = ModelBreakdown.ColourOrder(Window.Take(3).ToList());

        Assert.Equal(["claude-opus-4-8", "claude-opus-5", "claude-fable-5"], order);
    }

    [Fact]
    public void SlotFor_IsCaseInsensitive_AndMinusOneOutsideTheOrder()
    {
        var order = ModelBreakdown.ColourOrder(Window);

        Assert.Equal(0, ModelBreakdown.SlotFor("CLAUDE-OPUS-4-8", order));
        Assert.Equal(1, ModelBreakdown.SlotFor("claude-opus-5", order));
        Assert.Equal(-1, ModelBreakdown.SlotFor("claude-fable-5", order));
    }

    /// <summary>
    /// The regression this order exists to prevent. Colouring by rank within each tile
    /// made Opus 5 blue on the "today" tile (where it was the only model, hence first)
    /// and orange on the 31-day tile (where Opus 4.8 outranked it) — the same model
    /// wearing two colours on one panel.
    /// </summary>
    [Fact]
    public void Slot_IsTheSameModel_AcrossTilesAndMeasures()
    {
        var order = ModelBreakdown.ColourOrder(Window);
        var today = new[] { Slice("claude-opus-5", 155_400_000, 91.44m) };

        var todayTokens = ModelBreakdown.Segments(today, BreakdownMeasure.Tokens, order);
        var windowValue = ModelBreakdown.Segments(Window, BreakdownMeasure.EstValue, order);

        var slotToday = ModelBreakdown.SlotFor(todayTokens[0].Model, order);
        var slotInWindow = ModelBreakdown.SlotFor(
            windowValue.Single(s => s.Model == "claude-opus-5").Model, order);

        Assert.Equal(slotToday, slotInWindow);
        Assert.Equal(1, slotToday);   // Opus 5 is slot 2 everywhere, not "first here"
    }

    // ── segments ──────────────────────────────────────────────────────────────

    [Fact]
    public void Segments_OrderNamedByMeasure_LargestFirst()
    {
        var order = ModelBreakdown.ColourOrder(Window);
        var segments = ModelBreakdown.Segments(Window, BreakdownMeasure.Tokens, order);

        Assert.Equal(["Opus 4.8", "Opus 5", ModelDisplayName.Other],
            segments.Select(s => s.DisplayName));
    }

    [Fact]
    public void Segments_ReorderPerMeasure_ButKeepTheSameColourSlots()
    {
        // Fable prices far above Opus per token, so a value chart can rank differently
        // from a token chart. The ORDER may change; the colour slot may not.
        var slices = new[]
        {
            Slice("claude-opus-5", 9_000, 10m),
            Slice("claude-fable-5", 3_000, 40m),
        };
        var order = ModelBreakdown.ColourOrder(slices);

        Assert.Equal("Opus 5", ModelBreakdown.Segments(slices, BreakdownMeasure.Tokens, order)[0].DisplayName);
        Assert.Equal("Fable 5", ModelBreakdown.Segments(slices, BreakdownMeasure.EstValue, order)[0].DisplayName);
        Assert.Equal(0, ModelBreakdown.SlotFor("claude-opus-5", order));
        Assert.Equal(1, ModelBreakdown.SlotFor("claude-fable-5", order));
    }

    [Fact]
    public void Segments_FoldTheTail_KeepingTheTotalIntact()
    {
        var order = ModelBreakdown.ColourOrder(Window);
        var segments = ModelBreakdown.Segments(Window, BreakdownMeasure.Tokens, order);

        var other = segments[^1];
        Assert.Equal(ModelDisplayName.Other, other.DisplayName);
        Assert.Equal(45_000_000, other.Tokens);
        Assert.Equal(82.14m, other.EstUsd);
        Assert.Equal(Window.Sum(s => s.Tokens), segments.Sum(s => s.Tokens));
        Assert.Equal("2 more", other.Model);
    }

    [Fact]
    public void Segments_ASingleLeftover_KeepsItsOwnName()
    {
        // "Other · 1 more models" would be a clumsy way to say "Fable 5". It still takes
        // the neutral colour, because it is not one of the panel's coloured models.
        var order = ModelBreakdown.ColourOrder(Window);
        var tile = new[]
        {
            Slice("claude-opus-4-8", 100, 1m),
            Slice("claude-fable-5", 50, 1m),
        };

        var segments = ModelBreakdown.Segments(tile, BreakdownMeasure.Tokens, order);

        Assert.Equal(["Opus 4.8", "Fable 5"], segments.Select(s => s.DisplayName));
        Assert.Equal(-1, ModelBreakdown.SlotFor("claude-fable-5", order));
    }

    [Fact]
    public void Segments_OtherIsAlwaysLast_SoTheNeutralNeverSitsMidBar()
    {
        var slices = Enumerable.Range(0, 6).Select(i => Slice($"claude-model-{i}", 10 - i, 1m)).ToArray();
        var order = ModelBreakdown.ColourOrder(slices);

        var segments = ModelBreakdown.Segments(slices, BreakdownMeasure.Tokens, order);

        Assert.Equal(ModelDisplayName.Other, segments[^1].DisplayName);
        Assert.DoesNotContain(segments.Take(segments.Count - 1),
            s => s.DisplayName == ModelDisplayName.Other);
    }

    [Fact]
    public void Segments_DropZeroMeasure_SoNoLegendEntryClaimsAnInvisibleSegment()
    {
        var slices = new[]
        {
            Slice("claude-opus-5", 100, 10m),
            // Real tokens, value that rounds to zero — the case this test is about. It
            // used to use "<synthetic>", which ingestion can never produce (issue #57);
            // the behaviour under test is the zero MEASURE, not the model id.
            Slice("claude-haiku-4-5", 40, 0m),
        };
        var order = ModelBreakdown.ColourOrder(slices);

        Assert.Equal(2, ModelBreakdown.Segments(slices, BreakdownMeasure.Tokens, order).Count);

        var byValue = ModelBreakdown.Segments(slices, BreakdownMeasure.EstValue, order);
        Assert.Single(byValue);
        Assert.Equal("Opus 5", byValue[0].DisplayName);
    }

    [Fact]
    public void Segments_UnpricedModel_LeavesTheValueChart_ButIsReportedNotLost()
    {
        var slices = new[]
        {
            Slice("claude-opus-5", 100, 10m),
            Slice("claude-brandnew-9", 60, null),   // no published rate
        };
        var order = ModelBreakdown.ColourOrder(slices);

        Assert.Equal(2, ModelBreakdown.Segments(slices, BreakdownMeasure.Tokens, order).Count);
        Assert.Single(ModelBreakdown.Segments(slices, BreakdownMeasure.EstValue, order));
        Assert.Equal(["claude-brandnew-9"], ModelBreakdown.Unpriced(slices));
    }

    [Fact]
    public void Segments_OtherGoesUnpriced_WhenAnyFoldedModelIs()
    {
        // A remainder that quietly summed only the priced ones would understate itself
        // while looking authoritative. Unknown in, unknown out.
        var slices = new[]
        {
            Slice("claude-opus-5", 100, 10m),
            Slice("claude-fable-5", 90, 9m),
            Slice("claude-sonnet-5", 80, 8m),
            Slice("claude-brandnew-9", 70, null),
        };
        var order = ModelBreakdown.ColourOrder(slices);

        var segments = ModelBreakdown.Segments(slices, BreakdownMeasure.Tokens, order);

        Assert.Equal(ModelDisplayName.Other, segments[^1].DisplayName);
        Assert.Null(segments[^1].EstUsd);
    }

    [Fact]
    public void Segments_Empty_WhenNothingRecorded()
    {
        Assert.Empty(ModelBreakdown.Segments([], BreakdownMeasure.Tokens, []));
        Assert.Empty(ModelBreakdown.Segments(
            [Slice("claude-opus-5", 0, 0m)], BreakdownMeasure.Tokens, ["claude-opus-5"]));
    }

    // ── display names ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("claude-opus-5", "Opus 5")]
    [InlineData("claude-opus-4-8", "Opus 4.8")]
    [InlineData("claude-sonnet-5-20260101", "Sonnet 5")]
    [InlineData("claude-fable-5", "Fable 5")]
    [InlineData("claude-haiku-4-5", "Haiku 4.5")]
    public void DisplayName_MapsKnownModels(string model, string expected) =>
        Assert.Equal(expected, ModelDisplayName.For(model));

    [Fact]
    public void DisplayName_UnknownModel_ShownAsIs_NeverGuessed()
    {
        // A model released after this table was written must render as its raw id.
        // Inferring "Opus 6" from the pattern would be a fabricated fact (rule 6).
        Assert.Equal("claude-opus-6", ModelDisplayName.For("claude-opus-6"));
        Assert.Equal("some-other-thing", ModelDisplayName.For("some-other-thing"));
    }
}
