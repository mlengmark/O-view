using OView.Core.Models;

namespace OView.Core.Tests;

/// <summary>
/// The per-model tile breakdown (GitHub issue #37). The slot cap here is a colour
/// constraint expressed as code, so the tests pin the shape rather than the styling:
/// three models keep their own colour, four or more collapse to two named plus a
/// neutral remainder, because no validated neutral separates from the third chromatic
/// slot for deuteranopes.
/// </summary>
public class ModelBreakdownTests
{
    private static ModelSlice Slice(string model, long tokens, decimal? usd) =>
        new(model, ModelDisplayName.For(model), tokens, usd);

    [Fact]
    public void Segments_OrderLargestFirst()
    {
        var slices = new[]
        {
            Slice("claude-sonnet-5", 500, 1m),
            Slice("claude-opus-5", 9_000, 30m),
            Slice("claude-fable-5", 3_000, 20m),
        };

        var segments = ModelBreakdown.Segments(slices, BreakdownMeasure.Tokens);

        Assert.Equal(["Opus 5", "Fable 5", "Sonnet 5"], segments.Select(s => s.DisplayName));
    }

    [Fact]
    public void Segments_ReorderPerMeasure_ValueOrderCanDifferFromTokenOrder()
    {
        // Fable prices far above Opus per token, so the value chart's biggest segment
        // is not the token chart's. Sorting per measure is the whole point.
        var slices = new[]
        {
            Slice("claude-opus-5", 9_000, 10m),
            Slice("claude-fable-5", 3_000, 40m),
        };

        Assert.Equal("Opus 5", ModelBreakdown.Segments(slices, BreakdownMeasure.Tokens)[0].DisplayName);
        Assert.Equal("Fable 5", ModelBreakdown.Segments(slices, BreakdownMeasure.EstValue)[0].DisplayName);
    }

    [Fact]
    public void Segments_ThreeModels_AllKeepTheirOwnSlot()
    {
        var slices = new[]
        {
            Slice("claude-opus-5", 3, 3m),
            Slice("claude-fable-5", 2, 2m),
            Slice("claude-sonnet-5", 1, 1m),
        };

        var segments = ModelBreakdown.Segments(slices, BreakdownMeasure.Tokens);

        Assert.Equal(3, segments.Count);
        Assert.DoesNotContain(segments, s => s.DisplayName == ModelDisplayName.Other);
    }

    [Fact]
    public void Segments_FourModels_FoldTailIntoOther_KeepingTheTotalIntact()
    {
        var slices = new[]
        {
            Slice("claude-opus-5", 100, 10m),
            Slice("claude-fable-5", 50, 8m),
            Slice("claude-sonnet-5", 20, 3m),
            Slice("claude-haiku-4-5", 5, 1m),
        };

        var segments = ModelBreakdown.Segments(slices, BreakdownMeasure.Tokens);

        Assert.Equal(3, segments.Count);
        Assert.Equal(["Opus 5", "Fable 5", ModelDisplayName.Other], segments.Select(s => s.DisplayName));

        // The remainder carries the real sum, so the bar's proportions stay truthful
        // and the segment widths still add to the tile's total.
        var other = segments[^1];
        Assert.Equal(25, other.Tokens);
        Assert.Equal(4m, other.EstUsd);
        Assert.Equal(175, segments.Sum(s => s.Tokens));
        Assert.Equal("2 more", other.Model);
    }

    [Fact]
    public void Segments_OtherIsAlwaysLast_SoTheNeutralNeverSitsMidBar()
    {
        var slices = Enumerable.Range(0, 6)
            .Select(i => Slice($"claude-model-{i}", 10 - i, 1m))
            .ToArray();

        var segments = ModelBreakdown.Segments(slices, BreakdownMeasure.Tokens);

        Assert.Equal(ModelDisplayName.Other, segments[^1].DisplayName);
        Assert.DoesNotContain(segments.Take(segments.Count - 1), s => s.DisplayName == ModelDisplayName.Other);
    }

    [Fact]
    public void Segments_DropZeroMeasure_SoNoLegendEntryClaimsAnInvisibleSegment()
    {
        var slices = new[]
        {
            Slice("claude-opus-5", 100, 10m),
            Slice("<synthetic>", 40, 0m),      // real tokens, genuinely zero value
        };

        Assert.Equal(2, ModelBreakdown.Segments(slices, BreakdownMeasure.Tokens).Count);

        // Zero value means no segment to draw; a legend row for it would claim a share
        // of a total it contributed nothing to.
        var byValue = ModelBreakdown.Segments(slices, BreakdownMeasure.EstValue);
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

        // It still has tokens, so it appears in the token breakdown.
        Assert.Equal(2, ModelBreakdown.Segments(slices, BreakdownMeasure.Tokens).Count);

        // Its value is unknown, not zero, so it cannot be placed on the value chart —
        // but it must be named, never silently dropped (CLAUDE.md rule 6).
        Assert.Single(ModelBreakdown.Segments(slices, BreakdownMeasure.EstValue));
        Assert.Equal(["claude-brandnew-9"], ModelBreakdown.Unpriced(slices));
    }

    [Fact]
    public void Segments_OtherGoesUnpriced_WhenAnyFoldedModelIs()
    {
        // A remainder that quietly summed the priced ones would understate itself while
        // looking authoritative. Unknown in, unknown out.
        var slices = new[]
        {
            Slice("claude-opus-5", 100, 10m),
            Slice("claude-fable-5", 90, 9m),
            Slice("claude-sonnet-5", 80, 8m),
            Slice("claude-brandnew-9", 70, null),
        };

        var segments = ModelBreakdown.Segments(slices, BreakdownMeasure.Tokens);

        Assert.Equal(ModelDisplayName.Other, segments[^1].DisplayName);
        Assert.Null(segments[^1].EstUsd);
    }

    [Fact]
    public void Segments_Empty_WhenNothingRecorded()
    {
        Assert.Empty(ModelBreakdown.Segments([], BreakdownMeasure.Tokens));
        Assert.Empty(ModelBreakdown.Segments([Slice("claude-opus-5", 0, 0m)], BreakdownMeasure.Tokens));
    }

    [Theory]
    [InlineData("claude-opus-5", "Opus 5")]
    [InlineData("claude-opus-4-8", "Opus 4.8")]
    [InlineData("claude-sonnet-5-20260101", "Sonnet 5")]
    [InlineData("claude-fable-5", "Fable 5")]
    [InlineData("claude-haiku-4-5", "Haiku 4.5")]
    [InlineData("<synthetic>", "Local")]
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
