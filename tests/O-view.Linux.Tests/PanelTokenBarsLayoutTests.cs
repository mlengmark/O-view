using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;
using OView.App.Rendering;
using OView.Core.Models;
using OView.Core.Providers.Jsonl;
using OView.Linux.Panel;
using OView.Presentation.Panel;
using OView.Presentation.Samples;

namespace OView.Linux.Tests;

/// <summary>
/// The width every segment of the two "Tokens used" bars gets, in the two ways the panel is
/// laid out: by a window, and offscreen for the sample images.
///
/// <para><b>What went wrong.</b> The bars size their columns when the track gets its width,
/// which happens during the arrange. A window's layout manager goes back to the track and lays
/// it out again. Offscreen nothing did, so every sample image drew four one-pixel segments at
/// the left of the track, while a window drew the full bar. The stored images matched, so every
/// check passed.</para>
///
/// <para>A test process cannot have a real layout manager: Avalonia only builds one for a
/// top level, and a top level needs a render loop this process has no windowing subsystem to
/// supply. <see cref="LayOutLikeAWindow"/> repeats the manager's steps on the public layout
/// API instead.</para>
/// </summary>
public class PanelTokenBarsLayoutTests
{
    private const string BarsState = "typical";

    /// <summary>A 400 DIP panel, less its border and padding on each side.</summary>
    private const double TrackWidthDip = 366;

    /// <summary>
    /// Output, input, cache write and cache read in the sample's two windows, in whole pixels
    /// because the grid rounds each column. Input sits on the 3 DIP floor.
    /// </summary>
    private static readonly double[] SegmentWidthsDip = [4, 3, 37, 323];

    [Fact]
    public void AWindowGivesEverySegmentItsWidth()
    {
        var panel = Build(BarsState);

        LayOutLikeAWindow(panel);

        AssertBars(panel);
    }

    [Fact]
    public void TheOffscreenRenderGivesEverySegmentTheWidthAWindowDoes()
    {
        var panel = Build(BarsState);

        PanelSampleRenderer.LayOut(panel);

        AssertBars(panel);
    }

    /// <summary>
    /// Every state, against the widths the shared geometry asks for, because the shares differ
    /// and only one state's widths are written down above. A state with no tokens draws no bars.
    /// </summary>
    [Theory]
    [MemberData(nameof(StateNames))]
    public void TheOffscreenRenderFillsTheTrackInProportion(string name)
    {
        var state = SampleStates.All.Single(s => s.Name == name);
        var panel = Build(name);

        PanelSampleRenderer.LayOut(panel);

        var view = PanelSections.TokenBars(state.Stats, state.Scope ?? TranscriptScopeReport.Inspect());
        var tracks = Tracks(panel);
        Assert.Equal(view.Show ? 2 : 0, tracks.Count);
        if (!view.Show)
        {
            return;
        }

        AssertProportional(tracks[0], view.Today.Composition);
        AssertProportional(tracks[1], view.Window31.Composition);
    }

    private static void AssertProportional(Grid track, TokenComposition composition)
    {
        var wanted = TokenBarGeometry.Segments(composition, track.Bounds.Width);
        var drawn = track.Children.Select(c => c.Bounds).ToList();

        Assert.Equal(wanted.Count, drawn.Count);
        var edge = 0d;
        for (var segment = 0; segment < wanted.Count; segment++)
        {
            // Within a pixel, because the grid rounds each column to whole pixels.
            Assert.InRange(drawn[segment].Width, wanted[segment].Width - 1, wanted[segment].Width + 1);
            Assert.Equal(edge, drawn[segment].X);
            edge = drawn[segment].Right;
        }

        Assert.InRange(edge, track.Bounds.Width - 1, track.Bounds.Width + 1);
    }

    /// <summary>
    /// The narrower column a scroll bar leaves, arriving after the bars have already been cut
    /// once for a wider track.
    /// </summary>
    [Fact]
    public void TheCappedRenderCutsTheBarsForTheTrackTheScrollBarLeaves()
    {
        var state = SampleStates.All.Single(s => s.Name == PanelSampleRenderer.ShortState);
        var panel = Build(state.Name, PanelDensity.Compact);
        PanelSampleRenderer.LayOut(panel);

        panel.Width -= PanelMetrics.ScrollBarWidthDip + PanelMetrics.ScrollBarGapDip;
        PanelSampleRenderer.LayOut(panel, PanelSampleRenderer.CappedLayoutPasses);

        var view = PanelSections.TokenBars(state.Stats, state.Scope ?? TranscriptScopeReport.Inspect());
        var tracks = Tracks(panel);
        Assert.Equal(2, tracks.Count);
        AssertProportional(tracks[0], view.Today.Composition);
        AssertProportional(tracks[1], view.Window31.Composition);
    }

    public static TheoryData<string> StateNames()
    {
        var names = new TheoryData<string>();
        foreach (var state in SampleStates.All)
        {
            names.Add(state.Name);
        }

        return names;
    }

    private static void AssertBars(Control panel)
    {
        var tracks = Tracks(panel);
        Assert.Equal(2, tracks.Count);

        foreach (var track in tracks)
        {
            Assert.Equal(TrackWidthDip, track.Bounds.Width);
            Assert.Equal(SegmentWidthsDip, track.Children.Select(c => c.Bounds.Width));
        }
    }

    /// <summary>
    /// What a window's layout manager does: lay out from the root, then keep returning to each
    /// control that invalidated itself, at the constraint and bounds it last had. The panel is
    /// <c>SizeToContent</c>, so the root gets an unbounded measure in a window too.
    /// </summary>
    private static void LayOutLikeAWindow(Control content)
    {
        content.Measure(Size.Infinity);
        content.Arrange(new Rect(content.DesiredSize));

        for (var pass = 0; pass < 10; pass++)
        {
            var returned = false;
            foreach (var control in content.GetVisualDescendants().OfType<Layoutable>())
            {
                if (!control.IsMeasureValid
                    && LayoutInformation.GetPreviousMeasureConstraint(control) is { } constraint)
                {
                    control.Measure(constraint);
                    returned = true;
                }

                if (!control.IsArrangeValid
                    && LayoutInformation.GetPreviousArrangeBounds(control) is { } bounds)
                {
                    control.Arrange(bounds);
                    returned = true;
                }
            }

            if (!returned)
            {
                return;
            }
        }
    }

    /// <summary>The token bars' tracks: the only grids in the panel whose columns are all pixel widths.</summary>
    private static List<Grid> Tracks(Control panel) =>
        panel.GetVisualDescendants().OfType<Grid>()
            .Where(g => g.ColumnDefinitions.Count > 0 && g.ColumnDefinitions.All(c => c.Width.IsAbsolute))
            .ToList();

    /// <summary>
    /// The bars have left the panel, so they are built on their own here, in a frame as wide as
    /// the panel's and with its border and padding, which is the column they used to be given.
    /// </summary>
    private static Border Build(string name, PanelDensity? density = null)
    {
        UseRealFonts();

        var state = SampleStates.All.Single(s => s.Name == name);
        var sections = new StackPanel();
        new PanelTokenBars(new LinuxPanelTheme(false)).AddTo(
            sections, PanelSections.TokenBars(state.Stats, state.Scope ?? TranscriptScopeReport.Inspect()));

        return new Border
        {
            Width = PanelMetrics.PanelWidthDip,
            BorderThickness = new Thickness(1),
            Padding = new Thickness((density ?? PanelDensity.Normal).RootPadding),
            Child = sections,
        };
    }

    /// <summary>
    /// The font setup <see cref="PanelShortScreenTests"/> already runs once per process, called
    /// through its own lock: test classes run in parallel, and Avalonia's registry is not safe
    /// for two of them to fill at once.
    /// </summary>
    private static void UseRealFonts() =>
        typeof(PanelShortScreenTests)
            .GetMethod("UseRealFonts", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, null);
}
