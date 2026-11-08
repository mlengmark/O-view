using System.Reflection;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using OView.App.Rendering;
using OView.Core.Models;
using OView.Linux.Panel;
using OView.Presentation.Samples;
using Path = Avalonia.Controls.Shapes.Path;

namespace OView.Linux.Tests;

/// <summary>
/// The no-clipping check on this head, the counterpart of the Windows panel's
/// <c>PanelEdgeCheck</c>: every render the verification images are taken from, in both themes,
/// is asked whether anything crosses the panel's edge, whether any line of text trims or is cut
/// to a slot narrower than it, or a table cell wraps, and whether the renders themselves are the ones they claim to be.
///
/// <para>A render that clips looks like a render. An element drawn past the panel's edge, or a
/// figure in a column too narrow for it, is in the laid-out tree whether or not the PNG makes it
/// easy to see, so the question is asked of the tree. Every state is the longest the formatters
/// produce for it; <c>longest-figures</c> and <c>longest-figures-models</c> carry the widest
/// figures and the widest model id.</para>
///
/// <para>One exemption, a column rather than a state: a model's name in the model table trims
/// when its id is longer than the column, and only while the full id is in a tooltip, as on
/// Windows.</para>
///
/// <para><b>The doughnuts' hover cards are not in these trees.</b> They are popups, which a tree
/// with no window cannot place, so what is pinned here is the side they open to and the room
/// that side has; where they land in pixels is measured in a real window, which is a check this
/// project's test run has no desktop for.</para>
/// </summary>
public class PanelClippingTests
{
    /// <summary>How far past an edge or a slot still counts as on it, for layout rounding.</summary>
    private const double ToleranceDip = 0.5;

    /// <summary>
    /// How much shorter than its line a row may be: a compact row is a whole number of pixels, so
    /// a line of 16.7 sits in a row of 16 with its glyphs unclipped. A line cut by its slot is
    /// short by a whole line, which is more than a pixel.
    /// </summary>
    private const double RowRoundingDip = 1;

    private const string Compact = "compact";

    private const string Short = "short";

    private const string Longest = "longest-figures-models";

    // ── what is checked ─────────────────────────────────────────────────────────────

    /// <summary>Every sample state, then the two renders of a state that only the renderer makes, each in both themes.</summary>
    public static TheoryData<string, bool> EveryRender()
    {
        var data = new TheoryData<string, bool>();
        foreach (var light in new[] { false, true })
        {
            foreach (var name in SampleStates.All.Select(s => s.Name).Append(Compact).Append(Short))
            {
                data.Add(name, light);
            }
        }

        return data;
    }

    public static TheoryData<string, bool> EveryModelRender()
    {
        var data = new TheoryData<string, bool>();
        foreach (var light in new[] { false, true })
        {
            foreach (var state in SampleStates.All.Where(s => s.View.Face == PanelFace.Models))
            {
                data.Add(state.Name, light);
            }
        }

        return data;
    }

    /// <summary>Nothing drawn crosses the panel's sides, or its top and foot where the panel is not scrolling.</summary>
    [Theory]
    [MemberData(nameof(EveryRender))]
    public void NothingIsDrawnPastThePanelsEdge(string render, bool light)
    {
        var panel = Laid(render, light);

        NoneOf(CrossingTheEdge(panel));
    }

    /// <summary>No line of text is trimmed, and none is wider or taller than the slot it was given.</summary>
    [Theory]
    [MemberData(nameof(EveryRender))]
    public void NoLineOfTextTrimsOrIsCutToItsSlot(string render, bool light)
    {
        var panel = Laid(render, light);

        NoneOf(TextThatDoesNotFit(panel));
    }

    // ── the check can fail ──────────────────────────────────────────────────────────

    /// <summary>
    /// The edge check is not vacuous: an element pushed past the panel's side is reported, so
    /// the pass above means something.
    /// </summary>
    [Fact]
    public void AnElementPushedPastThePanelsSideIsReported()
    {
        var panel = Laid("typical", false);
        var line = panel.GetVisualDescendants().OfType<Border>().First(b => b.Bounds.Height == 1);

        line.HorizontalAlignment = HorizontalAlignment.Left;
        line.Width = 2 * PanelMetrics.PanelWidthDip;
        PanelSampleRenderer.LayOut(panel);

        Assert.NotEmpty(CrossingTheEdge(panel));
    }

    /// <summary>
    /// And neither is the text check: a column made too narrow for the figures in it, which is
    /// what a clip reintroduced on purpose looks like, leaves a figure wider than its slot.
    /// </summary>
    [Fact]
    public void AColumnTooNarrowForItsFiguresIsReported()
    {
        var panel = Laid("longest-figures", false);
        var table = panel.GetVisualDescendants().OfType<Grid>()
            .Single(g => g.ColumnDefinitions.Count == 5 && g.ColumnDefinitions[1].Width.IsAbsolute);

        table.ColumnDefinitions[1].Width = new GridLength(8);
        PanelSampleRenderer.LayOut(panel);

        Assert.NotEmpty(TextThatDoesNotFit(panel));
    }

    /// <summary>
    /// A model name may trim only while its full id is in a tooltip: with the tooltip taken
    /// away, the same trim is a miss.
    /// </summary>
    [Fact]
    public void ATrimmedModelNameWithNoTooltipIsReported()
    {
        var panel = Laid(Longest, false);
        NoneOf(TextThatDoesNotFit(panel));

        foreach (var cell in ModelTable(panel)!.Children.OfType<DockPanel>())
        {
            ToolTip.SetTip(cell, null);
        }

        Assert.Contains(TextThatDoesNotFit(panel), miss => miss.Contains("no tooltip", StringComparison.Ordinal));
    }

    // ── the renders ─────────────────────────────────────────────────────────────────

    /// <summary>The short render is the tallest state that shows the model face, found by measuring every one.</summary>
    [Fact]
    public void TheShortStateIsTheTallestOfTheStatesThatShowTheModelFace()
    {
        var shown = SampleStates.All.Where(s => s.View.Face == PanelFace.Models).ToList();
        var heights = shown.ToDictionary(s => s.Name, s => Built(s, false, PanelDensity.Normal).NaturalHeightDip());

        Assert.Contains(PanelSampleRenderer.ShortState, heights.Keys);
        Assert.Equal(heights.Values.Max(), heights[PanelSampleRenderer.ShortState]);
    }

    /// <summary>The short render is capped at the short work area and scrolls inside it, so it still proves the scrolling.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheShortRenderIsCappedAtTheWorkAreaAndScrolls(bool light)
    {
        var panel = Laid(Short, light);

        Assert.True(panel.Scrolls);
        Assert.True(panel.ScrollBar.IsVisible);
        Assert.Equal(SampleStates.ShortWorkAreaDip, panel.Bounds.Height);
        Assert.True(panel.Viewport.Extent.Height > panel.Viewport.Bounds.Height);
    }

    /// <summary>The compact render is the state the Windows panel uses for it, at the tighter density.</summary>
    [Fact]
    public void TheCompactRenderIsTheWindowsStateAtTheTighterDensity()
    {
        var windows = SampleStates.All.Single(s => s.Name == "typical-overall");
        var normal = Built(windows, false, PanelDensity.Normal).NaturalHeightDip();
        var compact = Built(PanelSampleRenderer.CompactSample, false, PanelDensity.Compact).NaturalHeightDip();

        Assert.Equal(windows, PanelSampleRenderer.CompactSample);
        Assert.True(compact < normal, $"compact measures {compact:0} against {normal:0} at the normal density");
    }

    /// <summary>
    /// The committed images follow the renders: the compact one is added, the short one has moved
    /// to the state it now photographs, and the old name is gone.
    /// </summary>
    [Fact]
    public void TheCommittedImagesHaveTheNamesTheRendersWrite()
    {
        var golden = System.IO.Path.Combine(RepositoryRoot(), "tests", "golden", "linux");

        foreach (var theme in new[] { "dark", "light" })
        {
            Assert.True(File.Exists(System.IO.Path.Combine(golden, $"panel-typical-compact-{theme}.png")));
            Assert.True(File.Exists(System.IO.Path.Combine(golden, $"panel-{PanelSampleRenderer.ShortState}-short-{theme}.png")));
            Assert.False(File.Exists(System.IO.Path.Combine(golden, $"panel-offplan-on-short-{theme}.png")));
        }
    }

    // ── the model face ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A window with no model to break down: the face is still the block's, with its headings and
    /// no row under them, and nothing in it crosses the panel or trims.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AModelFaceWithNoModelsDrawsNoRowsAndStaysInsideThePanel(bool light)
    {
        var typical = SampleStates.All.Single(s => s.Name == "typical-models");
        var empty = typical with { Stats = typical.Stats with { ModelsToday = [], Models31Days = [] } };

        var panel = Built(empty, light, PanelDensity.Normal);
        PanelSampleRenderer.LayOut(panel);
        var table = ModelTable(panel);

        Assert.NotNull(table);
        Assert.Equal(2, table.RowDefinitions.Count);
        Assert.Empty(table.Children.OfType<DockPanel>());
        NoneOf(CrossingTheEdge(panel));
        NoneOf(TextThatDoesNotFit(panel));
    }

    /// <summary>
    /// The 31-day doughnut is on the right, so its cards open towards the middle of the panel and
    /// have the whole width left of the ring to do it in.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryModelRender))]
    public void TheThirtyOneDayCardsOpenLeftWithRoomToSpare(string state, bool light)
    {
        var panel = Laid(state, light);
        var ring = Rings(panel)[1];
        var left = ring.TranslatePoint(default, panel)!.Value.X;

        foreach (var arc in ring.Children.OfType<Path>())
        {
            var card = Assert.IsType<Border>(ToolTip.GetTip(arc));
            card.Measure(Size.Infinity);

            Assert.Equal(PlacementMode.Left, ToolTip.GetPlacement(arc));
            Assert.True(card.DesiredSize.Width <= left, $"a card {card.DesiredSize.Width:0} wide has {left:0} to open into in {state}");
        }
    }

    // ── the two questions ───────────────────────────────────────────────────────────

    /// <summary>Fails with every miss listed, which is what says which element and how far.</summary>
    private static void NoneOf(List<string> misses) =>
        Assert.True(misses.Count == 0, string.Join(Environment.NewLine, misses));

    /// <summary>Everything drawn that crosses the panel's edge, and where.</summary>
    private static List<string> CrossingTheEdge(PanelContent panel)
    {
        var misses = new List<string>();
        Walk(panel, element =>
        {
            var origin = element.TranslatePoint(default, panel);
            if (origin is null || element.Bounds.Width <= 0 || element.Bounds.Height <= 0)
            {
                return;
            }

            var (left, top) = (origin.Value.X, origin.Value.Y);
            var (right, bottom) = (left + element.Bounds.Width, top + element.Bounds.Height);

            // A scroller's content runs past the foot by design, so there only the sides are the edge.
            var inScroller = element.GetVisualAncestors().Any(a => a is ScrollContentPresenter);
            var crosses = left < -ToleranceDip || right > panel.Bounds.Width + ToleranceDip
                          || (!inScroller && (top < -ToleranceDip || bottom > panel.Bounds.Height + ToleranceDip));

            if (crosses)
            {
                misses.Add($"{Describe(element)} crosses the edge: x {left:0.#}..{right:0.#}, "
                           + $"y {top:0.#}..{bottom:0.#} in {panel.Bounds.Width:0.#} x {panel.Bounds.Height:0.#}");
            }
        });

        return misses;
    }

    /// <summary>
    /// Every line of text that trims, or is wider (or, where it wraps, taller) than the slot it was
    /// given. <b>Measures the text it checks</b>, so the panel is not to be laid out and read
    /// again after it.
    /// </summary>
    private static List<string> TextThatDoesNotFit(PanelContent panel)
    {
        var misses = new List<string>();
        var modelTable = ModelTable(panel);
        var texts = new List<TextBlock>();

        Walk(panel, element =>
        {
            if (element is TextBlock { Text.Length: > 0 } text)
            {
                texts.Add(text);
            }
        });

        foreach (var text in texts)
        {
            var cell = text.GetVisualParent() as DockPanel;
            var byDesign = cell is not null && ReferenceEquals(cell.GetVisualParent(), modelTable);
            var trims = text.TextLayout.TextLines.Any(l => l.HasCollapsed);

            if (byDesign && (trims || Wanted(text) is not null))
            {
                if (ToolTip.GetTip(cell!) is null)
                {
                    misses.Add($"{Describe(text)} trims with no tooltip behind it");
                }

                continue;
            }

            if (trims)
            {
                misses.Add($"{Describe(text)} trims");
            }
            else if (text.GetVisualParent() is Grid table && table.ColumnDefinitions.Any(c => c.Width.IsAbsolute) && text.TextLayout.TextLines.Count > 1)
            {
                // A table's label or figure is one line. Its row grows to hold a second, so nothing
                // is cut, but a figure broken across lines is as unreadable as a trimmed one.
                misses.Add($"{Describe(text)} wraps in its cell");
            }
            else if (Wanted(text) is { } cut)
            {
                misses.Add($"{Describe(text)} is cut to its slot: {cut}");
            }
        }

        return misses;
    }

    /// <summary>How the text falls short of its slot, or null when it fits.</summary>
    private static string? Wanted(TextBlock text)
    {
        var margin = text.Margin;

        if (text.TextWrapping == TextWrapping.NoWrap)
        {
            text.Measure(Size.Infinity);
            var wide = text.DesiredSize.Width - margin.Left - margin.Right;
            return wide > text.Bounds.Width + ToleranceDip ? $"wants {wide:0.#} wide, has {text.Bounds.Width:0.#}" : null;
        }

        text.Measure(new Size(text.Bounds.Width + margin.Left + margin.Right, double.PositiveInfinity));
        var tall = text.DesiredSize.Height - margin.Top - margin.Bottom;
        return tall > text.Bounds.Height + RowRoundingDip ? $"wants {tall:0.#} high, has {text.Bounds.Height:0.#}" : null;
    }

    /// <summary>Every control that is showing, below something that is not hidden or faded out.</summary>
    private static void Walk(Visual parent, Action<Control> visit)
    {
        foreach (var child in parent.GetVisualChildren())
        {
            if (child is not Control { IsVisible: true, Opacity: > 0 } element)
            {
                continue;
            }

            visit(element);
            Walk(element, visit);
        }
    }

    private static string Describe(Control element)
    {
        var what = element is TextBlock { Text: { } text } ? text : element.Name ?? string.Empty;
        what = what.Length > 40 ? what[..40] + "..." : what;
        return what.Length > 0 ? $"{element.GetType().Name} \"{what}\"" : element.GetType().Name;
    }

    // ── building the renders ────────────────────────────────────────────────────────

    private static SampleState State(string name) => SampleStates.All.Single(s => s.Name == name);

    private static PanelContent Built(SampleState state, bool light, PanelDensity density)
    {
        UseRealFonts();

        return PanelSampleRenderer.Build(state, light, density);
    }

    /// <summary>A render as the verification images draw it: laid out, and for the short one capped to the work area first.</summary>
    private static PanelContent Laid(string render, bool light)
    {
        UseRealFonts();

        switch (render)
        {
            case Short:
                var capped = PanelSampleRenderer.ShortPanel(light);
                PanelSampleRenderer.LayOut(capped, PanelSampleRenderer.CappedLayoutPasses);
                return capped;
            case Compact:
                var compact = Built(PanelSampleRenderer.CompactSample, light, PanelDensity.Compact);
                PanelSampleRenderer.LayOut(compact);
                return compact;
            default:
                var panel = Built(State(render), light, PanelDensity.Normal);
                PanelSampleRenderer.LayOut(panel);
                return panel;
        }
    }

    /// <summary>The model face's table, the one grid whose name cells are filled panels; null on the totals face.</summary>
    private static Grid? ModelTable(Control panel) =>
        panel.GetVisualDescendants().OfType<Grid>().SingleOrDefault(g => g.Children.OfType<DockPanel>().Any())
        ?? panel.GetVisualDescendants().OfType<Grid>().SingleOrDefault(g =>
            g.ColumnDefinitions.Count == 5 && g.ColumnDefinitions[1].Width.IsAbsolute && g.RowDefinitions.Count == 2);

    private static List<Canvas> Rings(Control panel) =>
        panel.GetVisualDescendants().OfType<Canvas>().Where(c => c.Children.OfType<Path>().Any()).ToList();

    private static void UseRealFonts() =>
        typeof(PanelShortScreenTests)
            .GetMethod("UseRealFonts", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, null);

    private static string RepositoryRoot() =>
        (string)typeof(PanelShortScreenTests)
            .GetMethod("RepositoryRoot", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, null)!;
}
