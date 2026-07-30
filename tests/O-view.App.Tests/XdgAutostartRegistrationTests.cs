using OView.App.Platform;

namespace OView.App.Tests;

/// <summary>
/// The Linux run-at-startup mechanism. Nothing here needs a Linux API — it is a text file
/// in a directory — so it is exercised on both CI platforms rather than only the one it
/// was written for.
/// </summary>
public class XdgAutostartRegistrationTests
{
    // Returned as the interface deliberately: Apply is a default interface method, so it is
    // reachable only through the contract — which is where that rule belongs, and how every
    // real caller holds it.
    private static IStartupRegistration Subject(TempDir dir, string? exe = "/usr/bin/o-view") =>
        new XdgAutostartRegistration(dir.Path, () => exe);

    [Fact]
    public void StartsDisabled()
    {
        using var dir = new TempDir();
        Assert.False(Subject(dir).IsEnabled());
    }

    [Fact]
    public void EnableWritesTheDesktopFileAndDisableRemovesIt()
    {
        using var dir = new TempDir();
        var subject = Subject(dir);

        Assert.True(subject.Enable());
        Assert.True(subject.IsEnabled());
        Assert.True(File.Exists(Path.Combine(dir.Path, "o-view.desktop")));

        Assert.True(subject.Disable());
        Assert.False(subject.IsEnabled());
        Assert.False(File.Exists(Path.Combine(dir.Path, "o-view.desktop")));
    }

    [Fact]
    public void EnableCreatesTheAutostartDirectoryWhenAbsent()
    {
        using var dir = new TempDir();
        var nested = Path.Combine(dir.Path, "config", "autostart");
        var subject = new XdgAutostartRegistration(nested, () => "/usr/bin/o-view");

        Assert.True(subject.Enable());
        Assert.True(subject.IsEnabled());
    }

    [Fact]
    public void DisableOnSomethingAlreadyAbsentIsSuccess()
    {
        using var dir = new TempDir();
        // Deleting what is not there leaves the machine in the requested state, so it is
        // success — reporting failure would make the settings tick flip back for no reason.
        Assert.True(Subject(dir).Disable());
    }

    [Fact]
    public void EnableFailsWhenTheExecutablePathIsUnknown()
    {
        using var dir = new TempDir();
        var subject = Subject(dir, exe: null);

        Assert.False(subject.Enable());
        Assert.False(subject.IsEnabled());
    }

    [Fact]
    public void EntryCarriesTheFieldsAutostartActuallyNeeds()
    {
        using var dir = new TempDir();
        Subject(dir).Enable();

        var text = File.ReadAllText(Path.Combine(dir.Path, "o-view.desktop"));

        Assert.StartsWith("[Desktop Entry]", text, StringComparison.Ordinal);
        Assert.Contains("Type=Application", text, StringComparison.Ordinal);
        Assert.Contains("Name=O-view", text, StringComparison.Ordinal);
        Assert.Contains("Terminal=false", text, StringComparison.Ordinal);
        // GNOME honours this to disable an entry without deleting it; an entry without it
        // can be ignored by the session while this class still reports "enabled".
        Assert.Contains("X-GNOME-Autostart-enabled=true", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ExecIsQuotedSoAPathWithSpacesStillLaunches()
    {
        using var dir = new TempDir();
        new XdgAutostartRegistration(dir.Path, () => "/opt/My Apps/o-view").Enable();

        var text = File.ReadAllText(Path.Combine(dir.Path, "o-view.desktop"));

        Assert.Contains("Exec=\"/opt/My Apps/o-view\"", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The shared rule from <see cref="IStartupRegistration.Apply"/>: report the state as it
    /// actually stands, never the state that was asked for (CLAUDE.md rule 6).
    /// </summary>
    [Fact]
    public void ApplyReportsTheStateThatActuallyResulted()
    {
        using var dir = new TempDir();

        Assert.True(Subject(dir).Apply(true));
        Assert.False(Subject(dir).Apply(false));

        // Enabling cannot succeed with no executable path, so Apply must answer false
        // rather than echoing the request back.
        Assert.False(Subject(dir, exe: null).Apply(true));
    }
}
