using System.Reflection;

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using OView.App.Rendering;
using OView.Core.Models;
using OView.Linux.Panel;
using OView.Presentation.Panel;
using OView.Presentation.Samples;
using OView.Presentation.Text;

namespace OView.Linux.Tests;

/// <summary>
/// The "On this machine" block as this head lays it out: that every measurement is the shared one,
/// that every word and figure is the one Presentation built, and that nothing crosses the panel's
/// edge or is trimmed at either density.
///
/// <para>The trees are real and laid out with a real font, for the reason
/// <see cref="PanelShortScreenTests"/> gives, and by hand, as the verification renders are.</para>
/// </summary>
public class PanelLocalBlockTests
{
    private const string Typical = "typical";

    private const string OffPlan = "offplan-on";

    private const string NoTranscripts = "no-local-transcripts";

    public static TheoryData<string> EveryState()
    {
        var data = new TheoryData<string>();
        foreach (var state in SampleStates.All)
        {
            data.Add(state.Name);
        }

        return data;
    }

    public static TheoryData<string, bool, bool> EveryStateThemeAndDensity()
    {
        var data = new TheoryData<string, bool, bool>();
        foreach (var state in SampleStates.All)
        {
            foreach (var light in new[] { false, true })
            {
                data.Add(state.Name, light, false);
                data.Add(state.Name, light, true);
            }
        }

        return data;
    }

    // ── the block ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheTitleAndTheLineUnderItAreTheSharedOnes(bool light)
    {
        var panel = Laid(Typical, light);
        var totals = Totals(Typical);
        var title = Words(panel, totals.Title.ToUpperInvariant());
        var subtitle = Words(panel, totals.Subtitle);

        Assert.Equal(PanelMetrics.BlockTitleFontSize, title.FontSize);
        Assert.Equal(FontWeight.SemiBold, title.FontWeight);
        Assert.Equal(Hex("TextSecondary", light), Colour(title.Foreground));
        Assert.Equal(PanelMetrics.BlockTitleFontSize * PanelMetrics.LetterSpacingEm, title.LetterSpacing, 6);
        Assert.Equal(totals.Title, AutomationProperties.GetName(title));

        Assert.Equal(PanelMetrics.BlockSubtitleFontSize, subtitle.FontSize);
        Assert.Equal(Hex("TextMuted", light), Colour(subtitle.Foreground));
    }

    /// <summary>The block's parts in the design's order, each at its gap, at both densities.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThePartsStandInOrderAtTheDensitysGaps(bool compact)
    {
        var density = compact ? PanelDensity.Compact : PanelDensity.Normal;
        var scale = density.SpacingScale;
        var panel = Laid(Typical, false, density);
        var table = TableOf(panel);
        var block = BlockOf(table);
        var parts = block.Children;

        Assert.Equal(6 + 1, parts.Count);
        Assert.Same(Words(panel, Totals(Typical).Title.ToUpperInvariant()), parts[0]);
        Assert.Same(Words(panel, Totals(Typical).Subtitle), parts[1]);
        Assert.Same(PanelGraphLayoutTests.ChartOf(panel), parts[2]);
        Assert.Same(table, parts[4]);
        Assert.Same(Words(block, Totals(Typical).WindowCaveat), parts[5]);
        Assert.Same(Words(block, Footer(Typical).Caveat).GetVisualParent(), parts[6]);

        Assert.Equal(8 * scale, parts[2].Margin.Top, 6);
        Assert.Equal(PanelMetrics.ChartToTableGapDip * scale, parts[3].Margin.Top, 6);
        Assert.Equal(PanelMetrics.ChartToTableGapDip * scale, table.Margin.Top, 6);
        Assert.Equal(8 * scale, parts[6].Margin.Top, 6);

        var line = Assert.IsType<Border>(parts[3]);
        Assert.Equal(1, line.Bounds.Height);
        Assert.Equal(block.Bounds.Width, line.Bounds.Width);
        Assert.Equal(PanelMetrics.SoftLineOpacity, line.Opacity);
    }

    /// <summary>
    /// Space below the plan block's line and above this block's title: the panel's spacing and the
    /// block's own margin together make up one block padding, at both densities, within the pixel
    /// layout rounding leaves.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheLineBetweenTheBlocksHasTheBlocksPaddingBelowIt(bool compact)
    {
        var density = compact ? PanelDensity.Compact : PanelDensity.Normal;
        var panel = Laid(Typical, false, density);
        var block = BlockOf(TableOf(panel));
        var root = Assert.IsType<StackPanel>(block.GetVisualParent());
        var above = root.Children[root.Children.IndexOf(block) - 1];

        Assert.Equal(
            PanelMetrics.BlockPaddingYDip * density.SpacingScale,
            block.Bounds.Y + block.Margin.Top - above.Bounds.Bottom,
            1.0);
        Assert.Equal(PanelMetrics.BlockPaddingYDip * density.SpacingScale, block.Margin.Bottom, 6);
    }

    // ── the table ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// The data columns keep the widths the figures were measured against and the label column has
    /// what they leave, whichever width the density gives the panel's column.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheDataColumnsAreTheSharedWidthsAndTheLabelColumnTakesTheRest(bool compact)
    {
        var density = compact ? PanelDensity.Compact : PanelDensity.Normal;
        var panel = Laid(Typical, false, density);
        var table = TableOf(panel);

        Assert.Equal(panel.Bounds.Width - 2 * (panel.BorderThickness.Left + panel.Padding.Left), table.Bounds.Width);
        Assert.Equal(PanelMetrics.OutputColumnDip, table.ColumnDefinitions[1].ActualWidth);
        Assert.Equal(PanelMetrics.OutputColumnDip, table.ColumnDefinitions[2].ActualWidth);
        Assert.Equal(table.Bounds.Width - 2 * PanelMetrics.OutputColumnDip, table.ColumnDefinitions[0].ActualWidth);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheHeadingsAreLetterSpacedCaptionsCentredInTheirColumns(bool light)
    {
        var table = TableOf(Laid(Typical, light));

        for (var column = 1; column <= 2; column++)
        {
            var heading = Cell(table, 0, column);

            Assert.Equal(Totals(Typical).Table.Headers[column].ToUpperInvariant(), heading.Text);
            Assert.Equal(Totals(Typical).Table.Headers[column], AutomationProperties.GetName(heading));
            Assert.Equal(PanelMetrics.TableHeaderFontSize, heading.FontSize);
            Assert.Equal(PanelMetrics.TableHeaderFontSize * PanelMetrics.LetterSpacingEm, heading.LetterSpacing, 6);
            Assert.Equal(Hex("TextMuted", light), Colour(heading.Foreground));
            Assert.Equal(CentreOf(table, column), heading.Bounds.Center.X, 0.51);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheFiguresAreCentredSemiboldAndTabular(bool light)
    {
        var table = TableOf(Laid(Typical, light));

        foreach (var row in new[] { 1, 2 })
        {
            foreach (var column in new[] { 1, 2 })
            {
                var figure = Cell(table, row, column);

                Assert.Equal(PanelMetrics.TableFigureFontSize, figure.FontSize);
                Assert.Equal(FontWeight.SemiBold, figure.FontWeight);
                Assert.Equal(Hex("TextPrimary", light), Colour(figure.Foreground));
                Assert.Equal("tnum", Assert.Single(figure.FontFeatures!).Tag);
                Assert.Equal(CentreOf(table, column), figure.Bounds.Center.X, 0.51);
            }

            var label = Cell(table, row, 0);
            Assert.Equal(PanelMetrics.TableLabelFontSize, label.FontSize);
            Assert.Equal(Hex("TextSecondary", light), Colour(label.Foreground));
            Assert.Equal(0, label.Bounds.X);
        }
    }

    /// <summary>A soft line above every row, across the table, and a softer one at the left of every data column.</summary>
    [Fact]
    public void EveryRowHasASoftLineAndEveryDataColumnASofterOne()
    {
        var table = TableOf(Laid(Typical, false));
        var lines = table.Children.OfType<Border>().ToList();

        var rows = lines.Where(l => l.Opacity == PanelMetrics.SoftLineOpacity).ToList();
        Assert.Equal([1, 2], rows.Select(Grid.GetRow));
        Assert.All(rows, line =>
        {
            Assert.Equal(1, line.Bounds.Height);
            Assert.Equal(table.Bounds.Width, line.Bounds.Width);
        });

        var columns = lines.Where(l => l.Opacity == PanelMetrics.ColumnLineOpacity).ToList();
        Assert.Equal([1, 2], columns.Select(Grid.GetColumn));
        Assert.Equal(2, columns.Count);
        Assert.All(columns, line =>
        {
            Assert.Equal(1, line.Bounds.Width);
            Assert.Equal(table.Bounds.Height, line.Bounds.Height);
        });

        Assert.Equal(lines.Count, rows.Count + columns.Count);
    }

    /// <summary>Tightens the rows and leaves the type alone, so a short display loses space and not legibility.</summary>
    [Fact]
    public void TheCompactDensityTightensTheRowsAndLeavesTheType()
    {
        var normal = Cell(TableOf(Laid(Typical, false)), 1, 0);
        var compact = Cell(TableOf(Laid(Typical, false, PanelDensity.Compact)), 1, 0);

        Assert.True(compact.Margin.Top < normal.Margin.Top);
        Assert.True(compact.Margin.Bottom < normal.Margin.Bottom);
        Assert.Equal(normal.FontSize, compact.FontSize);
    }

    // ── the words ───────────────────────────────────────────────────────────────────

    /// <summary>Every word and figure is the view model's, cell for cell, in every state.</summary>
    [Theory]
    [MemberData(nameof(EveryState))]
    public void TheTableShowsExactlyWhatPresentationBuilt(string state)
    {
        var table = TableOf(Laid(state, false));
        var view = Totals(state).Table;

        Assert.Equal(view.Headers.Select(h => h.ToUpperInvariant()), Enumerable.Range(0, view.Headers.Count).Select(c => Cell(table, 0, c).Text));

        for (var row = 0; row < view.Rows.Count; row++)
        {
            var cells = view.Rows[row].Cells;

            Assert.Equal(view.Rows[row].Label, Words(table, view.Rows[row].Label).Text);
            Assert.Equal(
                cells,
                cells.Select((_, i) => TextIn(table, row + 1, 1 + i * view.Rows[row].CellSpan)));
        }
    }

    [Fact]
    public void WhileOffPlanTheValueRowSaysSpend()
    {
        var table = TableOf(Laid(OffPlan, false));

        Assert.Equal(TokenText.EstValueOutputLabel(true), Cell(table, 2, 0).Text);
    }

    /// <summary>The Overall value row says spend in its own words, and each estimate sits in its pill.</summary>
    [Fact]
    public void WhileOffPlanTheOverallValueRowSaysSpendInAPill()
    {
        var name = OffPlan + "-overall";
        var table = TableOf(Laid(name, false));
        var value = Totals(name).Table.Rows[^1];

        Assert.Equal(TokenText.EstValueLabel(true), value.Label);
        Assert.Equal(TokenText.EstValueLabel(true), Words(table, value.Label).Text);
        Assert.Equal(2, value.CellSpan);
        Assert.Equal(value.Cells, new[] { 1, 3 }.Select(c => TextIn(table, value.Label == null ? 0 : table.RowDefinitions.Count - 1, c)));
    }

    /// <summary>
    /// What qualifies the 31-day figures is said once, under the table and ending beneath them.
    /// Only the off-plan row's own caveat may repeat the words: that one qualifies a different figure.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryState))]
    public void TheCoverageCaveatIsSaidOnceBesideTheFiguresItQualifies(string state)
    {
        var panel = Laid(state, false);
        var block = BlockOf(TableOf(panel));
        var caveat = Totals(state).WindowCaveat;
        var texts = block.GetVisualDescendants().OfType<TextBlock>();

        if (caveat.Length == 0)
        {
            Assert.Equal(block.Children.IndexOf(TableOf(panel)) + 1 + (HasNote(state) ? 1 : 0), block.Children.Count - 1);
            return;
        }

        var line = Assert.Single(texts, t => t.Text == caveat);
        Assert.Equal(TextAlignment.Right, line.TextAlignment);
        Assert.Equal(PanelMetrics.OffPlanCaveatFontSize, line.FontSize);
        Assert.Equal(Hex("WarnText", false), Colour(line.Foreground));
        Assert.Equal(block.Children.IndexOf(TableOf(panel)) + 1, block.Children.IndexOf(line));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheFooterIsTheOneCaveatInTheFooterSize(bool light)
    {
        var footer = Words(Laid(Typical, light), Footer(Typical).Caveat);

        Assert.Equal(PanelMetrics.FooterFontSize, footer.FontSize);
        Assert.Equal(VerticalAlignment.Center, footer.VerticalAlignment);
        Assert.Equal(Hex("TextMuted", light), Colour(footer.Foreground));
    }

    /// <summary>The note that explains a zero stands with the figures it is about, above the footer.</summary>
    [Fact]
    public void TheNoteExplainingAZeroStandsAboveTheFooter()
    {
        var panel = Laid(NoTranscripts, false);
        var block = BlockOf(TableOf(panel));
        var state = SampleStates.All.Single(s => s.Name == NoTranscripts);
        var note = Assert.IsType<string>(PanelSections.ScopeNote(state.Stats, state.Snapshot, state.Scope!));
        var line = Words(panel, note);

        Assert.Equal(block.Children.Count - 2, block.Children.IndexOf(line));
        Assert.Same(Words(panel, Footer(NoTranscripts).Caveat).GetVisualParent(), block.Children[^1]);
    }

    /// <summary>The figures follow the stored measure: an Overall view shows the kind rows, not Output's one row.</summary>
    [Fact]
    public void AnOverallStateShowsTheOverallTable()
    {
        var state = SampleStates.All.First(s => s.View.Measure == PanelMeasure.Overall);
        var panel = Laid(state.Name, false);
        var table = TableOf(panel);

        Assert.Equal(5, table.ColumnDefinitions.Count);
        Assert.DoesNotContain(table.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == TokenText.OutputTokensRowLabel);
        Assert.Equal(Totals(state.Name).Subtitle, Words(panel, Totals(state.Name).Subtitle).Text);
    }

    /// <summary>The tiles, the tokens-used bars and the chart's old heading have all left the panel.</summary>
    [Theory]
    [MemberData(nameof(EveryState))]
    public void TheTilesTheBarsAndTheOldHeadingHaveLeftThePanel(string name)
    {
        var state = SampleStates.All.Single(s => s.Name == name);
        var panel = Laid(name, false);
        var words = panel.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();

        Assert.DoesNotContain(PanelText.UsageWindowHeading(state.Stats.WindowDays), words);
        Assert.DoesNotContain(TokenText.TokenViewBarsLabel, words);
        Assert.DoesNotContain(TokenText.TokenViewBreakdownLabel, words);
        Assert.DoesNotContain(TokenText.TokenScopeCaveat, words);

        // TileBg now only fills the switch and the Overall table's pills, and neither is a tile.
        var filled = panel.GetVisualDescendants().OfType<Border>().Where(b => IsColour(b.Background, "TileBg"));
        Assert.All(filled, b => Assert.True(
            b.GetVisualDescendants().OfType<RadioButton>().Any() || Grid.GetColumnSpan(b) == 2,
            "something other than the switch or a pill is filled like a tile"));
    }

    // ── the panel's edge ────────────────────────────────────────────────────────────

    /// <summary>
    /// Every state in both themes at both densities, the longest figures among them: nothing in
    /// the block leaves the panel's column, nothing is trimmed, and no figure needs a second line.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryStateThemeAndDensity))]
    public void NothingInTheBlockCrossesThePanelOrIsTrimmed(string state, bool light, bool compact)
    {
        var panel = Laid(state, light, compact ? PanelDensity.Compact : PanelDensity.Normal);
        var table = TableOf(panel);
        var inset = panel.BorderThickness.Left + panel.Padding.Left;

        foreach (var mark in BlockOf(table).GetVisualDescendants().OfType<Control>().Where(c => c.IsVisible))
        {
            var left = mark.TranslatePoint(default, panel)!.Value.X;

            Assert.True(left >= inset - 0.5, $"{mark.GetType().Name} starts at {left:0.#} in {state}");
            Assert.True(
                left + mark.Bounds.Width <= panel.Bounds.Width - inset + 0.5,
                $"{mark.GetType().Name} ends at {left + mark.Bounds.Width:0.#} in {state}");

            if (mark is TextBlock { Opacity: > 0 } line)
            {
                Assert.False(line.TextLayout.TextLines.Any(l => l.HasCollapsed), $"[{line.Text}] was trimmed in {state}");
            }
        }

        foreach (var figure in table.Children.OfType<TextBlock>().Where(t => Grid.GetRow(t) > 0 && Grid.GetColumn(t) > 0))
        {
            Assert.True(figure.TextLayout.TextLines.Count == 1, $"[{figure.Text}] took two lines in {state}");
        }
    }

    /// <summary>
    /// The scroll bar narrows the panel's column after the table is built, so the columns have to
    /// fit what is left rather than what was there.
    /// </summary>
    [Fact]
    public void TheTableFitsTheColumnTheScrollBarLeaves()
    {
        var panel = Laid(Typical, false, PanelDensity.Compact);
        var open = TableOf(panel).Bounds.Width;

        panel.ScrollContent(true);
        panel.Height = 400;
        PanelSampleRenderer.LayOut(panel, PanelSampleRenderer.CappedLayoutPasses);

        var table = TableOf(panel);
        Assert.Equal(open - PanelMetrics.ScrollBarWidthDip - PanelMetrics.ScrollBarGapDip, table.Bounds.Width);
        Assert.Equal(PanelMetrics.OutputColumnDip, table.ColumnDefinitions[2].ActualWidth);
    }

    // ── building the panels ─────────────────────────────────────────────────────────

    private static PanelContent Laid(string name, bool light, PanelDensity? density = null)
    {
        UseRealFonts();

        var state = SampleStates.All.Single(s => s.Name == name);
        var panel = new PanelContent(new LinuxPanelTheme(light)) { Density = density ?? PanelDensity.Normal };
        panel.Populate(state.Snapshot, state.Stats, state.Account, state.Plan, state.Scope,
            SampleStates.Now, state.Boost, SampleStates.Zone, state.CachedUsage, state.View);
        PanelSampleRenderer.LayOut(panel);
        return panel;
    }

    private static PanelLocalTotalsView Totals(string name)
    {
        var state = SampleStates.All.Single(s => s.Name == name);
        return PanelSections.LocalTotals(state.Stats, state.Snapshot, SampleStates.Zone, state.View);
    }

    private static PanelLocalFooterView Footer(string name)
    {
        var state = SampleStates.All.Single(s => s.Name == name);
        return PanelSections.LocalFooter(state.Scope, state.View);
    }

    private static bool HasNote(string name)
    {
        var state = SampleStates.All.Single(s => s.Name == name);
        return state.Scope is { } scope && PanelSections.ScopeNote(state.Stats, state.Snapshot, scope) is not null;
    }

    /// <summary>The table: the one grid whose data columns have pixel widths beside a star column.</summary>
    private static Grid TableOf(Control panel) =>
        panel.GetVisualDescendants().OfType<Grid>()
            .Single(g => g.ColumnDefinitions.Count >= 3 && g.ColumnDefinitions[1].Width.IsAbsolute);

    private static StackPanel BlockOf(Grid table) => Assert.IsType<StackPanel>(table.GetVisualParent());

    private static TextBlock Cell(Grid table, int row, int column) =>
        table.Children.OfType<TextBlock>().Single(t => Grid.GetRow(t) == row && Grid.GetColumn(t) == column);

    /// <summary>The words in a cell, whether they sit straight in the grid or inside a pill.</summary>
    private static string? TextIn(Grid table, int row, int column) =>
        table.Children.Where(c => c is not Border { Child: null } && Grid.GetRow(c) == row && Grid.GetColumn(c) == column)
            .SelectMany(c => c is TextBlock text ? [text] : c.GetVisualDescendants().OfType<TextBlock>())
            .Single().Text;

    private static double CentreOf(Grid table, int column)
    {
        var right = table.Bounds.Width - (table.ColumnDefinitions.Count - 1 - column) * PanelMetrics.OutputColumnDip;
        return right - PanelMetrics.OutputColumnDip / 2;
    }

    private static TextBlock Words(Control root, string text) =>
        root.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == text);

    private static Color Hex(string key, bool light) => Color.Parse(PanelPalette.Get(key, light));

    private static Color Colour(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    private static bool IsColour(IBrush? brush, string key) =>
        brush is ISolidColorBrush solid && solid.Color == Hex(key, false);

    /// <summary>The font setup <see cref="PanelShortScreenTests"/> runs once per process, through its own lock.</summary>
    private static void UseRealFonts() =>
        typeof(PanelShortScreenTests)
            .GetMethod("UseRealFonts", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, null);
}
