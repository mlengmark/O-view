using System.Text.RegularExpressions;
using OView.Linux.Panel;

namespace OView.Linux.Tests;

/// <summary>
/// The panel's dismissal rule: a window that has never been focused must not dismiss itself.
///
/// <para>These tests cannot open a panel — Avalonia raises neither <c>Activated</c> nor
/// <c>Deactivated</c> without a windowing subsystem, and CI has none. That is exactly why the
/// decision lives in <see cref="PanelDismissal"/> rather than inline in the event handler: it
/// puts the part that can be got wrong somewhere it can be tested.</para>
/// </summary>
public class PanelDismissalTests
{
    /// <summary>
    /// The bug this exists for. A compositor that refuses the activation deactivates a panel
    /// the user has never seen focused, and the unguarded handler hid it in the same frame it
    /// appeared — one flash, then a tray icon that looks dead.
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
    /// window per open — it is here so that reusing the window, an obvious later tidy-up and
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
    /// Walks up from the test binary to the directory holding the solution file — the same
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
