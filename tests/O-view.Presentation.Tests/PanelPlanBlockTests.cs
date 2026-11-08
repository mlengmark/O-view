using OView.Presentation.Panel;
using OView.Presentation.Samples;
using OView.Presentation.Text;

namespace OView.Presentation.Tests;

/// <summary>
/// The plan block: which meters it holds and in what order, the off-plan row that closes it,
/// and the banner above both blocks. Built from the sample states so the block is asserted
/// against the same data the goldens are rendered from.
/// </summary>
public class PanelPlanBlockTests
{
    private static SampleState State(string name) => SampleStates.All.Single(s => s.Name == name);

    private static PanelPlanBlockView Block(string name, PanelViewChoice view = default)
    {
        var s = State(name);
        var offPlan = PanelSections.OffPlan(s.Stats, s.CachedUsage, SampleStates.Now, SampleStates.Zone);

        return PanelSections.PlanBlock(
            PanelSections.SessionMeter(s.Snapshot, null, SampleStates.Now, SampleStates.Zone),
            PanelSections.WeeklyMeter(s.Snapshot, null, null, s.Boost, SampleStates.Now, SampleStates.Zone),
            PanelSections.ModelLimitMeters(s.CachedUsage, SampleStates.Now, SampleStates.Zone),
            offPlan, s.Stats, s.CachedUsage, view);
    }

    [Fact]
    public void TheMetersAreSessionThenWeeklyThenEachModelsOwnLimit()
    {
        var block = Block("model-limit");

        Assert.Equal(PanelText.PlanBlockTitle, block.Title);
        Assert.Equal(
            [PanelText.SessionBarLabel, PanelText.WeeklyBarLabel, "Weekly · Fable"],
            block.Meters.Select(m => m.Label));
    }

    [Fact]
    public void AnAccountWithNoModelLimitHasTwoMeters()
    {
        Assert.Equal(2, Block("typical").Meters.Count);
    }

    [Fact]
    public void TheOffPlanRowNamesAnEstimateAndItsWindow()
    {
        var row = Assert.IsType<PanelOffPlanRowView>(Block("offplan-on").OffPlanRow);

        Assert.Equal(OffPlanText.ReportedSpendHeading, row.Title);
        Assert.Equal(PanelText.CreditBadgeRecorded, row.Badge);
        Assert.Equal("est. credit spend · last 31 days", row.Caption);
        Assert.Equal("$92.75", row.Value);
    }

    [Fact]
    public void TheCoverageCaveatRidesOnTheRowItQualifies()
    {
        // 10 of 31 days recorded: the figure is real but partial, and the row says so itself
        // rather than leaving it to a caveat elsewhere on the panel.
        Assert.Equal("10 of 31 days recorded", Block("offplan-on").OffPlanRow!.Coverage);
    }

    [Fact]
    public void ClaudesOwnAmountIsCaptionedAsClaudesAndNotAsAnEstimate()
    {
        var row = Assert.IsType<PanelOffPlanRowView>(Block("model-limit-spend").OffPlanRow);

        Assert.Equal(PanelText.ReportedSpendBadge, row.Badge);
        Assert.Equal(OffPlanText.ReportedSpendRowCaption, row.Caption);
        Assert.Equal("$12.34", row.Value);
        Assert.Equal("", row.Coverage);
    }

    [Fact]
    public void ThereIsNoRowWhenNothingFellOutsideThePlan()
    {
        Assert.Null(Block("model-limit").OffPlanRow);
        Assert.Null(Block("no-data").OffPlanRow);
    }

    [Fact]
    public void TheBannerShowsWhileOffPlanAndNotWhileOnPlan()
    {
        Assert.NotNull(Block("offplan-on").Banner);
        Assert.Null(Block("typical").Banner);
    }

    [Fact]
    public void HidingTheBannerHidesOnlyTheBanner()
    {
        var hidden = Block("offplan-on", new PanelViewChoice(BannerHidden: true));

        Assert.Null(hidden.Banner);
        Assert.NotNull(hidden.OffPlanRow);
        Assert.Equal(Block("offplan-on").Meters, hidden.Meters);
        Assert.Equal(PanelText.HideBannerLabel, hidden.HideBannerLabel);
    }

    [Fact]
    public void TheSameChoiceHidesTheBannerInTheSampleThatShowsItHidden()
    {
        var hiddenState = State("offplan-on-hidden");

        Assert.True(hiddenState.View.BannerHidden);
        Assert.Null(Block("offplan-on-hidden", hiddenState.View).Banner);
    }
}
