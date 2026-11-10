using System.Text.RegularExpressions;
using OView.Linux.Panel;

namespace OView.Linux.Tests;

/// <summary>
/// The panel's dismissal rule: a window that has never been focused must not dismiss itself.
///
/// <para>These tests cannot open a panel, Avalonia raises neither <c>Activated</c> nor
/// <c>Deactivated</c> without a windowing subsystem, and CI has none. That is exactly why the
/// decision lives in <see cref="PanelDismissal"/> rather than inline in the event handler: it
/// puts the part that can be got wrong somewhere it can be tested.</para>
/// </summary>
public class PanelDismissalTests
{
    /// <summary>
    /// The bug this exists for. A compositor that refuses the activation deactivates a panel
    /// the user has never seen focused, and the unguarded handler hid it in the same frame it
    /// appeared, one flash, then a tray icon that looks dead.
    /// </summary>
    [Fact]
    public void ADeactivationBeforeAnyActivationDoesNotDismiss()
    {
        var dismissal = new PanelDismissal();
        dismissal.Opening();

        Assert.False(dismissal.ShouldHideOnDeactivated());
        Assert.Equal(1, dismissal.SuppressedDeactivations);
    }

    /// <summary>The ordinary path: focused, then clicked away from.</summary>
    [Fact]
    public void ADeactivationAfterActivationDismisses()
    {
        var dismissal = new PanelDismissal();
        dismissal.Opening();
        dismissal.Activated();

        Assert.True(dismissal.ShouldHideOnDeactivated());
        Assert.Equal(0, dismissal.SuppressedDeactivations);
    }

    /// <summary>
    /// A compositor may refuse more than once. Every refusal is counted, because the count is
    /// what the <c>--log</c> line reports and it is the only evidence a bug report can carry.
    /// </summary>
    [Fact]
    public void RepeatedRefusalsAreAllSuppressedAndCounted()
    {
        var dismissal = new PanelDismissal();
        dismissal.Opening();

        for (var i = 0; i < 5; i++)
        {
            Assert.False(dismissal.ShouldHideOnDeactivated());
        }

        Assert.Equal(5, dismissal.SuppressedDeactivations);
    }

    /// <summary>
    /// Focus arriving late restores normal dismissal. The panel is not stuck open for the rest
    /// of its life because the first activation was refused.
    /// </summary>
    [Fact]
    public void ActivationArrivingAfterARefusalRestoresDismissal()
    {
        var dismissal = new PanelDismissal();
        dismissal.Opening();

        Assert.False(dismissal.ShouldHideOnDeactivated());

        dismissal.Activated();
        Assert.True(dismissal.ShouldHideOnDeactivated());
    }

    /// <summary>
    /// Each open starts over. Not load-bearing today, because <c>LinuxApp</c> builds a fresh
    /// window per open, it is here so that reusing the window, an obvious later tidy-up and
    /// cheaper than rebuilding the tree, cannot silently carry the previous activation across
    /// and re-open the bug.
    /// </summary>
    [Fact]
    public void OpeningResetsTheState()
    {
        var dismissal = new PanelDismissal();
        dismissal.Opening();
        dismissal.Activated();
        Assert.True(dismissal.ShouldHideOnDeactivated());

        dismissal.Opening();
        Assert.False(dismissal.ShouldHideOnDeactivated());
    }

    /// <summary>
    /// The structural guard, in the spirit of the one issue #124 added. The policy is worth
    /// nothing if the window does not consult it, and an edit restoring the one-line handler
    /// would pass every test above without touching a single one of them.
    /// </summary>
    [Fact]
    public void PanelWindowDoesNotHideOnDeactivationWithoutConsultingThePolicy()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "O-view.Linux", "Panel", "PanelWindow.cs"));

        Assert.False(
            Regex.IsMatch(source, @"Deactivated\s*\+=\s*\([^)]*\)\s*=>\s*Hide\(\)"),
            "PanelWindow dismisses on any deactivation, including one arriving before the window was "
            + "ever focused. A compositor that refuses the activation then hides the panel in the frame "
            + "it appeared, which reads as a dead tray icon. Route it through PanelDismissal.");

        Assert.Contains("ShouldHideOnDeactivated", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// What a dismissal must never cost. Hiding the panel leaves a hidden window behind, the next
    /// open closes it before building its replacement, and on the lifetime's default shutdown mode,
    /// <c>OnLastWindowClose</c>, that close was the end of the process: the panel flashed up and
    /// the tray icon was gone, with nothing in the log to say why (issue #312).
    ///
    /// <para>Structural, like the guard above, and for the same reason: the mode is an argument to
    /// a call made once per entry path, so an edit dropping it would pass every other test here.
    /// What is in force at run time is a different fact, and the <c>linux-runtime</c> job asserts
    /// that one off the live lifetime.</para>
    /// </summary>
    [Fact]
    public void TheHeadStartsEveryLifetimeWithAnExplicitShutdownMode()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "O-view.Linux", "Program.cs"));

        var starts = Regex.Matches(source, @"StartWithClassicDesktopLifetime\((?<arguments>[^)]*)\)");
        Assert.NotEmpty(starts);

        foreach (Match start in starts)
        {
            var arguments = start.Groups["arguments"].Value;
            Assert.True(
                arguments.Contains(',', StringComparison.Ordinal),
                "the Linux head starts a desktop lifetime without saying when the process may end, "
                + "so it takes Avalonia's default, OnLastWindowClose, which ends a tray app the "
                + $"moment a dismissed panel is closed and rebuilt: {start.Value}");
        }

        Assert.Contains("ShutdownMode.OnExplicitShutdown", source, StringComparison.Ordinal);
    }

    // ── the tray icon's toggle ──────────────────────────────────────────────────────

    private static readonly DateTimeOffset T0 = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The figure is the Windows panel's, so the icon feels the same on both desktops.</summary>
    [Fact]
    public void TheToggleGraceIsTheWindowsFourHundredMilliseconds() =>
        Assert.Equal(TimeSpan.FromMilliseconds(400), TrayToggle.Grace);

    [Fact]
    public void AClickWithNothingOpenOpensThePanel() =>
        Assert.Equal(TrayClickOutcome.Open, new TrayToggle().Clicked(panelVisible: false, T0));

    /// <summary>The desktop delivered the click before any focus loss: the panel is on screen, and goes.</summary>
    [Fact]
    public void AClickWhileThePanelIsOpenClosesIt()
    {
        var toggle = Opened();

        Assert.Equal(TrayClickOutcome.Close, toggle.Clicked(panelVisible: true, T0));
    }

    /// <summary>
    /// The case the grace exists for: pressing the icon took the focus, the panel hid itself, and
    /// the click arrives a moment later to find it gone. That click is the close half of the
    /// toggle, not a request to open the panel again.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(150)]
    [InlineData(399)]
    public void AClickWithinTheGraceOfAFocusLossCloseCountsAsTheToggle(int afterMs)
    {
        var toggle = Opened();
        toggle.Deactivated(stillVisible: false, T0);

        Assert.Equal(
            TrayClickOutcome.AlreadyClosed,
            toggle.Clicked(panelVisible: false, T0.AddMilliseconds(afterMs)));
    }

    /// <summary>A click once the grace has run out is a deliberate new one, and opens the panel as before.</summary>
    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(5_000)]
    public void AClickAfterThePanelClosedOnItsOwnOpensItAsBefore(int afterMs)
    {
        var toggle = Opened();
        toggle.Deactivated(stillVisible: false, T0);

        Assert.Equal(TrayClickOutcome.Open, toggle.Clicked(panelVisible: false, T0.AddMilliseconds(afterMs)));
    }

    /// <summary>The close half is spent once: a second quick click opens the panel again.</summary>
    [Fact]
    public void TheCloseHalfIsSpentByOneClick()
    {
        var toggle = Opened();
        toggle.Deactivated(stillVisible: false, T0);

        Assert.Equal(TrayClickOutcome.AlreadyClosed, toggle.Clicked(panelVisible: false, T0.AddMilliseconds(50)));
        Assert.Equal(TrayClickOutcome.Open, toggle.Clicked(panelVisible: false, T0.AddMilliseconds(100)));
    }

    /// <summary>
    /// The other order: the click closed the panel first and the focus loss arrives after. It finds
    /// nothing open, so the next click, however quick, opens the panel rather than being eaten.
    /// </summary>
    [Fact]
    public void AFocusLossAfterTheClickClosedThePanelIsNotCounted()
    {
        var toggle = Opened();

        Assert.Equal(TrayClickOutcome.Close, toggle.Clicked(panelVisible: true, T0));
        toggle.Deactivated(stillVisible: false, T0.AddMilliseconds(5));

        Assert.Equal(TrayClickOutcome.Open, toggle.Clicked(panelVisible: false, T0.AddMilliseconds(100)));
    }

    /// <summary>
    /// A deactivation the panel ignored, a compositor refusing the activation or a drag settling,
    /// left it on screen. That is not a close, so a click then closes the panel rather than being
    /// taken as already done.
    /// </summary>
    [Fact]
    public void ADeactivationThePanelIgnoredIsNotAClose()
    {
        var toggle = Opened();
        toggle.Deactivated(stillVisible: true, T0);

        Assert.Equal(TrayClickOutcome.Close, toggle.Clicked(panelVisible: true, T0.AddMilliseconds(50)));
    }

    /// <summary>An open in between, from the menu's row, starts over: the old close is forgotten.</summary>
    [Fact]
    public void OpeningForgetsTheLastFocusLossClose()
    {
        var toggle = Opened();
        toggle.Deactivated(stillVisible: false, T0);
        toggle.Opened();

        Assert.Equal(TrayClickOutcome.Open, toggle.Clicked(panelVisible: false, T0.AddMilliseconds(50)));
    }

    /// <summary>
    /// The structural half, as for the dismissal rule above: the toggle is worth nothing if the
    /// icon's click does not consult it, or if the panel's focus loss never reaches it.
    /// </summary>
    [Fact]
    public void TheIconsClickConsultsTheToggleAndThePanelsFocusLossReachesIt()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "O-view.Linux", "LinuxApp.cs"));

        Assert.Contains("_tray.Clicked += TrayClickHandler(", source, StringComparison.Ordinal);
        Assert.Contains("_toggle.Clicked(", source, StringComparison.Ordinal);
        Assert.Contains("_toggle.Deactivated(", source, StringComparison.Ordinal);
        Assert.Contains("_toggle.Opened()", source, StringComparison.Ordinal);
    }

    private static TrayToggle Opened()
    {
        var toggle = new TrayToggle();
        toggle.Opened();
        return toggle;
    }

    /// <summary>
    /// Walks up from the test binary to the directory holding the solution file, the same
    /// approach <c>PortalThemeSourceTests</c> takes, and for the same reason: the depth below
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
}
