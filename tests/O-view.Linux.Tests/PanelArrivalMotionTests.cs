using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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
using Path = Avalonia.Controls.Shapes.Path;

namespace OView.Linux.Tests;

/// <summary>
/// What arrives on the Linux panel, and what leaves it: the chart's bars rising, the doughnuts'
/// arcs sweeping in, the bars moving to the other measure, and the off-plan banner folding away.
///
/// <para>Each test builds the panel as the window does, rebuilds it on a press as the window does,
/// and runs the motion by hand on a clock it owns. What it reads is the laid-out tree.</para>
/// </summary>
public class PanelArrivalMotionTests
{
    private static readonly double RiseMs = PanelMotion.Ms(PanelMotion.ChartBarRiseBaseMs);
    private static readonly double SweepMs = PanelMotion.Ms(PanelMotion.DoughnutSweepBaseMs);
    private static readonly double SwitchMs = PanelMotion.Ms(PanelMotion.ChartSeriesSwitchBaseMs);
    private static readonly double FoldMs = PanelMotion.Ms(PanelMotion.BannerFoldBaseMs);
    private static readonly double FadeMs = PanelMotion.Ms(PanelMotion.BannerFadeBaseMs);

    // ── the bars ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// As the panel arrives every bar starts on the baseline, each rises after the one to its left
    /// by the Windows stagger, and at rest they stand where a panel that never moved puts them.
    /// </summary>
    [Fact]
    public void OnOpenTheBarsRiseOneAfterAnotherFromTheBaseline()
    {
        var rig = new Rig(default);
        var resting = Heights(rig.Panel);
        Assert.True(resting.Count > 3);

        rig.Panel.Arrive();
        Assert.True(rig.Motion.Running, "the rise is under way");
        rig.FrameAt(0);
        Assert.All(Heights(rig.Panel), h => Assert.Equal(0, h, 0.01));

        // The first bar has begun while the last waits out its stagger.
        rig.FrameAt(PanelMotion.ChartBarStartMs(resting.Count - 1) - 1);
        var early = Heights(rig.Panel);
        Assert.True(early[0] > 0.01);
        Assert.Equal(0, early[^1], 0.01);

        // Midway through the first bar's rise, a later bar has not got as far.
        rig.FrameAt(PanelMotion.ChartBarStartMs(0) + RiseMs / 2);
        var mid = Heights(rig.Panel);
        Assert.InRange(mid[0], 1, resting[0] - 0.5);
        Assert.True(mid[^1] / resting[^1] < mid[0] / resting[0]);

        rig.Rest();
        Assert.Equal(resting, Heights(rig.Panel));
        Assert.Equal(Parts(Fresh(default)), Parts(rig.Panel));
    }

    /// <summary>
    /// An Output | Overall press moves every bar at once from its old height towards its new one,
    /// with the series-switch figure; nothing goes back to the baseline.
    /// </summary>
    [Fact]
    public void AMeasurePressMovesEveryBarTogetherFromItsOldHeight()
    {
        // Two series of different shapes, built by hand: in every sample state the two measures'
        // series share a shape, so their bars would not move at all.
        double now = 0;
        var motion = new LinuxMotion(() => true, () => TimeSpan.FromMilliseconds(now), _ => { });
        var chart = new Chart(motion);
        double[] output = [0.2, 0.9, 0.5, 1.0], overall = [1.0, 0.3, 0.5, 0.6];

        chart.Show(output, switching: false);
        var from = chart.Heights();
        var to = new Chart(motion).Show(overall, switching: false).Heights();

        chart.Show(overall, switching: true);
        Assert.Equal(from, chart.Heights());

        now = SwitchMs / 4;
        motion.Tick();
        var quarter = chart.Heights();
        for (var i = 0; i < from.Count; i++)
        {
            if (Math.Abs(from[i] - to[i]) > 1)
            {
                Assert.InRange(quarter[i], Math.Min(from[i], to[i]) + 0.01, Math.Max(from[i], to[i]) - 0.01);
            }
        }

        now = SwitchMs;
        motion.Tick();
        Assert.Equal(to, chart.Heights());
        Assert.False(motion.Running);
    }

    /// <summary>On the panel, a press neither drops a bar to the baseline nor replays the rise.</summary>
    [Fact]
    public void AMeasurePressOnThePanelReplaysNoRise()
    {
        var rig = new Rig(default);
        var resting = Heights(rig.Panel);

        rig.Press(PanelMeasure.Overall);
        for (var ms = 0.0; ms <= SwitchMs; ms += 40)
        {
            rig.FrameAt(ms);
            Assert.All(Heights(rig.Panel), h => Assert.True(h > 0.5));
        }

        rig.Rest();
        Assert.Equal(Heights(Fresh(new PanelViewChoice(PanelMeasure.Overall))), Heights(rig.Panel));
        Assert.Equal(resting.Count, Heights(rig.Panel).Count);
    }

    /// <summary>The totals face coming back from the models rises again, with the later start of a turn.</summary>
    [Fact]
    public void TurningBackToTheTotalsRisesTheBarsWithTheBackStart()
    {
        var rig = new Rig(new PanelViewChoice(Face: PanelFace.Models));
        rig.Flip(PanelFace.Totals);
        rig.FrameAt(rig.Now);
        var resting = Heights(Fresh(default));

        rig.FrameAt(rig.Now + PanelMotion.ChartBarBackStartMs(0) - 1);
        Assert.All(Heights(rig.Panel), h => Assert.Equal(0, h, 0.01));

        rig.FrameAt(rig.Now + RiseMs / 2);
        Assert.InRange(Heights(rig.Panel)[0], 1, resting[0] - 0.5);

        rig.Rest();
        Assert.Equal(resting, Heights(rig.Panel));
    }

    // ── the arcs ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A panel that opens on the model face sweeps both rings in after the open delay: a cover in
    /// the panel's colour hides the ring until then, takes no pointer, and is gone at rest.
    /// </summary>
    [Fact]
    public void OnOpenOnTheModelFaceTheArcsSweepIn()
    {
        var view = new PanelViewChoice(Face: PanelFace.Models);
        var rig = new Rig(view, Models);
        Assert.Empty(Covers(rig.Panel));

        rig.Panel.Arrive();
        rig.FrameAt(PanelMotion.Ms(PanelMotion.DoughnutSweepOpenDelayBaseMs) - 1);
        var covers = Covers(rig.Panel);
        Assert.Equal(2, covers.Count);
        Assert.All(covers, c => Assert.IsType<EllipseGeometry>(c.Data));
        Assert.All(covers, c => Assert.False(c.IsHitTestVisible));

        rig.FrameAt(PanelMotion.Ms(PanelMotion.DoughnutSweepOpenDelayBaseMs) + SweepMs / 2);
        Assert.All(Covers(rig.Panel), c => Assert.IsType<PathGeometry>(c.Data));

        rig.Rest();
        Assert.Empty(Covers(rig.Panel));
        Assert.Equal(Parts(Fresh(view, Models)), Parts(rig.Panel));
    }

    /// <summary>Turning to the models sweeps the rings in with the turn's delay; a measure press there changes them at once.</summary>
    [Fact]
    public void TurningToTheModelsSweepsTheArcsAndAMeasurePressThereDoesNot()
    {
        var rig = new Rig(default, Models);
        rig.Flip(PanelFace.Models);
        var flipped = rig.Now;

        rig.FrameAt(flipped + PanelMotion.Ms(PanelMotion.DoughnutSweepDelayBaseMs) - 1);
        Assert.All(Covers(rig.Panel), c => Assert.IsType<EllipseGeometry>(c.Data));
        Assert.Equal(2, Covers(rig.Panel).Count);

        rig.Rest();
        Assert.Empty(Covers(rig.Panel));

        // The switch's highlight still slides; the rings change at once.
        rig.Press(PanelMeasure.Overall);
        Assert.Empty(Covers(rig.Panel));
    }

    // ── the banner ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Hiding the banner fades it out ahead of its height, which folds to nothing taking the gap
    /// above with it; a second press changes nothing, and the panel the fold leaves behind is a
    /// panel built with the banner hidden, with nothing arriving again.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HidingTheBannerFoldsItAwayAndReplaysNothing(bool byKeyboard)
    {
        var rig = new Rig(default, OffPlan);
        var card = Banner(rig.Panel);
        var height = card.Bounds.Height;
        var panelHeight = rig.Panel.Bounds.Height;

        rig.HideBanner(byKeyboard);
        rig.FrameAt(FadeMs / 2);
        Assert.Equal(0.5, card.Opacity, 0.01);
        Assert.InRange(card.Bounds.Height, 1, height - 0.5);
        Assert.True(card.ClipToBounds);

        rig.HideBanner(byKeyboard);
        rig.FrameAt(FadeMs);
        Assert.Equal(0, card.Opacity, 0.01);
        Assert.Equal(0, rig.Hidden);

        rig.FrameAt(FoldMs - 1);
        Assert.InRange(rig.Panel.Bounds.Height, panelHeight - height - 30, panelHeight - height + 1);

        rig.FrameAt(FoldMs);
        Assert.Equal(1, rig.Hidden);
        Assert.False(rig.Motion.Running);
        Assert.Empty(Banners(rig.Panel));
        Assert.Equal(Parts(Fresh(new PanelViewChoice(BannerHidden: true), OffPlan)), Parts(rig.Panel));
    }

    // ── the desktop's setting ───────────────────────────────────────────────────────

    /// <summary>With the desktop's animations off the open, the press and the hide each land in one step (F55).</summary>
    [Fact]
    public void WithAnimationsOffEverythingIsDrawnCompleteAtOnce()
    {
        var rig = new Rig(default, OffPlan, animationsOn: false);
        var resting = Heights(rig.Panel);

        rig.Panel.Arrive();
        Assert.Equal(resting, Heights(rig.Panel));
        Assert.False(rig.Motion.Running);

        rig.HideBanner(byKeyboard: true);
        Assert.Equal(1, rig.Hidden);
        Assert.Empty(Banners(rig.Panel));

        var models = new Rig(new PanelViewChoice(Face: PanelFace.Models), Models, animationsOn: false);
        models.Panel.Arrive();
        Assert.Empty(Covers(models.Panel));
        Assert.False(models.Motion.Running);
    }

    /// <summary>The open is the one place an arrival starts, and it starts with the slide.</summary>
    [Fact]
    public void TheWindowStartsTheArrivalWithTheSlide()
    {
        var source = (string)typeof(PanelShortScreenTests)
            .GetMethod("WindowSourceWithoutComments", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, null)!;

        var arrive = Regex.Match(source, @"private void Arrive\(\)\s*\{(?<body>.*?)\n    \}", RegexOptions.Singleline).Groups["body"].Value;
        Assert.Contains("_content.Arrive()", arrive, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"_content\.Arrive\(\)"));
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────

    private const string OffPlan = "offplan-on";

    private const string Models = "typical-models";

    private static PanelContent Fresh(PanelViewChoice view, string state = "typical") => Rig.Build(view, null, state);

    /// <summary>The chart's bars, left to right, by their drawn height.</summary>
    private static List<double> Heights(Control panel) =>
        panel.GetVisualDescendants().OfType<Border>()
            .Where(b => !b.UseLayoutRounding && b.GetVisualParent()?.GetType().Name == "ChartSurface")
            .OrderBy(b => b.Bounds.X)
            .Select(b => Math.Round(b.Bounds.Height, 3))
            .ToList();

    private static List<Path> Covers(Control panel) =>
        panel.GetVisualDescendants().OfType<Path>().Where(p => !p.IsHitTestVisible).ToList();

    /// <summary>The banner's hide control: the one focusable square holding a drawn cross.</summary>
    private static List<Border> Hides(Control panel) =>
        panel.GetVisualDescendants().OfType<Border>().Where(b => b.Focusable && b.Child is Canvas).ToList();

    /// <summary>A banner on screen is one with its hide control.</summary>
    private static List<Border> Banners(Control panel) => Hides(panel);

    private static Border Banner(Control panel)
    {
        var hide = Assert.Single(Hides(panel));
        return hide.GetVisualAncestors().OfType<Border>().First(b => b.Background is ISolidColorBrush && b.CornerRadius.TopLeft > 0);
    }

    /// <summary>Every visible part, where it is and how strongly it is painted.</summary>
    private static List<string> Parts(PanelContent panel) =>
        panel.GetVisualDescendants().OfType<Control>()
            .Where(c => c is not Avalonia.Controls.Panel && c.GetSelfAndVisualAncestors().OfType<Control>().All(a => a.IsVisible))
            .Select(c =>
            {
                var line = c.GetSelfAndVisualAncestors().OfType<Control>().TakeWhile(a => a != panel).ToList();
                return $"{c.GetType().Name} {new Avalonia.Rect(c.TranslatePoint(default, panel)!.Value, c.Bounds.Size)} "
                    + $"{line.Aggregate(1.0, (o, a) => o * a.Opacity):0.######} clip={line.Count(a => a.ClipToBounds)}";
            })
            .ToList();

    /// <summary>
    /// The chart on its own, built from hand-made series of four days through the panel's own
    /// chart builder, and laid out in a column of the panel's width after every build and frame.
    /// </summary>
    private sealed class Chart
    {
        private static readonly Type GraphType = typeof(PanelContent).Assembly.GetType("OView.Linux.Panel.PanelGraph")!;

        private readonly object _graph;
        private readonly Border _column = new() { Width = PanelMetrics.ContentWidthDip };

        public Chart(LinuxMotion motion)
        {
            _graph = Activator.CreateInstance(GraphType, new LinuxPanelTheme(light: false))!;
            GraphType.GetMethod("Use")!.Invoke(_graph, [motion]);
        }

        public Chart Show(double[] heights, bool switching)
        {
            var view = new PanelLocalChartView(
                [.. heights.Select((h, i) => new PanelGraphColumnView($"{i + 1} Oct", true, h, h, $"{h}", $"{i + 1} Oct"))],
                [],
                "today");
            _column.Child = (Control)GraphType.GetMethod("Chart")!.Invoke(_graph, [view, PanelDensity.Normal, switching])!;
            return this;
        }

        public List<double> Heights()
        {
            foreach (var control in _column.GetSelfAndVisualDescendants().OfType<Layoutable>())
            {
                control.InvalidateMeasure();
            }

            PanelSampleRenderer.LayOut(_column);
            return PanelArrivalMotionTests.Heights(_column);
        }
    }

    /// <summary>A panel on a clock the test owns, rebuilt on a press or a hidden banner as the window rebuilds it.</summary>
    private sealed class Rig
    {
        private readonly string _state;

        public Rig(PanelViewChoice view, string state = "typical", bool animationsOn = true)
        {
            _state = state;
            View = view;
            Motion = new LinuxMotion(() => animationsOn, () => TimeSpan.FromMilliseconds(Now), _ => { });
            Panel = Build(view, Motion, state);
            Panel.ViewChosen += chosen => Rebuild(chosen);
            Panel.BannerHidden += () =>
            {
                Hidden++;
                Rebuild(View with { BannerHidden = true });
            };
        }

        public PanelContent Panel { get; }

        public LinuxMotion Motion { get; }

        public PanelViewChoice View { get; private set; }

        public double Now { get; private set; }

        /// <summary>How many times the panel was told the banner is hidden.</summary>
        public int Hidden { get; private set; }

        public static PanelContent Build(PanelViewChoice view, LinuxMotion? motion, string state)
        {
            typeof(PanelShortScreenTests)
                .GetMethod("UseRealFonts", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, null);

            var panel = new PanelContent(new LinuxPanelTheme(light: false));
            if (motion is not null)
            {
                panel.Motion = motion;
            }

            Populate(panel, view, state);
            PanelSampleRenderer.LayOut(panel);
            return panel;
        }

        public void Press(PanelMeasure measure)
        {
            var segments = Panel.GetVisualDescendants().OfType<RadioButton>().ToList();
            segments[measure == PanelMeasure.Output ? 0 : 1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }

        public void Flip(PanelFace to)
        {
            var label = TokenText.FlipTagLabel(to == PanelFace.Models ? PanelFace.Totals : PanelFace.Models);
            Panel.GetVisualDescendants().OfType<Button>().Single(b => b is not RadioButton && Equals(b.Content, label))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }

        /// <summary>
        /// Presses the banner's hide with Enter, or with Space. The pointer's press runs the same
        /// hide, and a raised pointer event cannot reach it here: a test has no pointer to press.
        /// </summary>
        public void HideBanner(bool byKeyboard)
        {
            var hide = Hides(Panel).SingleOrDefault();
            if (hide is null)
            {
                return;   // already gone: a press on nothing
            }

            hide.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = byKeyboard ? Key.Enter : Key.Space });
            Lay();
        }

        public void Rest()
        {
            for (var frames = 0; Motion.Running && frames < 500; frames++)
            {
                FrameAt(Now + 16);
            }

            Assert.False(Motion.Running);
            FrameAt(Now);
        }

        public void FrameAt(double ms)
        {
            Now = ms;
            Motion.Tick();
            Lay();
        }

        private void Lay()
        {
            // All of it: the offscreen layout does not pass a child's new height up to its parents.
            foreach (var control in Panel.GetVisualDescendants().OfType<Layoutable>())
            {
                control.InvalidateMeasure();
            }

            PanelSampleRenderer.LayOut(Panel);
        }

        private void Rebuild(PanelViewChoice view)
        {
            View = view;
            Populate(Panel, view, _state);
            PanelSampleRenderer.LayOut(Panel);
        }

        private static void Populate(PanelContent panel, PanelViewChoice view, string name)
        {
            var state = SampleStates.All.Single(s => s.Name == name);
            panel.Populate(state.Snapshot, state.Stats, state.Account, state.Plan, state.Scope,
                SampleStates.Now, state.Boost, SampleStates.Zone, state.CachedUsage, view);
        }
    }
}
