using System.Reflection;

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.VisualTree;
using OView.App.Rendering;
using OView.Core.Models;
using OView.Linux.Panel;
using OView.Presentation.Panel;
using OView.Presentation.Samples;
using OView.Presentation.Text;
using Path = Avalonia.Controls.Shapes.Path;

namespace OView.Linux.Tests;

/// <summary>
/// The local block's model face on this head: section 3.4's measurements, that every model is a
/// row wearing one colour everywhere, that the flip tag and the switch hand the choice back, and
/// that a section's card stays inside the panel.
///
/// <para>The trees are real and laid out with a real font, by hand, as the verification renders
/// are. Avalonia raises no pointer events without a windowing subsystem, so a press here is the
/// routed event a click raises.</para>
/// </summary>
public class PanelModelFaceTests
{
    private const string Typical = "typical-models";

    private const string Overall = "typical-models-overall";

    private const string OffPlan = "offplan-on-models";

    private const string Longest = "longest-figures-models";

    private const string Totals = "typical";

    private const string StillCapped = "offplan-on";

    private const double CappedHeight = 400;

    public static TheoryData<string> ModelStates() => [Typical, Overall, OffPlan, Longest];

    public static TheoryData<string, bool, bool> ModelStatesThemesAndDensities()
    {
        var data = new TheoryData<string, bool, bool>();
        foreach (var state in new[] { Typical, Overall, OffPlan, Longest })
        {
            foreach (var light in new[] { false, true })
            {
                data.Add(state, light, false);
                data.Add(state, light, true);
            }
        }

        return data;
    }

    // ── the doughnuts ───────────────────────────────────────────────────────────────

    /// <summary>The rings' size, stroke and radius, and the type in and under them, are the shared ones.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void TheDoughnutsAreTheSharedMeasurements(bool light, bool compact)
    {
        var density = compact ? PanelDensity.Compact : PanelDensity.Normal;
        var panel = Laid(Typical, light, density);
        var face = Face(Typical);
        var rings = Rings(panel);

        Assert.Equal(2, rings.Count);
        foreach (var (ring, view) in rings.Zip([face.Today, face.Window]))
        {
            Assert.Equal(new Size(PanelMetrics.DoughnutSizeDip, PanelMetrics.DoughnutSizeDip), ring.Bounds.Size);

            foreach (var arc in Arcs(ring))
            {
                Assert.Equal(PanelMetrics.DoughnutStrokeDip, arc.StrokeThickness);
                Assert.Null(arc.Fill);
                Assert.Equal(ring.Bounds.Size, arc.Bounds.Size);
                Assert.Equal(PanelMetrics.DoughnutRingRadiusDip, ArcOf(arc).Size.Width, 6);
            }

            var layers = Assert.IsType<Grid>(ring.GetVisualParent());
            var total = Assert.Single(layers.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == view.Total);
            var unit = Assert.Single(layers.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == view.Unit);

            Assert.Equal(PanelMetrics.DoughnutCentreTotalFontSize, total.FontSize);
            Assert.Equal(FontWeight.SemiBold, total.FontWeight);
            Assert.Equal(Hex("TextPrimary", light), Colour(total.Foreground));
            Assert.Equal(PanelMetrics.DoughnutCentreUnitFontSize, unit.FontSize);
            Assert.Equal(Hex("TextMuted", light), Colour(unit.Foreground));

            var middle = ring.TranslatePoint(new Point(ring.Bounds.Width / 2, ring.Bounds.Height / 2), panel)!.Value;
            Assert.Equal(middle.X, total.TranslatePoint(default, panel)!.Value.X + total.Bounds.Width / 2, 0.51);
            Assert.Equal(middle.X, unit.TranslatePoint(default, panel)!.Value.X + unit.Bounds.Width / 2, 0.51);

            var caption = Assert.Single(
                Assert.IsType<StackPanel>(layers.GetVisualParent()).Children.OfType<TextBlock>());
            Assert.Equal(view.Caption.ToUpperInvariant(), caption.Text);
            Assert.Equal(view.Caption, AutomationProperties.GetName(caption));
            Assert.Equal(PanelMetrics.DoughnutLabelFontSize, caption.FontSize);
            Assert.Equal(Hex("TextMuted", light), Colour(caption.Foreground));
        }

        var table = TableOf(panel);
        Assert.Equal(PanelMetrics.DoughnutsToTableGapDip * density.SpacingScale, table.Margin.Top, 6);
    }

    /// <summary>
    /// Clockwise from the top in the table's order: the first arc starts at twelve o'clock and
    /// each ends the shared gap before the next begins.
    /// </summary>
    [Theory]
    [MemberData(nameof(ModelStates))]
    public void TheSectionsRunClockwiseFromTheTopLeavingTheGap(string state)
    {
        var face = Face(state);

        foreach (var (ring, view) in Rings(Laid(state, false)).Zip([face.Today, face.Window]))
        {
            var arcs = Arcs(ring);
            Assert.Equal(view.Sections.Count, arcs.Count);

            if (arcs.Count < 2)
            {
                continue;
            }

            var centre = PanelMetrics.DoughnutSizeDip / 2;
            Assert.Equal(new Point(centre, centre - PanelMetrics.DoughnutRingRadiusDip), Figure(arcs[0]).StartPoint);

            for (var i = 0; i + 1 < arcs.Count; i++)
            {
                Assert.Equal(SweepDirection.Clockwise, ArcOf(arcs[i]).SweepDirection);

                var end = ArcOf(arcs[i]).Point;
                var next = Figure(arcs[i + 1]).StartPoint;
                var chord = Math.Sqrt(Math.Pow(end.X - next.X, 2) + Math.Pow(end.Y - next.Y, 2));
                Assert.Equal(PanelMetrics.DoughnutSectionGapDip, chord, 2);
            }
        }
    }

    // ── the table ───────────────────────────────────────────────────────────────────

    /// <summary>Every model in the store has a row with the words Presentation built, cell for cell.</summary>
    [Theory]
    [MemberData(nameof(ModelStates))]
    public void EveryModelIsARowWithExactlyWhatPresentationBuilt(string state)
    {
        var table = TableOf(Laid(state, false));
        var view = Face(state).Table;

        Assert.Equal(view.Rows.Count + 2, table.RowDefinitions.Count);
        Assert.Equal(7, view.Rows.Count);

        Assert.Equal(view.TodayGroup.ToUpperInvariant(), Cell(table, 0, 1).Text);
        Assert.Equal(view.WindowGroup.ToUpperInvariant(), Cell(table, 0, 3).Text);
        Assert.Equal(
            view.Headers.Select(h => h.ToUpperInvariant()),
            Enumerable.Range(0, view.Headers.Count).Select(c => Cell(table, 1, c).Text));

        foreach (var (row, gridRow) in view.Rows.Select((r, i) => (r, i + 2)))
        {
            Assert.Equal(row.Name, NameOf(table, gridRow).Text);
            Assert.Equal(row.Today.Tokens, Cell(table, gridRow, 1).Text);
            Assert.Equal(row.Today.Value, Cell(table, gridRow, 2).Text);
            Assert.Equal(row.Window.Tokens, Cell(table, gridRow, 3).Text);
            Assert.Equal(row.Window.Value, Cell(table, gridRow, 4).Text);
        }
    }

    /// <summary>One model, one colour: the same on its chip, its section in each ring, and its card.</summary>
    [Theory]
    [InlineData(Typical, false)]
    [InlineData(Typical, true)]
    [InlineData(Longest, false)]
    public void AModelWearsOneColourOnBothRingsAndItsChip(string state, bool light)
    {
        var panel = Laid(state, light);
        var face = Face(state);
        var table = TableOf(panel);

        foreach (var (row, i) in face.Table.Rows.Select((r, i) => (r, i)))
        {
            var key = row.Slot >= 0 ? $"Series{row.Slot + 1}" : $"SeriesTail{Math.Min(row.GreyStep + 1, 4)}";
            var chip = Assert.IsType<Border>(CellPanel(table, i + 2).Children[0]);

            Assert.Equal(new Size(8, 8), chip.Bounds.Size);
            Assert.Equal(new CornerRadius(2), chip.CornerRadius);
            Assert.Equal(6, chip.Margin.Right);
            Assert.Equal(Hex(key, light), Colour(chip.Background));
        }

        foreach (var (ring, view) in Rings(panel).Zip([face.Today, face.Window]))
        {
            foreach (var (arc, section) in Arcs(ring).Zip(view.Sections))
            {
                var row = face.Table.Rows[section.Row];
                var key = row.Slot >= 0 ? $"Series{row.Slot + 1}" : $"SeriesTail{Math.Min(row.GreyStep + 1, 4)}";

                Assert.Equal(Hex(key, light), Colour(arc.Stroke));
                Assert.Equal(Hex(key, light), Colour(CardOf(arc).GetLogicalDescendants().OfType<Border>().First().Background));
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheColumnsAreTheSharedWidthsAndTheNamesTakeTheRest(bool compact)
    {
        var table = TableOf(Laid(Typical, false, compact ? PanelDensity.Compact : PanelDensity.Normal));

        foreach (var column in Enumerable.Range(1, 4))
        {
            Assert.Equal(PanelMetrics.ModelColumnDip, table.ColumnDefinitions[column].ActualWidth);
        }

        Assert.Equal(table.Bounds.Width - 4 * PanelMetrics.ModelColumnDip, table.ColumnDefinitions[0].ActualWidth);
    }

    /// <summary>Tokens are the semibold primary figure, the estimate the quieter one, both centred in their columns.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheFiguresAreTheSharedSizesAndCentred(bool light)
    {
        var table = TableOf(Laid(Typical, light));

        foreach (var column in new[] { 1, 3 })
        {
            var tokens = Cell(table, 2, column);
            Assert.Equal(PanelMetrics.ModelTokensFontSize, tokens.FontSize);
            Assert.Equal(FontWeight.SemiBold, tokens.FontWeight);
            Assert.Equal(Hex("TextPrimary", light), Colour(tokens.Foreground));
            Assert.Equal("tnum", Assert.Single(tokens.FontFeatures!).Tag);
            Assert.Equal(CentreOf(table, column), tokens.Bounds.Center.X, 0.51);
        }

        foreach (var column in new[] { 2, 4 })
        {
            var value = Cell(table, 2, column);
            Assert.Equal(PanelMetrics.ModelValueFontSize, value.FontSize);
            Assert.Equal(FontWeight.Normal, value.FontWeight);
            Assert.Equal(Hex("TextSecondary", light), Colour(value.Foreground));
            Assert.Equal(CentreOf(table, column), value.Bounds.Center.X, 0.51);
        }
    }

    /// <summary>
    /// A group heading is centred over its pair with no line above it; the lines inside a pair
    /// start under it, and those at the left of a pair run from the top.
    /// </summary>
    [Fact]
    public void TheGroupHeadingsSitOverTheirPairsAndTheLinesStartUnderThem()
    {
        var table = TableOf(Laid(Typical, false));
        var today = Cell(table, 0, 1);

        Assert.Equal(2, Grid.GetColumnSpan(today));
        Assert.Equal(CentreOf(table, 1) + PanelMetrics.ModelColumnDip / 2, today.Bounds.Center.X, 0.51);
        Assert.Equal(2, Grid.GetColumnSpan(Cell(table, 0, 3)));

        var rowLines = table.Children.OfType<Border>().Where(b => b.Opacity == PanelMetrics.SoftLineOpacity).ToList();
        Assert.Equal(Enumerable.Range(2, Face(Typical).Table.Rows.Count), rowLines.Select(Grid.GetRow));

        var columns = table.Children.OfType<Border>().Where(b => b.Opacity == PanelMetrics.ColumnLineOpacity).ToList();
        var rows = table.RowDefinitions.Count;

        Assert.Equal([1, 2, 3, 4], columns.Select(Grid.GetColumn));
        Assert.Equal([0, 1, 0, 1], columns.Select(Grid.GetRow));
        Assert.Equal([rows, rows - 1, rows, rows - 1], columns.Select(Grid.GetRowSpan));
    }

    /// <summary>An unpriced model says so, in the muted colour, and never reads blank or zero.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnUnpricedModelReadsUnpricedInTheMutedColour(bool light)
    {
        var state = State(Typical);
        var unpriced = new ModelSlice("claude-brandnew-9", "claude-brandnew-9", 400);
        var stats = state.Stats with { Models31Days = [unpriced], ModelsToday = [unpriced] };
        var panel = new PanelContent(new LinuxPanelTheme(light));
        UseRealFonts();
        panel.Populate(state.Snapshot, stats, state.Account, state.Plan, state.Scope,
            SampleStates.Now, state.Boost, SampleStates.Zone, state.CachedUsage, state.View);
        PanelSampleRenderer.LayOut(panel);

        var row = Assert.Single(PanelSections.ModelFace(stats, state.View).Table.Rows);
        var cell = Cell(TableOf(panel), 2, 4);

        Assert.False(row.Window.ValueKnown);
        Assert.Equal("unpriced", cell.Text);
        Assert.Equal(Hex("TextMuted", light), Colour(cell.Foreground));
    }

    /// <summary>A long id trims only in the name column, and its row's tooltip carries it whole.</summary>
    [Fact]
    public void ALongIdTrimsOnlyInTheNameColumnWithTheFullIdInTheTooltip()
    {
        var table = TableOf(Laid(Longest, false));
        var rows = Face(Longest).Table.Rows;

        var trimmed = Enumerable.Range(0, rows.Count).Where(i => NameOf(table, i + 2).TextLayout.TextLines.Any(l => l.HasCollapsed)).ToList();
        Assert.NotEmpty(trimmed);

        foreach (var i in trimmed)
        {
            Assert.Equal(TextTrimming.CharacterEllipsis, NameOf(table, i + 2).TextTrimming);
            Assert.Equal(rows[i].Id, ToolTip.GetTip(CellPanel(table, i + 2)));
        }

        foreach (var figure in table.Children.OfType<TextBlock>().Where(t => Grid.GetColumn(t) > 0))
        {
            Assert.False(figure.TextLayout.TextLines.Any(l => l.HasCollapsed), $"[{figure.Text}] was trimmed");
        }
    }

    /// <summary>The cell has a fill even past the end of a short name, or the tooltip would answer only on the letters.</summary>
    [Fact]
    public void TheNameCellIsFilledSoThePointerFindsItBeyondTheName()
    {
        var table = TableOf(Laid(Typical, false));

        Assert.All(Enumerable.Range(2, Face(Typical).Table.Rows.Count), row => Assert.NotNull(CellPanel(table, row).Background));
    }

    // ── the flip tag ────────────────────────────────────────────────────────────────

    /// <summary>The tag is the top right of the block, flush with its edge, and says what a press gives.</summary>
    [Theory]
    [InlineData(Totals, false)]
    [InlineData(Totals, true)]
    [InlineData(Typical, false)]
    [InlineData(Typical, true)]
    public void TheTagIsAPillAtTheBlocksTopRightSayingWhereItGoes(string name, bool light)
    {
        var panel = Laid(name, light);
        var tag = TagOf(panel);
        var pill = Assert.IsType<Border>(Assert.Single(tag.GetVisualChildren()));
        var word = Assert.Single(pill.GetVisualDescendants().OfType<TextBlock>());
        var block = BlockOf(panel);

        var expected = TokenText.FlipTagLabel(State(name).View.Face);
        Assert.Equal(expected, word.Text);
        Assert.Equal(expected, tag.Content);
        Assert.Equal(PanelMetrics.FlipTagFontSize, word.FontSize);
        Assert.Equal(Hex("TextMuted", light), Colour(word.Foreground));

        Assert.Equal(Hex("TileBg", light), Colour(pill.Background));
        Assert.Equal(new CornerRadius(4), pill.CornerRadius);

        var mark = Assert.Single(pill.GetVisualDescendants().OfType<Path>());
        Assert.Equal(Hex("TextMuted", light), Colour(mark.Stroke));

        Assert.Equal(
            block.TranslatePoint(default, panel)!.Value.X + block.Bounds.Width,
            tag.TranslatePoint(default, panel)!.Value.X + tag.Bounds.Width,
            0.51);
        Assert.Equal(VerticalAlignment.Top, tag.VerticalAlignment);
        Assert.True(tag.Focusable);
    }

    /// <summary>A press keeps the rest of the view and asks for the other face, in one step each way.</summary>
    [Theory]
    [InlineData(Totals, PanelFace.Models)]
    [InlineData(Typical, PanelFace.Totals)]
    public void APressHandsBackTheOtherFaceKeepingTheRestOfTheView(string name, PanelFace expected)
    {
        var state = State(name);
        var kept = state.View with { BannerHidden = true, Measure = PanelMeasure.Overall };
        var panel = Laid(state, kept);
        PanelViewChoice? chosen = null;
        panel.ViewChosen += view => chosen = view;

        TagOf(panel).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(kept with { Face = expected }, chosen);
    }

    /// <summary>The rebuild the press asks for shows the other face: the model face's words, or the chart again.</summary>
    [Fact]
    public void ARebuildFromThePressShowsTheFaceAndTheLineUnderTheTitle()
    {
        var state = State(Totals);
        var panel = Laid(state, state.View);
        PanelViewChoice? chosen = null;
        panel.ViewChosen += view => chosen = view;

        TagOf(panel).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Populate(panel, state, chosen!.Value);
        PanelSampleRenderer.LayOut(panel);

        var models = PanelSections.ModelFace(state.Stats, chosen.Value);
        Assert.Equal(models.Subtitle, Words(panel, models.Subtitle).Text);
        Assert.Equal(2, Rings(panel).Count);
        Assert.Equal(TokenText.FlipTagLabel(PanelFace.Models), TagOf(panel).Content);

        TagOf(panel).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Populate(panel, state, chosen.Value);
        PanelSampleRenderer.LayOut(panel);

        Assert.Empty(Rings(panel));
        Assert.Same(PanelGraphLayoutTests.ChartOf(panel), BlockOf(panel).Children[1]);
    }

    /// <summary>
    /// The window's half, structural because it cannot be built here: the tag goes through the same
    /// handler the switch does, the one that stores the choice and re-fits.
    /// </summary>
    [Fact]
    public void TheTagGoesThroughTheHandlerThatStoresAndRefits()
    {
        var source = (string)typeof(PanelShortScreenTests)
            .GetMethod("WindowSourceWithoutComments", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, null)!;

        Assert.Contains("_content.ViewChosen += OnViewChosen", source, StringComparison.Ordinal);
    }

    // ── the switch on this face ─────────────────────────────────────────────────────

    /// <summary>Output | Overall changes the figures and leaves the reader on the model face.</summary>
    [Fact]
    public void TheSwitchAppliesToThisFace()
    {
        var state = State(Typical);
        var panel = Laid(state, state.View);
        PanelViewChoice? chosen = null;
        panel.ViewChosen += view => chosen = view;

        var overall = panel.GetVisualDescendants().OfType<RadioButton>().Last();
        overall.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(state.View with { Measure = PanelMeasure.Overall }, chosen);

        Populate(panel, state, chosen!.Value);
        PanelSampleRenderer.LayOut(panel);

        var face = PanelSections.ModelFace(state.Stats, chosen.Value);
        Assert.Equal(face.Subtitle, Words(panel, face.Subtitle).Text);
        Assert.Equal(face.Today.Total, Words(panel, face.Today.Total).Text);
        Assert.Equal(face.Today.Unit, Words(panel, face.Today.Unit).Text);
        Assert.Equal(face.Table.Rows[0].Window.Tokens, Cell(TableOf(panel), 2, 3).Text);
    }

    /// <summary>The caveat is the model face's own, and stands under the table as on the totals face.</summary>
    [Fact]
    public void TheCoverageCaveatIsTheFacesOwnUnderTheTable()
    {
        var panel = Laid(Typical, false);
        var caveat = Face(Typical).WindowCaveat;
        var block = BlockOf(panel);

        Assert.NotEmpty(caveat);
        Assert.Equal(block.Children.IndexOf(TableOf(panel)) + 1, block.Children.IndexOf(Words(block, caveat)));
    }

    // ── the hover card ──────────────────────────────────────────────────────────────

    /// <summary>A section's card carries the model's chip and name, the figure, and the share and value lines.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASectionsCardIsTheSharedShape(bool light)
    {
        var face = Face(Typical);

        foreach (var (ring, view) in Rings(Laid(Typical, light)).Zip([face.Today, face.Window]))
        {
            foreach (var (arc, section) in Arcs(ring).Zip(view.Sections))
            {
                var card = CardOf(arc);
                var words = card.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();

                Assert.Equal(Hex("TooltipBg", light), Colour(card.Background));
                Assert.Equal(Hex("TooltipBorder", light), Colour(card.BorderBrush));
                Assert.Equal(
                    [face.Table.Rows[section.Row].Name, section.Figure, $"{section.ShareLine}\n{section.ValueLine}"],
                    words);
            }
        }
    }

    /// <summary>The right-hand doughnut's cards open towards the middle of the panel; the left-hand one's follow the pointer.</summary>
    [Fact]
    public void TheThirtyOneDayCardsOpenLeftAndTodaysFollowThePointer()
    {
        var rings = Rings(Laid(Typical, false));

        Assert.All(Arcs(rings[0]), arc => Assert.Equal(PlacementMode.Pointer, ToolTip.GetPlacement(arc)));
        Assert.All(Arcs(rings[1]), arc => Assert.Equal(PlacementMode.Left, ToolTip.GetPlacement(arc)));
    }

    /// <summary>
    /// The widest card, measured against the room each doughnut has. The left doughnut's card opens
    /// at the pointer, so it starts at worst at the ring's far corner; the right one's opens beside
    /// the ring's left edge, centred on it.
    /// </summary>
    [Theory]
    [MemberData(nameof(ModelStatesThemesAndDensities))]
    public void EveryCardEndsInsideThePanel(string state, bool light, bool compact)
    {
        var panel = Laid(state, light, compact ? PanelDensity.Compact : PanelDensity.Normal);
        var rings = Rings(panel);

        var left = rings[0].TranslatePoint(default, panel)!.Value;
        foreach (var arc in Arcs(rings[0]))
        {
            var card = Measured(arc);
            var pointerBelow = left.Y + rings[0].Bounds.Height + ToolTip.GetVerticalOffset(arc);

            Assert.True(left.X + rings[0].Bounds.Width + card.Width <= panel.Bounds.Width, $"a card from the left ring ends past the panel in {state}");
            Assert.True(pointerBelow + card.Height <= panel.Bounds.Height, $"a card from the left ring ends below the panel in {state}");
        }

        var right = rings[1].TranslatePoint(default, panel)!.Value;
        foreach (var arc in Arcs(rings[1]))
        {
            var card = Measured(arc);
            var middle = right.Y + rings[1].Bounds.Height / 2;

            Assert.True(right.X - card.Width >= 0, $"a card from the right ring starts before the panel in {state}");
            Assert.True(middle - card.Height / 2 >= 0, $"a card from the right ring starts above the panel in {state}");
            Assert.True(middle + card.Height / 2 <= panel.Bounds.Height, $"a card from the right ring ends below the panel in {state}");
        }
    }

    // ── a capped panel ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The tag is above the face that changes height, so a press must not move it: anchored to
    /// the foot as the switch is, it is carried out of the view by exactly what the face gained.
    /// </summary>
    [Theory]
    [InlineData(StillCapped, PanelFace.Models)]
    [InlineData(OffPlan, PanelFace.Totals)]
    public void OnACappedPanelThePressLeavesTheTagWhereItWas(string name, PanelFace to)
    {
        var state = State(name);
        var panel = Capped(state, state.View);
        var tag = TagOf(panel);

        panel.Viewport.Offset = panel.Viewport.Offset.WithY(tag.TranslatePoint(default, panel.Viewport)!.Value.Y - 40);
        PanelSampleRenderer.LayOut(panel, PanelSampleRenderer.CappedLayoutPasses);

        var before = TagOf(panel).TranslatePoint(default, panel.Viewport)!.Value.Y;
        Assert.InRange(before, 0, panel.Viewport.Bounds.Height);

        PanelViewChoice? chosen = null;
        panel.ViewChosen += view => chosen = view;
        var fromFoot = panel.ScrollFromFoot;

        TagOf(panel).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(to, chosen!.Value.Face);

        panel.ReturnToNaturalLayout();
        panel.Height = double.NaN;
        panel.Density = PanelDensity.Compact;
        Populate(panel, state, chosen.Value);

        var height = PanelFit.Resolve(panel.NaturalHeightDip(), CappedHeight).WindowHeightDip;
        panel.ScrollContent(true);
        panel.Height = height;
        PanelSampleRenderer.LayOut(panel, PanelSampleRenderer.CappedLayoutPasses);

        panel.ScrollToFromFoot(fromFoot);
        PanelSampleRenderer.LayOut(panel, PanelSampleRenderer.CappedLayoutPasses);

        var after = TagOf(panel).TranslatePoint(default, panel.Viewport)!.Value.Y;
        Assert.True(panel.Viewport.Extent.Height > panel.Viewport.Bounds.Height, "the panel stopped scrolling, so the test proves nothing");
        Assert.InRange(after, 0, panel.Viewport.Bounds.Height - TagOf(panel).Bounds.Height);
        Assert.Equal(before, after, 1);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────

    private static SampleState State(string name) => SampleStates.All.Single(s => s.Name == name);

    private static PanelModelFaceView Face(string name) =>
        PanelSections.ModelFace(State(name).Stats, State(name).View);

    private static PanelContent Build(SampleState state, PanelViewChoice view, bool light, PanelDensity? density)
    {
        UseRealFonts();

        var panel = new PanelContent(new LinuxPanelTheme(light)) { Density = density ?? PanelDensity.Normal };
        Populate(panel, state, view);
        return panel;
    }

    private static void Populate(PanelContent panel, SampleState state, PanelViewChoice view) =>
        panel.Populate(state.Snapshot, state.Stats, state.Account, state.Plan, state.Scope,
            SampleStates.Now, state.Boost, SampleStates.Zone, state.CachedUsage, view);

    private static PanelContent Laid(string name, bool light, PanelDensity? density = null)
    {
        var panel = Build(State(name), State(name).View, light, density);
        PanelSampleRenderer.LayOut(panel);
        return panel;
    }

    private static PanelContent Laid(SampleState state, PanelViewChoice view, bool light = false)
    {
        var panel = Build(state, view, light, null);
        PanelSampleRenderer.LayOut(panel);
        return panel;
    }

    private static PanelContent Capped(SampleState state, PanelViewChoice view)
    {
        var panel = Build(state, view, false, PanelDensity.Compact);
        var fit = PanelFit.Resolve(panel.NaturalHeightDip(), CappedHeight);
        Assert.True(fit.MustScroll);

        panel.ScrollContent(true);
        panel.Height = fit.WindowHeightDip;
        PanelSampleRenderer.LayOut(panel, PanelSampleRenderer.CappedLayoutPasses);
        return panel;
    }

    private static Grid TableOf(Control panel) =>
        panel.GetVisualDescendants().OfType<Grid>()
            .Single(g => g.RowDefinitions.Count > 0 && g.ColumnDefinitions.Count == 5 && g.ColumnDefinitions[1].Width.IsAbsolute);

    private static StackPanel BlockOf(Control panel) =>
        Assert.IsType<StackPanel>(TagOf(panel).GetVisualParent()!.GetVisualParent());

    private static Button TagOf(Control panel) =>
        panel.GetVisualDescendants().OfType<Button>().Single(b => b is not RadioButton);

    private static List<Canvas> Rings(Control panel) => panel.GetVisualDescendants().OfType<Canvas>()
        .Where(c => c.Children.OfType<Path>().Any()).ToList();

    private static List<Path> Arcs(Canvas ring) => ring.Children.OfType<Path>().ToList();

    private static PathFigure Figure(Path arc) => Assert.Single(Assert.IsType<PathGeometry>(arc.Data).Figures!);

    private static ArcSegment ArcOf(Path arc) => Assert.IsType<ArcSegment>(Assert.Single(Figure(arc).Segments!));

    private static Border CardOf(Path arc) => Assert.IsType<Border>(ToolTip.GetTip(arc));

    private static Size Measured(Path arc)
    {
        var card = CardOf(arc);
        card.Measure(Size.Infinity);
        return card.DesiredSize;
    }

    private static TextBlock Cell(Grid table, int row, int column) =>
        table.Children.OfType<TextBlock>().Single(t => Grid.GetRow(t) == row && Grid.GetColumn(t) == column);

    private static DockPanel CellPanel(Grid table, int row) =>
        table.Children.OfType<DockPanel>().Single(d => Grid.GetRow(d) == row);

    private static TextBlock NameOf(Grid table, int row) =>
        Assert.Single(CellPanel(table, row).Children.OfType<TextBlock>());

    private static double CentreOf(Grid table, int column)
    {
        var right = table.Bounds.Width - (table.ColumnDefinitions.Count - 1 - column) * PanelMetrics.ModelColumnDip;
        return right - PanelMetrics.ModelColumnDip / 2;
    }

    private static TextBlock Words(Control root, string text) =>
        root.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == text);

    private static Color Hex(string key, bool light) => Color.Parse(PanelPalette.Get(key, light));

    private static Color Colour(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    private static void UseRealFonts() =>
        typeof(PanelShortScreenTests)
            .GetMethod("UseRealFonts", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, null);
}
