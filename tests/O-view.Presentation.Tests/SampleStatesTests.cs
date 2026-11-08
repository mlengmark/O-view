using System.Text.RegularExpressions;
using OView.Presentation.Panel;
using OView.Presentation.Samples;
using OView.Presentation.Text;

namespace OView.Presentation.Tests;

/// <summary>
/// The sample states are what every golden image is rendered from, so a state that quietly
/// depended on the real clock, or contradicted itself, would poison every comparison.
/// </summary>
public class SampleStatesTests
{
    [Fact]
    public void NamesAreUniqueKebabCase_BecauseTheyBecomeGoldenFileNames()
    {
        var names = SampleStates.All.Select(s => s.Name).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(names, n => Assert.Matches(new Regex("^[a-z0-9]+(-[a-z0-9]+)*$"), n));
    }

    [Fact]
    public void EveryStateSaysWhatItShows()
    {
        Assert.All(SampleStates.All, s => Assert.False(string.IsNullOrWhiteSpace(s.Shows)));
    }

    [Fact]
    public void NoStateReadsTheRealClock()
    {
        // Every capture time is the fixed render instant (or absent), so a render made today
        // and one made next year draw the same pixels.
        Assert.All(SampleStates.All, s =>
        {
            if (s.Snapshot.CapturedAtUtcValue.Value is { } captured)
            {
                Assert.Equal(SampleStates.Now, captured);
            }
        });
    }

    [Fact]
    public void RecordedCompositionsAgreeWithTheTokenTiles()
    {
        // The tiles headline output; a composition whose Output disagreed would render a
        // panel contradicting itself (issue #253).
        foreach (var s in SampleStates.All)
        {
            if (s.Stats.CompositionToday.Output > 0)
            {
                Assert.Equal(s.Stats.TokensToday, s.Stats.CompositionToday.Output);
            }
            if (s.Stats.Composition31Days.Output > 0)
            {
                Assert.Equal(s.Stats.Tokens31Days, s.Stats.Composition31Days.Output);
            }
        }
    }

    /// <summary>
    /// The model-limit states render the bar on both heads, and they are the only ones that do,
    /// which is what leaves every other state's golden image untouched by the bar existing.
    /// </summary>
    [Fact]
    public void OnlyTheModelLimitStatesHaveAModelBar()
    {
        var withABar = SampleStates.All
            .Where(s => PanelSections.ModelLimitMeters(s.CachedUsage, SampleStates.Now, SampleStates.Zone).Count > 0)
            .ToList();

        Assert.Equal(
            ["model-limit", "model-limit-spend", "model-limit-spend-unknown"],
            withABar.Select(s => s.Name));

        Assert.All(withABar, state =>
        {
            var meter = Assert.Single(PanelSections.ModelLimitMeters(state.CachedUsage, SampleStates.Now, SampleStates.Zone));
            Assert.Equal("Weekly · Fable", meter.Label);
            Assert.NotNull(meter.FillPercent);
            Assert.NotNull(meter.ResetLine);
        });
    }

    private static readonly string[] RedesignStates =
    [
        "typical-overall", "typical-models", "typical-models-overall", "offplan-on-overall",
        "offplan-on-models", "offplan-on-hidden", "longest-figures",
    ];

    /// <summary>
    /// Per-model data is on the redesign's states and nowhere else, which is what leaves every
    /// earlier state's golden image as it was: the tiles' per-model split appears in none.
    /// </summary>
    [Fact]
    public void OnlyTheRedesignStatesCarryPerModelData()
    {
        var withModels = SampleStates.All.Where(s => s.Stats.Models31Days.Count > 0).Select(s => s.Name);

        Assert.Equal(
            ["typical-models", "typical-models-overall", "offplan-on-models", "longest-figures"],
            withModels);
    }

    [Fact]
    public void TheRedesignStatesExistAndTheModelFaceOnesAskForIt()
    {
        var names = SampleStates.All.Select(s => s.Name).ToList();

        Assert.All(RedesignStates, n => Assert.Contains(n, names));
        Assert.Equal(
            ["typical-models", "typical-models-overall", "offplan-on-models"],
            SampleStates.All.Where(s => s.View.Face == OView.Core.Models.PanelFace.Models).Select(s => s.Name));
        Assert.Equal(
            ["offplan-on-hidden"],
            SampleStates.All.Where(s => s.View.BannerHidden).Select(s => s.Name));
    }

    [Fact]
    public void EveryStateEarlierThanTheRedesignOpensOnTheFreshView()
    {
        Assert.All(
            SampleStates.All.Where(s => !RedesignStates.Contains(s.Name)),
            s => Assert.Equal(default, s.View));
    }

    /// <summary>
    /// The model rows add up to the figures above them, or the model face would contradict
    /// the totals face it flips from.
    /// </summary>
    [Fact]
    public void ModelRowsAddUpToTheWindowsTotals()
    {
        foreach (var s in SampleStates.All.Where(s => s.Stats.Models31Days.Count > 0))
        {
            Assert.Equal(s.Stats.Tokens31Days, s.Stats.Models31Days.Sum(m => m.Tokens));
            Assert.Equal(s.Stats.TokensToday, s.Stats.ModelsToday.Sum(m => m.Tokens));
            Assert.Equal(
                s.Stats.Composition31Days.Total, s.Stats.Models31Days.Sum(m => m.AllTokensCount.Value));
            Assert.Equal(
                s.Stats.CompositionToday.Total, s.Stats.ModelsToday.Sum(m => m.AllTokensCount.Value));
        }
    }

    [Fact]
    public void ASevenModelStoreGetsTheStoresColourOrderAndNoMore()
    {
        var stats = SampleStates.All.Single(s => s.Name == "typical-models").Stats;

        Assert.Equal(7, stats.Models31Days.Count);
        Assert.Equal(["claude-opus-5", "claude-sonnet-5"], stats.ModelColourOrder);
    }

    [Fact]
    public void TheLongestFiguresStateHoldsTheWidestFiguresThePanelIsAsked()
    {
        var s = SampleStates.All.Single(s => s.Name == "longest-figures");

        Assert.Equal("4733.4M", UsageFormatter.Tokens(s.Stats.Composition31Days.Total));
        Assert.Equal("$3513.67", UsageFormatter.Usd(s.Stats.Est31DaysValue.Value));
        Assert.True(s.Stats.Models31Days.Max(m => m.Model.Length) > 40);
    }

    [Fact]
    public void RendersInUtc_SoCiAndADesktopAgree()
    {
        Assert.Equal(TimeZoneInfo.Utc, SampleStates.Zone);
    }
}
