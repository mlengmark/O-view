using System.Text.RegularExpressions;
using OView.App.Rendering;
using OView.Linux.Panel;
using OView.Linux.Platform;

namespace OView.Linux.Tests;

/// <summary>
/// The Linux panel's motion: the piece that runs <see cref="PanelMotion"/> against Avalonia, the
/// desktop's animation setting, and the panel appearing and leaving.
///
/// <para><b>What a test here can reach.</b> The clock and the frame source are handed in, so a
/// motion is driven frame by frame without a dispatcher, and every value it puts on screen is
/// compared with the shared curve. No test can build a window (there is no windowing subsystem)
/// or ask a real portal, so the window's half is pinned by reading its source, the way
/// <see cref="PanelDismissalTests"/> pins the dismissal, and the portal by the rule that turns its
/// answers into on or off.</para>
/// </summary>
public class LinuxMotionTests
{
    private const double OpenMs = 460;
    private const double CloseMs = 300;

    // ── the motion piece ────────────────────────────────────────────────────────────

    [Fact]
    public void AMotionFollowsTheSharedCurveAndLandsExactlyOnItsFigure()
    {
        var rig = new Rig();
        var value = rig.Motion.Value(500, rig.Shown.Add);
        var arrived = 0;

        value.To(0, OpenMs, PanelMotion.Decelerate, () => arrived++);

        foreach (var at in new[] { 16.0, 115, 230, 400 })
        {
            rig.FrameAt(at);
            var expected = 500 - 500 * PanelMotion.Decelerate.ValueAt(at / OpenMs);
            Assert.Equal(expected, value.Current, precision: 9);
            Assert.True(value.InFlight);
        }

        rig.FrameAt(OpenMs);

        Assert.Equal(0, value.Current);
        Assert.Equal(0, rig.Shown[^1]);
        Assert.False(value.InFlight);
        Assert.Equal(1, arrived);
        Assert.False(rig.Motion.Running);
    }

    /// <summary>A late frame costs smoothness, never time: the motion still lands, and only once.</summary>
    [Fact]
    public void AFrameThatComesLateStillLandsTheMotionOnce()
    {
        var rig = new Rig();
        var value = rig.Motion.Value(0, _ => { });
        var arrived = 0;

        value.To(1, CloseMs, PanelMotion.Accelerate, () => arrived++);
        rig.FrameAt(CloseMs * 3);
        rig.FrameAt(CloseMs * 4);

        Assert.Equal(1, value.Current);
        Assert.Equal(1, arrived);
    }

    [Fact]
    public void FramesAreAskedForOnlyWhileSomethingMoves()
    {
        var rig = new Rig();
        var value = rig.Motion.Value(0, _ => { });

        Assert.Empty(rig.Pump);

        value.To(1, OpenMs, PanelMotion.Decelerate);
        value.To(2, OpenMs, PanelMotion.Decelerate);   // a second start asks for nothing more
        Assert.Equal([true], rig.Pump);

        rig.FrameAt(OpenMs);
        Assert.Equal([true, false], rig.Pump);
    }

    /// <summary>
    /// F56's mechanism: a new motion takes no start value. It begins where the old one had got
    /// to, with no frame at either end, and the old one's completion, the window's hide on a
    /// leave, never runs.
    /// </summary>
    [Fact]
    public void ATurnedRoundMotionContinuesFromWhatIsOnScreenAndDropsTheOldCompletion()
    {
        var rig = new Rig();
        var value = rig.Motion.Value(0, rig.Shown.Add);
        var hidden = false;
        var back = false;

        value.To(500, CloseMs, PanelMotion.Accelerate, () => hidden = true);
        rig.FrameAt(150);
        var midLeave = value.Current;
        Assert.InRange(midLeave, 1, 499);

        value.To(0, OpenMs, PanelMotion.Decelerate, () => back = true);
        Assert.Equal(midLeave, value.Current);

        rig.FrameAt(166);
        Assert.True(value.Current < midLeave, "the turned-round motion did not head back");
        Assert.True(midLeave - value.Current < 100, $"it jumped from {midLeave} to {value.Current}");

        rig.FrameAt(150 + OpenMs);

        Assert.Equal(0, value.Current);
        Assert.True(back);
        Assert.False(hidden, "the replaced leave still ran its completion, which would hide a panel that came back");
        Assert.DoesNotContain(rig.Shown, v => v > 500 || v < 0);
    }

    /// <summary>The other direction: an arrival interrupted by a leave leaves from where it had got to.</summary>
    [Fact]
    public void ALeaveDuringAnArrivalLeavesFromWhereTheArrivalHadGot()
    {
        var rig = new Rig();
        var value = rig.Motion.Value(500, rig.Shown.Add);
        var hidden = false;

        value.To(0, OpenMs, PanelMotion.Decelerate);
        rig.FrameAt(40);
        var midArrival = value.Current;

        value.To(500, CloseMs, PanelMotion.Accelerate, () => hidden = true);
        Assert.Equal(midArrival, value.Current);

        rig.FrameAt(40 + CloseMs);
        Assert.Equal(500, value.Current);
        Assert.True(hidden);
    }

    [Fact]
    public void ADelayHoldsTheValueUntilItHasPassed()
    {
        var rig = new Rig();
        var value = rig.Motion.Value(0, _ => { });

        value.To(1, 100, PanelMotion.Decelerate, delayMs: 50);

        rig.FrameAt(49);
        Assert.Equal(0, value.Current);

        rig.FrameAt(100);
        Assert.Equal(PanelMotion.Decelerate.ValueAt(0.5), value.Current, precision: 9);

        rig.FrameAt(150);
        Assert.Equal(1, value.Current);
    }

    [Fact]
    public void SettingAValueStopsItsMotionAndDropsTheCompletion()
    {
        var rig = new Rig();
        var value = rig.Motion.Value(0, _ => { });
        var arrived = false;

        value.To(1, OpenMs, PanelMotion.Decelerate, () => arrived = true);
        value.Set(0.25);
        rig.FrameAt(OpenMs * 2);

        Assert.Equal(0.25, value.Current);
        Assert.False(arrived);
        Assert.False(rig.Motion.Running);
        Assert.Equal([true, false], rig.Pump);
    }

    /// <summary>
    /// F55's rule for Linux: with the desktop's animations off nothing runs. The end value is on
    /// screen and the completion has run before the call returns, and no frame is ever asked for.
    /// </summary>
    [Fact]
    public void WithTheDesktopsAnimationsOffEveryChangeIsAppliedInOneStep()
    {
        var rig = new Rig(animationsOn: false);
        var value = rig.Motion.Value(500, rig.Shown.Add);
        var arrived = false;

        value.To(0, OpenMs, PanelMotion.Decelerate, () => arrived = true);

        Assert.Equal(0, value.Current);
        Assert.True(arrived);
        Assert.False(value.InFlight);
        Assert.Empty(rig.Pump);
        Assert.Equal([500, 0], rig.Shown);
    }

    /// <summary>The setting is asked at the start of every motion, so changing it needs no restart.</summary>
    [Fact]
    public void TheSettingIsAskedAtTheStartOfEveryMotion()
    {
        var on = true;
        var asked = 0;
        var rig = new Rig(() =>
        {
            asked++;
            return on;
        });
        var value = rig.Motion.Value(0, _ => { });

        value.To(1, OpenMs, PanelMotion.Decelerate);
        Assert.True(value.InFlight);

        on = false;
        value.To(0, CloseMs, PanelMotion.Accelerate);

        Assert.Equal(2, asked);
        Assert.False(value.InFlight);
        Assert.Equal(0, value.Current);
    }

    [Fact]
    public void AZeroLengthMotionIsAppliedInOneStep()
    {
        var rig = new Rig();
        var value = rig.Motion.Value(0, _ => { });

        value.To(1, 0, PanelMotion.Decelerate);

        Assert.Equal(1, value.Current);
        Assert.Empty(rig.Pump);
    }

    // ── the panel appearing and leaving ─────────────────────────────────────────────

    /// <summary>
    /// The Windows panel's figures and curves, read from the shared file and never copied: 460 ms
    /// in on the decelerating curve, 300 ms out on the accelerating one. Pinned on the source,
    /// because a window cannot be built here.
    /// </summary>
    [Fact]
    public void ThePanelArrivesAndLeavesWithTheWindowsSurfaceFigures()
    {
        var window = Source("Panel", "PanelWindow.cs");

        Assert.Equal(2, Regex.Matches(window,
            @"PanelMotion\.Ms\(PanelMotion\.SurfaceOpenBaseMs\),\s*PanelMotion\.Decelerate").Count);
        Assert.Single(Regex.Matches(window,
            @"PanelMotion\.Ms\(PanelMotion\.SurfaceCloseBaseMs\),\s*PanelMotion\.Accelerate"));

        Assert.Equal(OpenMs, PanelMotion.Ms(PanelMotion.SurfaceOpenBaseMs));
        Assert.Equal(CloseMs, PanelMotion.Ms(PanelMotion.SurfaceCloseBaseMs));

        // No figure of its own: a literal here would be a second copy of the Windows timing.
        Assert.DoesNotMatch(@"\.To\([^;]*\b\d{2,}\b", window);
    }

    /// <summary>
    /// The window itself never moves for the motion, the content does: native Wayland refuses a
    /// client that moves its window. And the window is hidden only once the content has left, so
    /// the panel's one hide is the leave's completion.
    /// </summary>
    [Fact]
    public void TheContentMovesAndTheWindowIsHiddenOnlyOnceItHasLeft()
    {
        var window = WithoutComments(Source("Panel", "PanelWindow.cs"));

        var showOffset = Body(window, "ShowOffset");
        Assert.Contains("_content.RenderTransform", showOffset, StringComparison.Ordinal);
        Assert.DoesNotContain("Position", showOffset, StringComparison.Ordinal);

        Assert.Single(Regex.Matches(window, @"\bHide\(\);"));
        Assert.Contains("Hide();", Body(window, "Leave"), StringComparison.Ordinal);
        Assert.Contains("void Dismiss(string how) => Leave(how);", window, StringComparison.Ordinal);
    }

    /// <summary>
    /// F56 at the tray icon: a click asks whether the panel is open or arriving, not whether its
    /// window is mapped, closes through the leave, and turns a leaving panel round rather than
    /// building a new one. The focus loss reaches the toggle the same way.
    /// </summary>
    [Fact]
    public void ATrayClickDuringAnArrivalOrALeaveTurnsThePanelRound()
    {
        var app = WithoutComments(Source("LinuxApp.cs"));
        var click = Body(app, "OnTrayClicked");

        Assert.Contains("_toggle.Clicked(_panel?.IsOpen == true", click, StringComparison.Ordinal);
        Assert.Contains("_panel!.Leave(\"tray click\")", click, StringComparison.Ordinal);
        Assert.DoesNotContain(".Hide()", click, StringComparison.Ordinal);

        var comeBack = click.IndexOf("_panel?.ComeBack()", StringComparison.Ordinal);
        Assert.True(comeBack >= 0, "a click on a leaving panel no longer turns it round");
        Assert.True(comeBack < click.IndexOf("ShowPanel(shell)", StringComparison.Ordinal),
            "a fresh panel is built before a leaving one is turned round");

        Assert.Contains("_toggle.Deactivated(panel.IsOpen,", app, StringComparison.Ordinal);
        Assert.Contains("() => _animations.AnimationsEnabled", app, StringComparison.Ordinal);
    }

    /// <summary>Docked at the bottom, the content rises in and sinks out; at the top it drops in.</summary>
    [Theory]
    [InlineData(612, true)]    // the corner above a bottom bar
    [InlineData(56, false)]    // the corner under GNOME's top bar
    [InlineData(220, true)]    // dragged so its middle is just below the work area's
    [InlineData(200, false)]   // and just above it
    public void APlacedPanelComesFromTheNearerEdge(double topDip, bool fromBottom)
    {
        var workArea = new DipBox(0, 56, 1024, 712);   // middle at 412

        Assert.Equal(fromBottom, PanelWindow.SitsLow(topDip, 400, workArea));
    }

    // ── the desktop's setting ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("org.gnome.desktop.interface", "enable-animations", true, true)]
    [InlineData("org.gnome.desktop.interface", "enable-animations", false, false)]
    [InlineData("org.freedesktop.appearance", "reduced-motion", 0u, true)]
    [InlineData("org.freedesktop.appearance", "reduced-motion", 1u, false)]
    [InlineData("org.kde.kdeglobals.KDE", "AnimationDurationFactor", 0.0, false)]
    [InlineData("org.kde.kdeglobals.KDE", "AnimationDurationFactor", 0.5, true)]
    [InlineData("org.kde.kdeglobals.KDE", "AnimationDurationFactor", "0", false)]
    [InlineData("org.kde.kdeglobals.KDE", "AnimationDurationFactor", "1", true)]
    public void EachDesktopsKeyReadsAsOnOrOff(string ns, string key, object value, bool on)
    {
        Assert.Equal(on, PortalAnimationSetting.Interpret(ns, key, value));
    }

    /// <summary>A value of a shape nobody published, or a key nobody asked for, is not an answer.</summary>
    [Theory]
    [InlineData("org.gnome.desktop.interface", "enable-animations", "false")]
    [InlineData("org.freedesktop.appearance", "reduced-motion", true)]
    [InlineData("org.kde.kdeglobals.KDE", "AnimationDurationFactor", "slow")]
    [InlineData("org.freedesktop.appearance", "color-scheme", 1u)]
    [InlineData("org.gnome.desktop.interface", "enable-animations", null)]
    public void AnythingElseIsNotAnAnswer(string ns, string key, object? value)
    {
        Assert.Null(PortalAnimationSetting.Interpret(ns, key, value));
    }

    /// <summary>
    /// Never a default other than on: with nothing read the panel animates, and it stops only when
    /// a key the desktop published says off.
    /// </summary>
    [Fact]
    public void AnimationsAreOnUntilTheDesktopSaysOff()
    {
        var setting = new PortalAnimationSetting();

        Assert.True(PortalAnimationSetting.Decide([]));
        Assert.StartsWith("on (no animation setting read", setting.Describe(), StringComparison.Ordinal);

        setting.OnSettingChanged("org.gnome.desktop.interface", "enable-animations", false);
        Assert.StartsWith("off (org.gnome.desktop.interface enable-animations=off)", setting.Describe(), StringComparison.Ordinal);

        // Switched back on, which is a signal like any other.
        setting.OnSettingChanged("org.gnome.desktop.interface", "enable-animations", true);
        Assert.StartsWith("on (", setting.Describe(), StringComparison.Ordinal);

        // A key outside the three is ignored, even one in the same namespace.
        setting.OnSettingChanged("org.gnome.desktop.interface", "gtk-theme", "Adwaita");
        Assert.DoesNotContain("gtk-theme", setting.Describe(), StringComparison.Ordinal);
    }

    /// <summary>Any key that says off wins, so a desktop publishing two keys is honoured when either asks for less motion.</summary>
    [Fact]
    public void AnyKeyThatSaysOffTurnsAnimationsOff()
    {
        Assert.False(PortalAnimationSetting.Decide([true, false]));

        var setting = new PortalAnimationSetting();
        setting.OnSettingChanged("org.gnome.desktop.interface", "enable-animations", true);
        setting.OnSettingChanged("org.freedesktop.appearance", "reduced-motion", 1u);
        Assert.StartsWith("off (", setting.Describe(), StringComparison.Ordinal);

        // A key that stops being an answer is forgotten rather than left at its last value.
        setting.OnSettingChanged("org.freedesktop.appearance", "reduced-motion", "garbled");
        Assert.StartsWith("on (", setting.Describe(), StringComparison.Ordinal);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────

    /// <summary>A motion on a hand-driven clock, recording what it showed and when it asked for frames.</summary>
    private sealed class Rig
    {
        private double _nowMs;

        public Rig(bool animationsOn = true)
            : this(() => animationsOn)
        {
        }

        public Rig(Func<bool> animationsOn)
        {
            Motion = new LinuxMotion(animationsOn, () => TimeSpan.FromMilliseconds(_nowMs), Pump.Add);
        }

        public LinuxMotion Motion { get; }

        public List<bool> Pump { get; } = [];

        public List<double> Shown { get; } = [];

        public void FrameAt(double ms)
        {
            _nowMs = ms;
            Motion.Tick();
        }
    }

    private static string Source(params string[] underTheHead) =>
        File.ReadAllText(Path.Combine([RepositoryRoot(), "src", "O-view.Linux", .. underTheHead]));

    private static string WithoutComments(string source) => Regex.Replace(source, @"//[^\n]*", string.Empty);

    /// <summary>A method's body, from its name to the closing brace at its own indentation.</summary>
    private static string Body(string source, string method)
    {
        var match = Regex.Match(source, $@"\b{method}\([^)]*\)\s*\{{(?<body>.*?)\n    \}}", RegexOptions.Singleline);
        Assert.True(match.Success, $"no method {method} found");
        return match.Groups["body"].Value;
    }

    /// <summary>
    /// Walks up from the test binary to the directory holding the solution file, the way
    /// <see cref="PanelDismissalTests"/> does.
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
}
