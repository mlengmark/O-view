using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
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
/// The local block's motion on Linux: the Output | Overall fold with its sliding highlight, and
/// the block's turn between the totals and the models.
///
/// <para>Each test drives the panel's rebuild exactly as the window does on a press (the press
/// hands back the view, the panel is populated for it and laid out) and then runs the motion by
/// hand, one frame at a time, on a clock it owns. What it reads is the laid-out tree.</para>
/// </summary>
public class PanelFoldMotionTests
{
    private static readonly double LeaveMs = PanelMotion.Ms(PanelMotion.RowsCloseFadeBaseMs);
    private static readonly double HighlightMs = PanelMotion.Ms(PanelMotion.SwitchHighlightBaseMs);

    // ── the fold ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The old rows fade out together while the height holds; then the height moves between the
    /// two tables' while the new rows arrive; and at rest the block is today's, part for part.
    /// </summary>
    [Theory]
    [InlineData(PanelMeasure.Output)]
    [InlineData(PanelMeasure.Overall)]
    public void TheTableFoldsThroughItsFramesAndRestsAsAFreshBuild(PanelMeasure from)
    {
        var rig = new Rig(new PanelViewChoice(from));
        var oldHeight = Table(rig.Panel).Bounds.Height;
        var to = Other(from);

        rig.Press(to);
        rig.FrameAt(LeaveMs / 4);
        rig.FrameAt(LeaveMs / 2);

        var leaving = Tables(rig.Panel);
        Assert.Equal(2, leaving.Count);
        Assert.Equal(0.5, leaving[^1].Opacity, 0.01);
        Assert.Equal(oldHeight, Host(rig.Panel).Height, 0.5);
        Assert.True(Host(rig.Panel).ClipToBounds);

        var newHeight = Fresh(to).Table.Bounds.Height;
        Assert.NotEqual(oldHeight, newHeight, 0.5);

        rig.FrameAt(LeaveMs);
        rig.FrameAt(LeaveMs + PanelMotion.Ms(PanelMotion.FoldCloseBaseMs) / 2);

        var midway = Host(rig.Panel).Height;
        Assert.InRange(midway, Math.Min(oldHeight, newHeight) + 1, Math.Max(oldHeight, newHeight) - 1);
        Assert.Single(Tables(rig.Panel));
        Assert.Contains(Table(rig.Panel).Children, c => c.Opacity < 1 && c.Opacity > 0 || c.RenderTransform is not null);

        rig.Rest();
        AssertAtRest(rig.Panel, Fresh(to).Panel);
    }

    /// <summary>
    /// A second press while the old rows are leaving brings them back from where they had faded
    /// to, whichever way the fold was going, and ends on the measure it asked for.
    /// </summary>
    [Theory]
    [InlineData(PanelMeasure.Output)]
    [InlineData(PanelMeasure.Overall)]
    public void ASecondPressWhileTheRowsLeaveBringsThemBack(PanelMeasure from)
    {
        var rig = new Rig(new PanelViewChoice(from));

        rig.Press(Other(from));
        rig.FrameAt(LeaveMs / 2);
        var faded = Tables(rig.Panel)[^1].Opacity;

        rig.Press(from);
        Assert.Equal(faded, Table(rig.Panel).Opacity, 0.01);
        Assert.Single(Tables(rig.Panel));

        rig.FrameAt(LeaveMs / 2 + 40);
        Assert.InRange(Table(rig.Panel).Opacity, faded + 0.01, 0.99);

        rig.Rest();
        AssertAtRest(rig.Panel, Fresh(from).Panel);
    }

    /// <summary>
    /// A second press while the height is moving holds it where it stands, never at either end,
    /// and folds back to the measure asked for, whether the first press was opening or closing.
    /// </summary>
    [Theory]
    [InlineData(PanelMeasure.Output)]
    [InlineData(PanelMeasure.Overall)]
    public void ASecondPressWhileTheHeightMovesTurnsItRoundWhereItStands(PanelMeasure from)
    {
        var rig = new Rig(new PanelViewChoice(from));
        var start = Table(rig.Panel).Bounds.Height;
        var end = Fresh(Other(from)).Table.Bounds.Height;

        rig.Press(Other(from));
        rig.FrameAt(LeaveMs);
        rig.FrameAt(LeaveMs + 100);
        var caught = Host(rig.Panel).Height;
        Assert.InRange(caught, Math.Min(start, end) + 1, Math.Max(start, end) - 1);

        rig.Press(from);
        Assert.Equal(caught, Host(rig.Panel).Height, 0.5);

        rig.FrameAt(LeaveMs + 100 + LeaveMs / 2);
        Assert.Equal(caught, Host(rig.Panel).Height, 0.5);
        Assert.Equal(2, Tables(rig.Panel).Count);

        rig.Rest();
        AssertAtRest(rig.Panel, Fresh(from).Panel);
    }

    /// <summary>The window fits the height the fold ends at, not a frame of it.</summary>
    [Fact]
    public void TheNaturalHeightMidFoldIsTheHeightItEndsAt()
    {
        var rig = new Rig(default);

        rig.Press(PanelMeasure.Overall);
        rig.FrameAt(LeaveMs);
        rig.FrameAt(LeaveMs + 100);

        Assert.Equal(Fresh(PanelMeasure.Overall).Panel.NaturalHeightDip(), rig.Panel.NaturalHeightDip(), 0.5);
        Assert.Single(Tables(rig.Panel));
    }

    // ── the highlight ───────────────────────────────────────────────────────────────

    /// <summary>The lit fill slides between the segments on the shared curve, and at rest is the segment's own fill again.</summary>
    [Fact]
    public void TheHighlightSlidesToTheSegmentNowOn()
    {
        var rig = new Rig(default);
        var (output, _) = Segments(rig.Panel);
        var startX = output.Bounds.X;

        rig.Press(PanelMeasure.Overall);
        rig.FrameAt(HighlightMs / 2);

        var (_, overall) = Segments(rig.Panel);
        var fill = Highlight(rig.Panel);
        var travel = PanelMotion.Decelerate.ValueAt(0.5);

        Assert.True(fill.IsVisible);
        Assert.Equal(startX + (overall.Bounds.X - startX) * travel, fill.Margin.Left, 0.5);
        Assert.Equal(Colors.Transparent, Colour(overall.Background));

        rig.Rest();
        Assert.False(Highlight(rig.Panel).IsVisible);
        Assert.Equal(Color.Parse(PanelPalette.Get("BarTrack", false)), Colour(Segments(rig.Panel).Overall.Background));
    }

    // ── the turn ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The flip tag turns the block: the face going fades away while the face coming fades in
    /// after it, the height moves between the two, and the tag's mark turns half a circle.
    /// </summary>
    [Fact]
    public void TheBlockTurnsBetweenItsFacesAndRestsAsAFreshBuild()
    {
        var rig = new Rig(default);

        rig.Flip(PanelFace.Models);
        rig.FrameAt(PanelMotion.Ms(PanelMotion.TileFaceOutBaseMs) / 2);

        var (totals, models) = Faces(rig.Panel);
        Assert.InRange(totals.Opacity, 0.01, 0.99);
        Assert.Equal(0, models.Opacity);
        Assert.False(totals.IsHitTestVisible);
        Assert.True(double.IsFinite(FacesCell(rig.Panel).Height));
        Assert.IsType<RotateTransform>(Mark(rig.Panel).RenderTransform);

        rig.FrameAt(PanelMotion.Ms(PanelMotion.TileFaceInDelayBaseMs + PanelMotion.TileFaceInBaseMs / 2));
        Assert.InRange(FacesCell(rig.Panel).Children[^1].Opacity, 0.01, 0.99);

        rig.Rest();
        AssertAtRest(rig.Panel, Fresh(default, PanelFace.Models).Panel);
        Assert.Null(Mark(rig.Panel).RenderTransform);
    }

    /// <summary>A second press mid-turn brings the face that was leaving back from where it had got to.</summary>
    [Fact]
    public void ASecondPressMidTurnTurnsItBack()
    {
        var rig = new Rig(default);

        rig.Flip(PanelFace.Models);
        rig.FrameAt(PanelMotion.Ms(PanelMotion.TileFaceOutBaseMs) / 2);
        var caught = Faces(rig.Panel).Totals.Opacity;

        rig.Flip(PanelFace.Totals);
        Assert.Equal(caught, Faces(rig.Panel).Totals.Opacity, 0.01);

        rig.Rest();
        AssertAtRest(rig.Panel, Fresh(default).Panel);
    }

    // ── the desktop's setting ───────────────────────────────────────────────────────

    /// <summary>With the desktop's animations off, a press changes the block in one step: nothing is left moving or pinned.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WithAnimationsOffEveryChangeLandsInOneStep(bool flip)
    {
        var rig = new Rig(default, animationsOn: false);

        if (flip)
        {
            rig.Flip(PanelFace.Models);
        }
        else
        {
            rig.Press(PanelMeasure.Overall);
        }

        Assert.Empty(rig.Frames);
        AssertAtRest(rig.Panel, flip ? Fresh(default, PanelFace.Models).Panel : Fresh(PanelMeasure.Overall).Panel);
    }

    // ── the window ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Mid-fold, the place the window asks for keeps a panel at the bottom on its docked edge:
    /// its top moves by exactly what the height has, so the lower edge does not.
    /// </summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void MidFoldThePanelsLowerEdgeStaysOnTheDockedEdge(double scaling)
    {
        var rig = new Rig(default);
        var restHeight = rig.Panel.Bounds.Height;
        const int bottom = 1032;
        var restTop = PanelWindow.DockedTop(bottom, restHeight, scaling);

        rig.Press(PanelMeasure.Overall);
        rig.FrameAt(LeaveMs);

        foreach (var at in new[] { LeaveMs + 60, LeaveMs + 200, LeaveMs + 320 })
        {
            rig.FrameAt(at);
            var height = rig.Panel.Bounds.Height;
            var top = PanelWindow.DockedTop(bottom, height, scaling);

            Assert.NotEqual(restTop, top);
            Assert.Equal(bottom, top + (int)Math.Round(height * scaling));
        }
    }

    /// <summary>
    /// The window's half, from its source, because it cannot be built here: the content's motion
    /// asks only the desktop's setting, every open settles it, every placement records the docked
    /// edge, and a change in the content's height re-places the window from that edge.
    /// </summary>
    [Fact]
    public void TheWindowHandsTheContentItsMotionAndKeepsTheDockedEdge()
    {
        var source = WindowSource();

        Assert.Contains("Motion = new LinuxMotion(() => animationsOn?.Invoke() ?? true)", source, StringComparison.Ordinal);
        Assert.Contains("e.Property == BoundsProperty", source, StringComparison.Ordinal);
        Assert.Contains("DockedTop(bottom, _content.Bounds.Height, RenderScaling)", source, StringComparison.Ordinal);
        Assert.Contains("place => Position = Docked(place)", source, StringComparison.Ordinal);
        Assert.Contains("_requestedPosition = Docked(new PixelPoint(left, top), panelHeightDip, scaling)", source, StringComparison.Ordinal);
        Assert.Contains("_requestedPosition = Docked(ToPixels(place, scaling), scaling: scaling)", source, StringComparison.Ordinal);

        var show = Regex.Match(source, @"public void ShowWith\(.*?\{(?<body>.*?)\n    \}", RegexOptions.Singleline).Groups["body"].Value;
        Assert.True(
            show.IndexOf("_content.SettleMotion()", StringComparison.Ordinal) is var settled and >= 0
            && settled < show.IndexOf("_populate()", StringComparison.Ordinal),
            "every open settles the content's motion before it builds the panel");
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────

    private static SampleState Typical => SampleStates.All.Single(s => s.Name == "typical");

    private static PanelMeasure Other(PanelMeasure measure) =>
        measure == PanelMeasure.Output ? PanelMeasure.Overall : PanelMeasure.Output;

    /// <summary>What the panel looks like built straight into a view, with nothing moving: today's panel.</summary>
    private static (PanelContent Panel, Grid Table) Fresh(PanelMeasure measure, PanelFace face = PanelFace.Totals)
    {
        var panel = Rig.Build(new PanelViewChoice(measure, face), motion: null);
        return (panel, face == PanelFace.Totals ? Table(panel) : null!);
    }

    /// <summary>
    /// At rest the panel is today's: every visible part where a fresh build puts it, painted the
    /// same, at full strength, unmoved and unclipped as there. A motion can leave a plain layout
    /// panel round a face or the table, which draws nothing, so those are not compared.
    /// </summary>
    private static void AssertAtRest(PanelContent moved, PanelContent fresh)
    {
        Assert.Equal(fresh.Bounds.Height, moved.Bounds.Height, 0.01);
        Assert.Equal(Parts(fresh), Parts(moved));
    }

    private static List<string> Parts(PanelContent panel) =>
        panel.GetVisualDescendants().OfType<Control>()
            .Where(c => c is not Avalonia.Controls.Panel && c.GetSelfAndVisualAncestors().OfType<Control>().All(a => a.IsVisible))
            .Select(c =>
            {
                var line = c.GetSelfAndVisualAncestors().OfType<Control>().TakeWhile(a => a != panel).ToList();
                var paint = (c switch { Border b => b.Background, TemplatedControl t => t.Background, _ => null }) as ISolidColorBrush;
                return $"{c.GetType().Name} {new Rect(c.TranslatePoint(default, panel)!.Value, c.Bounds.Size)} "
                    + $"{line.Aggregate(1.0, (o, a) => o * a.Opacity):0.######} {paint?.Color} "
                    + $"clip={line.Count(a => a.ClipToBounds)} moved={line.Count(a => a.RenderTransform is not null)}";
            })
            .ToList();

    private static List<Grid> Tables(Control panel) =>
        panel.GetVisualDescendants().OfType<Grid>()
            .Where(g => g.RowDefinitions.Count > 0 && g.ColumnDefinitions.Count >= 3 && g.ColumnDefinitions[1].Width.IsAbsolute)
            .ToList();

    /// <summary>The table the view asks for: the one a fold is arriving with, drawn beneath one leaving.</summary>
    private static Grid Table(Control panel) => Tables(panel)[0];

    private static Grid Host(Control panel) => (Grid)Table(panel).GetVisualParent()!;

    /// <summary>Both faces mid-turn: the totals always beneath the models, as on Windows.</summary>
    private static (StackPanel Totals, StackPanel Models) Faces(Control panel)
    {
        var faces = FacesCell(panel).Children.OfType<StackPanel>().ToList();
        Assert.Equal(2, faces.Count);
        return (faces[0], faces[1]);
    }

    private static Grid FacesCell(Control panel) =>
        panel.GetVisualDescendants().OfType<Grid>().Single(g => double.IsFinite(g.Height) && g.Children.All(c => c is StackPanel));

    private static Avalonia.Controls.Shapes.Path Mark(Control panel) =>
        panel.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single(p => p.RenderTransformOrigin.Unit == RelativeUnit.Absolute);

    private static (RadioButton Output, RadioButton Overall) Segments(Control panel)
    {
        var segments = panel.GetVisualDescendants().OfType<RadioButton>().ToList();
        return (segments[0], segments[1]);
    }

    private static Border Highlight(Control panel) =>
        Segments(panel).Output.GetVisualParent()!.GetVisualParent()!.GetVisualChildren().OfType<Border>().Single();

    private static Color Colour(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    private static string WindowSource() =>
        (string)typeof(PanelShortScreenTests)
            .GetMethod("WindowSourceWithoutComments", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, null)!;

    /// <summary>A panel on a clock the test owns, laid out after every rebuild and every frame, as a window lays out before it paints.</summary>
    private sealed class Rig
    {
        private double _nowMs;

        public Rig(PanelViewChoice view, bool animationsOn = true)
        {
            View = view;
            Motion = new LinuxMotion(() => animationsOn, () => TimeSpan.FromMilliseconds(_nowMs), Frames.Add);
            Panel = Build(view, Motion);
            Panel.ViewChosen += chosen =>
            {
                View = chosen;
                Populate(Panel, chosen);
                PanelSampleRenderer.LayOut(Panel);
            };
        }

        public PanelContent Panel { get; }

        public LinuxMotion Motion { get; }

        public PanelViewChoice View { get; private set; }

        /// <summary>Every time the motion asked for frames to start (true) or stop (false).</summary>
        public List<bool> Frames { get; } = [];

        public static PanelContent Build(PanelViewChoice view, LinuxMotion? motion)
        {
            typeof(PanelShortScreenTests)
                .GetMethod("UseRealFonts", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, null);

            var panel = new PanelContent(new LinuxPanelTheme(light: false));
            if (motion is not null)
            {
                panel.Motion = motion;
            }

            Populate(panel, view);
            PanelSampleRenderer.LayOut(panel);
            return panel;
        }

        /// <summary>Presses the segment for <paramref name="measure"/>; the rebuild it causes runs as the window runs it.</summary>
        public void Press(PanelMeasure measure)
        {
            var (output, overall) = Segments(Panel);
            (measure == PanelMeasure.Output ? output : overall).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }

        /// <summary>Presses the flip tag, which must be showing the other face's word.</summary>
        public void Flip(PanelFace to)
        {
            var label = TokenText.FlipTagLabel(to == PanelFace.Models ? PanelFace.Totals : PanelFace.Models);
            Panel.GetVisualDescendants().OfType<Button>().Single(b => b is not RadioButton && Equals(b.Content, label))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(to, View.Face);
        }

        /// <summary>Draws a frame every 16 ms, as the window does, until nothing is moving.</summary>
        public void Rest()
        {
            for (var frames = 0; Motion.Running && frames < 500; frames++)
            {
                FrameAt(_nowMs + 16);
            }

            Assert.False(Motion.Running);
            FrameAt(_nowMs);
        }

        /// <summary>Draws the frame <paramref name="ms"/> after the rig was made, then lays the panel out.</summary>
        public void FrameAt(double ms)
        {
            _nowMs = ms;
            Motion.Tick();

            // All of it: a window's layout passes a child's new height up to its parents, the
            // offscreen layout only goes back to the child.
            foreach (var control in Panel.GetVisualDescendants().OfType<Layoutable>())
            {
                control.InvalidateMeasure();
            }

            PanelSampleRenderer.LayOut(Panel);
        }

        private static void Populate(PanelContent panel, PanelViewChoice view)
        {
            var state = Typical;
            panel.Populate(state.Snapshot, state.Stats, state.Account, state.Plan, state.Scope,
                SampleStates.Now, state.Boost, SampleStates.Zone, state.CachedUsage, view);
        }
    }
}
