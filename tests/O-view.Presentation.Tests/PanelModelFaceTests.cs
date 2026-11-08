using OView.Core.Models;
using OView.Presentation.Panel;
using OView.Presentation.Samples;
using OView.Presentation.Text;

namespace OView.Presentation.Tests;

/// <summary>
/// The local block's model face. Each test pins one decision a head would otherwise make for
/// itself: which models are rows, which order they and the doughnuts share, which colour each
/// takes, and how a model with no price reads.
/// </summary>
public class PanelModelFaceTests
{
    private static readonly PanelViewChoice Output = new(Face: PanelFace.Models);
    private static readonly PanelViewChoice Overall = new(PanelMeasure.Overall, PanelFace.Models);

    private static PanelStatistics Stats(string name) => SampleStates.All.Single(s => s.Name == name).Stats;

    private static PanelModelFaceView Face(string name, PanelViewChoice view) =>
        PanelSections.ModelFace(Stats(name), view);

    private static ModelSlice Slice(string model, long output, long all, decimal? usd, decimal? outputUsd) =>
        new(model, ModelDisplayName.For(model), output)
        {
            TokensCount = TokenCount.Measured(output),
            AllTokensCount = TokenCount.Measured(all),
            EstUsdValue = new(usd, usd is null ? UsageValueStatus.Unavailable : UsageValueStatus.Estimated),
            EstOutputUsdValue = new(outputUsd, outputUsd is null ? UsageValueStatus.Unavailable : UsageValueStatus.Estimated),
        };

    private static PanelStatistics WithModels(
        IReadOnlyList<ModelSlice> window, IReadOnlyList<ModelSlice> today) =>
        Stats("typical") with { Models31Days = window, ModelsToday = today };

    // ── which models ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryModelInTheStoreIsARowAndNothingIsFoldedIntoOther(bool overall)
    {
        var stats = Stats("typical-models");
        var face = PanelSections.ModelFace(stats, overall ? Overall : Output);

        Assert.Equal(7, face.Table.Rows.Count);
        Assert.Equal(
            stats.Models31Days.Select(m => m.Model).Order(),
            face.Table.Rows.Select(r => r.Id).Order());
        Assert.DoesNotContain(face.Table.Rows, r => r.Name == TokenText.OtherSegmentLabel);
    }

    [Fact]
    public void ARowCarriesTheModelsWholeNameAndIdForTheHeadToTrimOnlyWhereItDraws()
    {
        var row = Face("typical-models", Output).Table.Rows[0];

        Assert.Equal("claude-opus-5", row.Id);
        Assert.Equal(ModelDisplayName.For("claude-opus-5"), row.Name);
    }

    [Fact]
    public void ARowForAModelOnlyTheWindowSawStaysInTheTable()
    {
        var stats = WithModels(
            [Slice("claude-opus-5", 900, 9_000, 12m, 3m), Slice("claude-haiku-5", 100, 1_000, 1m, 0.5m)],
            [Slice("claude-opus-5", 90, 900, 1.2m, 0.3m)]);

        var face = PanelSections.ModelFace(stats, Output);

        Assert.Equal(2, face.Table.Rows.Count);
        Assert.Single(face.Today.Sections);
        Assert.Equal(2, face.Window.Sections.Count);
    }

    // ── the doughnuts ───────────────────────────────────────────────────────────────

    [Fact]
    public void TheSectionsFollowTheTablesOrderAndSumToTheWholeRing()
    {
        var face = Face("typical-models", Output);

        foreach (var doughnut in new[] { face.Today, face.Window })
        {
            Assert.Equal(doughnut.Sections.Select(s => s.Row).Order(), doughnut.Sections.Select(s => s.Row));
            Assert.Equal(1.0, doughnut.Sections.Sum(s => s.Share), 6);
        }

        // Largest first, so the biggest sweep starts at the top.
        Assert.Equal(
            face.Window.Sections.Select(s => s.Share).OrderDescending(),
            face.Window.Sections.Select(s => s.Share));
    }

    [Fact]
    public void TheCentreTotalsAndTheirUnitsFollowTheMeasure()
    {
        var stats = Stats("typical-models");
        var output = PanelSections.ModelFace(stats, Output);
        var overall = PanelSections.ModelFace(stats, Overall);

        Assert.Equal(UsageFormatter.Tokens(stats.TokensToday), output.Today.Total);
        Assert.Equal(UsageFormatter.Tokens(stats.Tokens31Days), output.Window.Total);
        Assert.Equal("output", output.Window.Unit);
        Assert.Equal(UsageFormatter.Tokens(stats.CompositionToday.Total), overall.Today.Total);
        Assert.Equal(UsageFormatter.Tokens(stats.Composition31Days.Total), overall.Window.Total);
        Assert.Equal("tokens", overall.Window.Unit);
        Assert.Equal("today", output.Today.Caption);
        Assert.Equal("last 31 days", output.Window.Caption);
    }

    [Fact]
    public void AHoverCardNamesTheFigureTheShareAndTheValue()
    {
        var stats = WithModels(
            [Slice("claude-opus-5", 370, 3_700, 12.4m, 3.1m), Slice("claude-haiku-5", 630, 6_300, 1m, 0.5m)],
            []);

        var section = PanelSections.ModelFace(stats, Output).Window.Sections.Single(s => s.Row == 1);

        Assert.Equal("370", section.Figure);
        Assert.Equal("output · 37% of 31 days", section.ShareLine);
        Assert.Equal("$3.10 est. value", section.ValueLine);
    }

    [Fact]
    public void WhileOffPlanTodaysValueReadsAsSpendAndTheWindowsStaysValue()
    {
        var face = Face("offplan-on-models", Overall);

        Assert.Equal("Est. spend", face.Table.Headers[2]);
        Assert.Equal("Est. value", face.Table.Headers[4]);
        Assert.All(face.Today.Sections, s => Assert.EndsWith(" est. spend", s.ValueLine));
        Assert.All(face.Window.Sections, s => Assert.EndsWith(" est. value", s.ValueLine));

        var onPlan = Face("typical-models", Overall);
        Assert.Equal("Est. value", onPlan.Table.Headers[2]);
    }

    [Fact]
    public void AShareTooSmallToRoundIsNotWrittenAsZero()
    {
        var stats = WithModels(
            [Slice("claude-opus-5", 99_800, 99_800, 1m, 1m), Slice("claude-haiku-5", 100, 100, 1m, 1m)],
            []);

        var sections = PanelSections.ModelFace(stats, Output).Window.Sections;

        Assert.Equal("output · <1% of 31 days", sections[1].ShareLine);
    }

    // ── the words ───────────────────────────────────────────────────────────────────

    [Fact]
    public void TheSubtitleTheFlipTagAndTheColumnsAreWorded()
    {
        var output = Face("typical-models", Output);
        var overall = Face("typical-models", Overall);

        Assert.Equal("by model · output tokens", output.Subtitle);
        Assert.Equal("by model · all tokens", overall.Subtitle);
        Assert.Equal("totals", output.FlipLabel);
        Assert.Equal("by model", TokenText.FlipTagLabel(PanelFace.Totals));
        Assert.Equal(["Model", "Tokens", "Est. value", "Tokens", "Est. value"], output.Table.Headers);
        Assert.Equal("Today", output.Table.TodayGroup);
        Assert.Equal("31 days", output.Table.WindowGroup);
    }

    [Fact]
    public void OutputCountsOutputTokensAndOverallCountsEverythingBilled()
    {
        var stats = Stats("typical-models");
        var top = stats.Models31Days.OrderByDescending(m => m.Tokens).First();

        var output = PanelSections.ModelFace(stats, Output).Table.Rows[0];
        var overall = PanelSections.ModelFace(stats, Overall).Table.Rows[0];

        Assert.Equal(top.Model, output.Id);
        Assert.Equal(UsageFormatter.Tokens(top.Tokens), output.Window.Tokens);
        Assert.Equal(UsageFormatter.Usd(top.EstOutputUsdValue.Value), output.Window.Value);
        Assert.Equal(UsageFormatter.Tokens(top.AllTokensCount.Value!.Value), overall.Window.Tokens);
        Assert.Equal(UsageFormatter.Usd(top.EstUsdValue.Value), overall.Window.Value);
    }

    // ── colours ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(2, 0)]
    [InlineData(3, 0)]
    [InlineData(5, 2)]
    [InlineData(7, 4)]
    public void TheThreeLargestTakeTheSeriesSlotsAndTheRestStepThroughTheGreys(int models, int greys)
    {
        var slices = Enumerable.Range(0, models)
            .Select(i => Slice($"model-{i}", 1_000 - i * 10, 1_000 - i * 10, 1m, 1m))
            .ToList();

        var face = PanelSections.ModelFace(WithModels(slices, []), Output);

        Assert.Equal(greys, face.GreyCount);
        Assert.Equal(
            Enumerable.Range(0, models).Select(i => i < 3 ? i : -1),
            face.Table.Rows.Select(r => r.Slot));
        Assert.Equal(
            Enumerable.Range(0, models).Select(i => i < 3 ? -1 : i - 3),
            face.Table.Rows.Select(r => r.GreyStep));
    }

    [Fact]
    public void AModelsColourIsItsRowsSoBothDoughnutsAndTheTableAgree()
    {
        // The sections name their row rather than a colour of their own, and the same row
        // is named by both doughnuts, so there is no second place for a colour to be decided.
        var face = Face("typical-models", Output);

        Assert.All(face.Today.Sections.Concat(face.Window.Sections), s => Assert.InRange(s.Row, 0, 6));
        Assert.Equal(
            face.Table.Rows.Select(r => r.Id).Distinct().Count(),
            face.Table.Rows.Count);
    }

    // ── unpriced ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnUnpricedModelsValueCellSaysUnpricedNeverBlankOrZero(bool overall)
    {
        var unpriced = Slice("claude-unheard-of-9", 400, 4_000, null, null);
        var stats = WithModels(
            [Slice("claude-opus-5", 900, 9_000, 12m, 3m), unpriced],
            [Slice("claude-opus-5", 90, 900, 1.2m, 0.3m), unpriced with { Tokens = 40 }]);

        var face = PanelSections.ModelFace(stats, overall ? Overall : Output);
        var row = face.Table.Rows.Single(r => r.Id == "claude-unheard-of-9");

        foreach (var figure in new[] { row.Today, row.Window })
        {
            Assert.Equal(TokenText.UnpricedValue, figure.Value);
            Assert.False(figure.ValueKnown);
        }

        // Its tokens are real and stay counted, only the money is unknown.
        Assert.Equal(overall ? "4.0K" : "400", row.Window.Tokens);
        Assert.Equal(
            TokenText.UnpricedValue,
            face.Window.Sections.Single(s => s.Row == face.Table.Rows.ToList().IndexOf(row)).ValueLine);
    }

    [Fact]
    public void APricedModelsValueCellIsKnown()
    {
        var face = Face("typical-models", Output);

        Assert.All(face.Table.Rows, r =>
        {
            Assert.True(r.Window.ValueKnown);
            Assert.StartsWith("$", r.Window.Value);
        });
    }

    [Fact]
    public void APricedModelThatDidNothingTodayReadsAsAKnownZeroNotAsUnpriced()
    {
        var stats = WithModels(
            [Slice("claude-opus-5", 900, 9_000, 12m, 3m), Slice("claude-haiku-5", 100, 1_000, 1m, 0.5m)],
            [Slice("claude-opus-5", 90, 900, 1.2m, 0.3m)]);

        var idle = PanelSections.ModelFace(stats, Output).Table.Rows.Single(r => r.Id == "claude-haiku-5");

        Assert.Equal("0", idle.Today.Tokens);
        Assert.Equal("$0.00", idle.Today.Value);
        Assert.True(idle.Today.ValueKnown);
    }

    [Fact]
    public void AnUnpricedModelThatDidNothingTodayStaysUnpricedToday()
    {
        var stats = WithModels(
            [Slice("claude-opus-5", 900, 9_000, 12m, 3m), Slice("claude-unheard-of-9", 100, 1_000, null, null)],
            [Slice("claude-opus-5", 90, 900, 1.2m, 0.3m)]);

        var idle = PanelSections.ModelFace(stats, Output).Table.Rows.Single(r => r.Id == "claude-unheard-of-9");

        Assert.Equal(TokenText.UnpricedValue, idle.Today.Value);
        Assert.False(idle.Today.ValueKnown);
    }

    [Fact]
    public void ACountTheStoreDidNotRecordReadsUnknownRatherThanZero()
    {
        var recorded = Slice("claude-opus-5", 900, 9_000, 12m, 3m);
        var unrecorded = recorded with { AllTokensCount = new(null, UsageValueStatus.Unavailable) };

        var face = PanelSections.ModelFace(WithModels([unrecorded], []), Overall);

        Assert.Equal(TokenText.UnknownTokens, face.Table.Rows[0].Window.Tokens);
        Assert.Empty(face.Window.Sections);
    }

    [Fact]
    public void AStoreWithNoModelsGivesAnEmptyFaceNotAnError()
    {
        var face = PanelSections.ModelFace(WithModels([], []), Output);

        Assert.Empty(face.Table.Rows);
        Assert.Empty(face.Today.Sections);
        Assert.Empty(face.Window.Sections);
        Assert.Equal(0, face.GreyCount);
    }
}
