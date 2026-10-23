using OView.Core.Models;
using OView.Core.Providers.Jsonl;
using OView.Presentation.Panel;
using OView.Presentation.Samples;
using OView.Presentation.Text;

namespace OView.Presentation.Tests;

/// <summary>
/// The tokens-used section and the scope note, built once for both heads
/// (<see cref="PanelSections.TokenBars"/>, <see cref="PanelSections.ScopeNote"/>). These pin
/// the decisions each head used to make for itself: which kinds draw, what the legend reads,
/// how the breakdown table is shaped, and when the note beneath the tiles applies.
/// </summary>
public class PanelTokenBarsTests
{
    private static SampleState State(string name) => SampleStates.All.Single(s => s.Name == name);

    private static TranscriptScopeReport Scope(SampleState state) =>
        state.Scope ?? TranscriptScopeReport.Inspect(null, []);

    [Fact]
    public void ASectionWithTokensShowsAndNamesItsParts()
    {
        var state = State("typical");

        var view = PanelSections.TokenBars(state.Stats, Scope(state));

        Assert.True(view.Show);
        Assert.Equal(TokenText.TokensUsedHeading, view.Heading);
        Assert.Equal(TokenText.TokenViewBarsLabel, view.BarsLabel);
        Assert.Equal(TokenText.TokenViewBreakdownLabel, view.BreakdownLabel);
        Assert.Equal(TokenText.TokensUsedTodayLabel, view.Today.Heading);
        Assert.Equal(TokenText.TokensUsed31DaysLabel, view.Window31.Heading);
        Assert.Equal(UsageFormatter.Tokens(state.Stats.Composition31Days.Total), view.Window31.Total);
    }

    /// <summary>Only kinds with tokens draw, and every segment has a legend entry at the same index naming it.</summary>
    [Fact]
    public void SegmentsAreTheKindsWithTokensAndTheLegendPairsWithThem()
    {
        var state = State("typical");

        var bar = PanelSections.TokenBars(state.Stats, Scope(state)).Window31;

        Assert.All(bar.Segments, s => Assert.True(s.Tokens > 0));
        Assert.Equal(bar.Segments.Count, bar.Legend.Count);
        for (var i = 0; i < bar.Segments.Count; i++)
        {
            Assert.Equal(bar.Segments[i].Kind, bar.Legend[i].Kind);
            Assert.StartsWith(TokenText.TokenKindLabel(bar.Segments[i].Kind), bar.Legend[i].Text, StringComparison.Ordinal);
            Assert.EndsWith(UsageFormatter.Tokens(bar.Segments[i].Tokens), bar.Legend[i].Text, StringComparison.Ordinal);
        }
    }

    /// <summary>The table is a header row, one row per kind carrying its chip, and a bold total row.</summary>
    [Fact]
    public void TheBreakdownIsHeaderThenKindsThenTotal()
    {
        var state = State("typical");
        var stats = state.Stats;

        var rows = PanelSections.TokenBars(stats, Scope(state)).Breakdown;

        Assert.Equal(PanelTokenRowKind.Header, rows[0].RowKind);
        Assert.Null(rows[0].Kind);
        Assert.Equal(TokenText.TokenBreakdownKindHeader, rows[0].Label);

        var kinds = rows.Skip(1).Take(rows.Count - 2).ToList();
        Assert.All(kinds, r => Assert.Equal(PanelTokenRowKind.Kind, r.RowKind));
        Assert.All(kinds, r => Assert.NotNull(r.Kind));
        Assert.Equal(stats.CompositionToday.InDisplayOrder.Count, kinds.Count);

        var total = rows[^1];
        Assert.Equal(PanelTokenRowKind.Total, total.RowKind);
        Assert.Null(total.Kind);
        Assert.Equal(UsageFormatter.Tokens(stats.CompositionToday.Total), total.TodayTokens);
        Assert.Equal(UsageFormatter.Tokens(stats.Composition31Days.Total), total.WindowTokens);
    }

    /// <summary>Nothing recorded in either window means nothing to break down, so the section hides.</summary>
    [Fact]
    public void ASectionWithNoTokensInEitherWindowHides()
    {
        var state = State("no-local-transcripts");

        Assert.False(state.Stats.CompositionToday.HasTokens || state.Stats.Composition31Days.HasTokens);
        Assert.False(PanelSections.TokenBars(state.Stats, Scope(state)).Show);
    }

    /// <summary>
    /// The note applies exactly when the tiles read zero while the plan meters show real
    /// usage, and then it is the scan's own explanation.
    /// </summary>
    [Fact]
    public void TheScopeNoteAppearsOnlyForZeroTilesBesideRealPlanUsage()
    {
        var zero = State("no-local-transcripts");
        var typical = State("typical");

        Assert.Equal(Scope(zero).Explain(), PanelSections.ScopeNote(zero.Stats, zero.Snapshot, Scope(zero)));
        Assert.Null(PanelSections.ScopeNote(typical.Stats, typical.Snapshot, Scope(typical)));
    }
}
