using System.Text.RegularExpressions;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

using Avalonia.Skia;
using Avalonia.VisualTree;
using OView.App.Rendering;
using OView.Linux.Panel;
using OView.Presentation.Samples;
using OView.Presentation.Text;

namespace OView.Linux.Tests;

/// <summary>
/// The panel on a display too short for it: that it measures itself rather than the screen,
/// that the part which will not fit can still be reached, and that the bar it is reached with
/// is this project's own rather than a toolkit default.
///
/// <para><b>What went wrong.</b> The panel is <c>SizeToContent</c>, so its frame is only ever
/// as tall as the compositor granted — and no compositor grants more than the screen. Asking
/// the frame how tall the panel wanted to be therefore returned exactly the screen height on
/// Maximilian's 1024×768 desktop, the panel concluded it fitted the 712 DIP work area, and
/// four sections sat below the bottom edge with nothing to bring them back (his report of
/// 2026-10-15).</para>
///
/// <para>These measure a real tree rather than reasoning about one, which needs a font: a
/// <see cref="TextBlock"/> cannot say how tall it is without one, and a test process has no
/// toolkit platform of its own. <see cref="UseRealFonts"/> registers just the rasteriser the
/// app itself uses, which is the whole of what measuring needs.</para>
/// </summary>
public class PanelShortScreenTests
{
    /// <summary>The 1024×768 desktop the panel was reported cut off on, and what its work area
    /// leaves once <see cref="WorkAreaPlacement.DefaultMarginPx"/> is taken at each end.</summary>
    private const double ScreenHeightDip = 768;

    private const double ShortWorkAreaDip = 712;

    private const double PanelWidthDip = 400;

    // ── the measurement ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Half of the guard the whole slice turns on: whatever height the screen has already forced
    /// on the panel, the content still answers with the height it wants. The other half — that
    /// the window asks the content at all, rather than its own frame — is structural, and is the
    /// test below it.
    /// </summary>
    [Fact]
    public void TheNaturalHeightIsTheContentsOwnAndNotOneTheScreenHasForcedOnIt()
    {
        var panel = Panel(PanelSampleRenderer.ShortState, light: false, PanelDensity.Normal);
        var natural = panel.NaturalHeightDip();

        // Exactly what a 1024×768 compositor does: hand back a frame no taller than the screen.
        panel.Measure(new Size(PanelWidthDip, ScreenHeightDip));
        panel.Arrange(new Rect(0, 0, PanelWidthDip, ScreenHeightDip));

        Assert.Equal(natural, panel.NaturalHeightDip(), 3);
        Assert.True(
            natural > ScreenHeightDip,
            $"the fullest panel measured {natural:0} dip, which a {ScreenHeightDip:0} dip screen "
            + "cannot have reported. This test only means something while the panel it measures "
            + "is taller than that screen, and it no longer is — the state it is built from needs "
            + "replacing with one that still overflows.");
    }

    /// <summary>
    /// The other half, structural, in the spirit of the guards in <c>PanelDismissalTests</c>:
    /// the test above pins the content's answer, but the fault was the window's. It asked its
    /// own frame how tall the panel wanted to be, and putting that one line back leaves every
    /// behavioural test in this file green. <see cref="PanelWindow"/> cannot be built in a test
    /// process — there is no windowing subsystem — so its source is read, which is how that
    /// same file is already held.
    ///
    /// <para>The rule being held: the window may take its <i>width</i> from the size it reports
    /// itself, because nothing has ever capped a 400 DIP panel's width, and may take nothing
    /// else from it. Every height it sizes or places from comes through
    /// <see cref="PanelContent.NaturalHeightDip"/>.</para>
    /// </summary>
    [Fact]
    public void TheWindowSizesAndPlacesFromTheContentAndNeverFromItsOwnFrame()
    {
        var source = WindowSourceWithoutComments();

        var reads = Regex.Matches(
            source,
            @"(?<![.\w])\(?(?:this\.)?(?:FrameSize|ClientSize|DesiredSize|Bounds)\b"
            + @"(?:\s*\?\?\s*(?:FrameSize|ClientSize|DesiredSize|Bounds))*\s*\)?\s*(?<member>\.\w+)?");

        foreach (Match read in reads)
        {
            Assert.True(
                read.Groups["member"].Value == ".Width",
                $"PanelWindow takes `{read.Value.Trim()}` off the size it reports itself. The panel "
                + "is SizeToContent, so that size is only ever what the compositor granted, and no "
                + "compositor grants more than the screen: reading it made a 1024x768 desktop "
                + "report a natural height of exactly 768, conclude the panel fitted a 712 dip "
                + "work area, and leave four sections below the bottom edge with nothing to scroll "
                + "them back. Only the width may come from the window; every height comes from "
                + "PanelContent.NaturalHeightDip.");
        }

        Assert.True(
            source.Contains("_content.NaturalHeightDip()", StringComparison.Ordinal),
            "PanelWindow never asks its content how tall it is, so the height it fits to can only "
            + "have come from the window or from a guess at the screen.");

        Assert.True(
            Regex.IsMatch(source, @"PlaceAtWorkAreaCorner\(\s*double"),
            "PanelWindow places the panel without being told how tall it is, so the corner can "
            + "only have come from the window — which on a short screen describes the screen, and "
            + "straight after a resize describes a size the compositor has not answered yet. Pass "
            + "the height the fit decided.");
    }

    /// <summary>
    /// And compacting alone does not save it: the fullest panel is still taller than a short
    /// desktop's work area at the tighter density, which is what makes scrolling the answer
    /// rather than an extra.
    /// </summary>
    [Fact]
    public void TheFullestPanelStillOverflowsAShortWorkAreaOnceCompacted()
    {
        var compacted = Panel(PanelSampleRenderer.ShortState, light: false, PanelDensity.Compact)
            .NaturalHeightDip();

        var fit = PanelFit.Resolve(compacted, ShortWorkAreaDip);

        Assert.True(fit.MustScroll, $"the compacted panel measured {compacted:0} dip");
        Assert.Equal(ShortWorkAreaDip, fit.WindowHeightDip);
    }

    /// <summary>
    /// The state the verification renders photograph for a short display is the tallest one —
    /// a picture of a state that nearly fitted would prove nothing. Named in the renderer so
    /// the golden image keeps its name; checked here so the name cannot go quietly stale.
    /// </summary>
    [Fact]
    public void TheStateRenderedForAShortScreenIsTheTallestOfThem()
    {
        var rendered = Panel(PanelSampleRenderer.ShortState, light: false, PanelDensity.Normal)
            .NaturalHeightDip();

        foreach (var state in SampleStates.All)
        {
            var height = Panel(state.Name, light: false, PanelDensity.Normal).NaturalHeightDip();

            Assert.True(
                height <= rendered,
                $"{state.Name} measures {height:0} dip against {PanelSampleRenderer.ShortState}'s "
                + $"{rendered:0} — the short render should photograph the tallest state");
        }
    }

    // ── nothing is dropped ──────────────────────────────────────────────────────────

    /// <summary>
    /// A panel with room has no viewport and no bar at all, so it is laid out exactly as it
    /// was before either existed.
    /// </summary>
    [Fact]
    public void APanelWithRoomNeitherScrollsNorShowsABar()
    {
        var panel = Panel(PanelSampleRenderer.ShortState, light: false, PanelDensity.Normal);
        Layout(panel);

        Assert.False(panel.Scrolls);
        Assert.False(panel.ScrollBar.IsVisible);
    }

    /// <summary>
    /// The promise scrolling is for: every section of the capped panel can be brought fully
    /// into view, and the last of them sits at the very foot of the content — so nothing is
    /// past the end of what the scroll reaches. Both views, because the breakdown's table is
    /// the taller of the two and is the one most likely to run off.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EverySectionIsReachableWhenTheWindowHasBeenCapped(bool breakdown)
    {
        var panel = Capped(light: false, breakdown);
        var sections = ((StackPanel)panel.Viewport.Child!).Children;
        var viewport = panel.Viewport.Bounds.Height;
        var hidden = panel.Viewport.Extent.Height - viewport;

        Assert.True(hidden > 0, $"the capped panel hides nothing: extent {panel.Viewport.Extent.Height:0}");

        foreach (var section in sections)
        {
            Assert.True(
                section.Bounds.Height <= viewport + 0.5,
                $"a section {section.Bounds.Height:0} dip tall cannot fit a {viewport:0} dip viewport");
            Assert.True(
                section.Bounds.Bottom - viewport <= hidden + 0.5,
                $"a section ending at {section.Bounds.Bottom:0} dip is past what the scroll reaches");
        }

        // Scrolled to the end, the last section is whole and on screen.
        panel.Viewport.Offset = panel.Viewport.Offset.WithY(hidden);
        var last = sections[^1];

        Assert.True(last.Bounds.Y - hidden >= -0.5, "the last section starts above the viewport");
        Assert.True(last.Bounds.Bottom - hidden <= viewport + 0.5, "the last section ends below it");
    }

    /// <summary>A wheel turn over the content scrolls it, which is the gesture a user reaches
    /// for before they look for a bar at all.</summary>
    [Fact]
    public void TheMouseWheelScrollsTheCappedContent()
    {
        var panel = Capped(light: false, breakdown: false);

        Assert.Equal(0, panel.Viewport.Offset.Y);

        panel.Viewport.RaiseEvent(new PointerWheelEventArgs(
            panel.Viewport,
            new Pointer(1, PointerType.Mouse, isPrimary: true),
            panel,
            new Point(10, 10),
            timestamp: 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other),
            KeyModifiers.None,
            new Vector(0, -3)));

        Assert.True(panel.Viewport.Offset.Y > 0, "a wheel turn away from the user did not scroll down");
    }

    // ── the bar itself ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The bar is thin, and both its colours come from the shared palette rather than from a
    /// grey chosen here — the same two the Windows panel reads.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheScrollBarIsThinAndPainted(bool light)
    {
        var panel = Capped(light, breakdown: false);
        var track = Assert.IsType<Border>(panel.ScrollBar);
        var thumb = Assert.IsType<Border>(Assert.Single(track.GetVisualChildren().OfType<Border>()));

        Assert.True(track.IsVisible);
        Assert.Equal(6, track.Width);
        Assert.Equal(6, thumb.Width);

        Assert.Equal(Hex("ScrollTrack", light), Colour(track.Background));
        Assert.Equal(Hex("ScrollThumb", light), Colour(thumb.Background));
    }

    /// <summary>
    /// The thumb says where in the content the reader is: as tall a share of the track as the
    /// viewport is of the content, at the top when nothing is scrolled and at the foot when
    /// everything is.
    /// </summary>
    [Fact]
    public void TheThumbShowsHowMuchIsOnScreenAndWhereItIs()
    {
        var panel = Capped(light: false, breakdown: false);
        var track = Assert.IsType<Border>(panel.ScrollBar);
        var thumb = Assert.IsType<Border>(Assert.Single(track.GetVisualChildren().OfType<Border>()));

        var viewport = panel.Viewport.Bounds.Height;
        var hidden = panel.Viewport.Extent.Height - viewport;

        Assert.True(thumb.Height < viewport, "a thumb as tall as its track says nothing");
        Assert.True(thumb.Height >= 24, "the thumb is too short to grab");
        Assert.Equal(0, thumb.Margin.Top);

        panel.Viewport.Offset = panel.Viewport.Offset.WithY(hidden);

        Assert.Equal(viewport - thumb.Height, thumb.Margin.Top, 3);
    }

    // ── building the panels ─────────────────────────────────────────────────────────

    /// <summary>
    /// The fullest panel as a short desktop lays it out: compacted, put in its viewport, and
    /// pinned to the work area — the same three steps the window takes, and the same ones the
    /// verification render takes offscreen.
    /// </summary>
    private static PanelContent Capped(bool light, bool breakdown)
    {
        var panel = Panel(PanelSampleRenderer.ShortState, light, PanelDensity.Compact);

        if (breakdown)
        {
            var half = panel.GetVisualDescendants().OfType<Button>()
                .Single(b => (b.Content as string) == TokenText.TokenViewBreakdownLabel);
            half.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }

        var fit = PanelFit.Resolve(panel.NaturalHeightDip(), ShortWorkAreaDip);
        Assert.True(fit.MustScroll);

        panel.ScrollContent(true);
        panel.Height = fit.WindowHeightDip;
        Layout(panel, passes: 3);

        return panel;
    }

    private static PanelContent Panel(string stateName, bool light, PanelDensity density)
    {
        UseRealFonts();

        var state = SampleStates.All.Single(s => s.Name == stateName);
        var panel = new PanelContent(new LinuxPanelTheme(light)) { Density = density };
        panel.Populate(state.Snapshot, state.Stats, state.Account, state.Plan, state.Scope,
            SampleStates.Now, state.Boost, SampleStates.Zone, state.CachedUsage);

        return panel;
    }

    /// <summary>
    /// Lays the tree out by hand. Nothing here is attached to a window, so there is no layout
    /// manager to notice an invalidation and run the next pass — and a capped panel needs
    /// three, for the reason <c>PanelSampleRenderer</c> records.
    /// </summary>
    private static void Layout(Control content, int passes = 1)
    {
        for (var pass = 0; pass < passes; pass++)
        {
            content.InvalidateMeasure();
            content.Measure(Size.Infinity);
            content.InvalidateArrange();
            content.Arrange(new Rect(content.DesiredSize));
        }
    }

    /// <summary>
    /// The rasteriser the app itself draws with, registered once for the whole process, so a
    /// line of text can say how tall it is. Only the font side of it is used here; nothing
    /// below renders anything.
    ///
    /// <para>Two pieces, and deliberately not the app's own start-up: that would want a
    /// windowing system this process has not got, and it would leave an
    /// <see cref="Application"/> behind for every other test in the assembly to trip over —
    /// the panel asks whether one exists before it makes a mouse cursor. So the platform's
    /// file services are started on their own (the asset loader the font collection reads
    /// through), and then the rasteriser.</para>
    /// </summary>
    private static void UseRealFonts()
    {
        lock (FontLock)
        {
            if (_fontsReady)
            {
                return;
            }

            var builder = AppBuilder.Configure<Application>()
                .UseStandardRuntimePlatformSubsystem()
                .UseHarfBuzz();

            builder.RuntimePlatformServicesInitializer?.Invoke();
            builder.TextShapingSubsystemInitializer?.Invoke();
            SkiaPlatform.Initialize();
            _fontsReady = true;
        }
    }

    private static readonly Lock FontLock = new();

    private static bool _fontsReady;

    /// <summary>
    /// The window's code without its comments. The paragraphs explaining why the frame is not
    /// read name the very properties the structural guard looks for, so reading the file whole
    /// would fail it on its own documentation. No string in that file contains a slash pair, so
    /// cutting at one cannot swallow code.
    /// </summary>
    private static string WindowSourceWithoutComments()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "O-view.Linux", "Panel", "PanelWindow.cs"));

        return Regex.Replace(source, @"//[^\n]*", string.Empty);
    }

    /// <summary>
    /// Walks up from the test binary to the directory holding the solution file, the way
    /// <c>PanelDismissalTests</c> and <c>PortalThemeSourceTests</c> do: the depth below
    /// <c>bin/</c> differs by configuration, so the marker is searched for rather than counted.
    /// </summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "O-view.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }

    private static Color Hex(string key, bool light) => Color.Parse(PanelPalette.Get(key, light));

    private static Color Colour(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;
}
