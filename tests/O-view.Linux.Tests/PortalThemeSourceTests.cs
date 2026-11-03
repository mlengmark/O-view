using System.Diagnostics;
using System.Text.RegularExpressions;
using OView.Linux.Platform;

namespace OView.Linux.Tests;

/// <summary>
/// The theme seam froze v0.6.1 on the first left click (issue #124): it waited on a D-Bus
/// round trip from Avalonia's UI thread, and the reply continuation was posted back to the
/// thread already blocked waiting for it.
///
/// <para>These tests cannot open a panel, there is no desktop here, but they can assert
/// the two properties whose absence caused it: that a read returns promptly with no bus to
/// talk to, and that nothing in the head waits synchronously on a task anywhere the
/// dispatcher can reach.</para>
/// </summary>
public class PortalThemeSourceTests
{
    private const string Appearance = "org.freedesktop.appearance";
    private const string ColorScheme = "color-scheme";

    /// <summary>The portal's <c>color-scheme</c> values.</summary>
    private const uint NoPreference = 0;
    private const uint PreferDark = 1;
    private const uint PreferLight = 2;

    /// <summary>
    /// CI has no session bus and no portal, which is the harshest case and the one that
    /// used to hang: with a bus present the call at least completes.
    ///
    /// <para>The bound is deliberately loose. It is not measuring how fast the lookup is,
    /// it is distinguishing "returned" from "never returns", and a generous ceiling keeps
    /// that distinction from turning into a flake on a loaded runner.</para>
    /// </summary>
    [Fact]
    public void AReadReturnsPromptlyWithNoBusToAsk()
    {
        var source = new PortalThemeSource();

        // First read primes nothing and must still return: this is the panel-open path.
        var watch = Stopwatch.StartNew();
        source.IsPanelLight();
        source.IsTrayLight();
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1),
            $"a theme read took {watch.ElapsedMilliseconds} ms, it is talking to the bus on the caller's thread");
    }

    /// <summary>
    /// Repeated reads are what the tray does every 60 s and the panel does on every open.
    /// None of them may wait, and none may queue another bus call behind the last.
    /// </summary>
    [Fact]
    public void RepeatedReadsNeverWait()
    {
        var source = new PortalThemeSource();

        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 500; i++)
        {
            source.IsPanelLight();
        }
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1),
            $"500 theme reads took {watch.ElapsedMilliseconds} ms, reads are not served from the cache");
    }

    /// <summary>
    /// Priming is the one call allowed to wait, and it must still terminate on a machine
    /// with nothing to answer it, otherwise a broken portal hangs startup instead of the
    /// first click.
    /// </summary>
    [Fact]
    public async Task PrimingTerminatesWithNoPortalPresent()
    {
        await PortalThemeSource.PrimeAsync().WaitAsync(TimeSpan.FromSeconds(20));
    }

    /// <summary>
    /// A desktop switched between light and dark used to show on the second panel open,
    /// not the first (issue #357): each read only started a refresh for the read after it.
    /// The portal's signal is what changes the cache now, so the very next read has it, with
    /// no priming and nothing asked.
    /// </summary>
    [Fact]
    public void ASignalAloneFlipsTheNextRead()
    {
        var source = new PortalThemeSource();

        PortalThemeSource.OnSettingChanged(Appearance, ColorScheme, PreferLight);
        Assert.True(source.IsPanelLight());
        Assert.True(source.IsTrayLight());

        PortalThemeSource.OnSettingChanged(Appearance, ColorScheme, PreferDark);
        Assert.False(source.IsPanelLight());
        Assert.False(source.IsTrayLight());
    }

    /// <summary>
    /// Once signals arrive, a read is a field load and nothing else. If reads still polled,
    /// a refresh would ask a bus that is not here, fall closed to dark and overwrite the
    /// light the signal reported, so a read answered by the bus shows up as a flip.
    /// </summary>
    [Fact]
    public async Task NoReadAfterASignalIsAnsweredByTheBus()
    {
        var source = new PortalThemeSource();
        PortalThemeSource.OnSettingChanged(Appearance, ColorScheme, PreferLight);

        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromMilliseconds(500))
        {
            Assert.True(source.IsPanelLight(), "a read after the signal was answered by a refresh, not the signal");
            await Task.Delay(5);
        }
    }

    /// <summary>
    /// The portal raises <c>SettingChanged</c> for every setting it carries, accent colour
    /// and contrast among them. Only the colour scheme may move the theme.
    /// </summary>
    [Fact]
    public void OtherSettingsLeaveTheThemeAlone()
    {
        var source = new PortalThemeSource();
        PortalThemeSource.OnSettingChanged(Appearance, ColorScheme, PreferLight);

        PortalThemeSource.OnSettingChanged(Appearance, "contrast", PreferDark);
        PortalThemeSource.OnSettingChanged("org.gnome.desktop.interface", ColorScheme, PreferDark);

        Assert.True(source.IsPanelLight());
    }

    /// <summary>
    /// "No preference" is not an answer, on the signal as on a read: it goes to the gsettings
    /// fallback and, with nothing to read here, fails closed to dark. That lookup runs off the
    /// bus's thread, so the theme follows shortly after rather than within the call.
    /// </summary>
    [Fact]
    public async Task NoPreferenceFallsBackAsAReadDoes()
    {
        var source = new PortalThemeSource();
        PortalThemeSource.OnSettingChanged(Appearance, ColorScheme, PreferLight);

        PortalThemeSource.OnSettingChanged(Appearance, ColorScheme, NoPreference);

        var watch = Stopwatch.StartNew();
        while (source.IsPanelLight() && watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(20);
        }

        Assert.False(source.IsPanelLight(), "\"no preference\" with nothing to fall back on did not fail closed to dark");
    }

    /// <summary>
    /// The structural guard, and the test that would actually have caught #124.
    ///
    /// <para>Both tests above pass against the broken implementation on this machine, because
    /// with no bus the call fails fast and never reaches the deadlock. The deadlock needs a
    /// live session bus <i>and</i> a dispatcher, which is precisely the combination no CI
    /// runner has, so the property has to be asserted over the source rather than observed
    /// at runtime.</para>
    ///
    /// <para><c>Program.Main</c> is the sole exemption, and a narrow one: it runs before the
    /// toolkit starts, so there is no dispatcher to block. That exemption is the whole
    /// design (ADR-0013, findings item 5), so it is named here explicitly rather than left
    /// as a hole in the pattern.</para>
    /// </summary>
    [Fact]
    public void NothingInTheHeadWaitsOnATaskOutsideProgramMain()
    {
        var head = Path.Combine(RepositoryRoot(), "src", "O-view.Linux");
        var blocking = new Regex(@"GetAwaiter\(\)\s*\.\s*GetResult\(\)|\.Wait\(\)|\.Result\b", RegexOptions.Compiled);

        var offenders = Directory
            .EnumerateFiles(head, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => Path.GetFileName(f) != "Program.cs")
            .SelectMany(f => File.ReadLines(f)
                .Select((line, i) => (File: Path.GetFileName(f), Number: i + 1, Text: line.Trim()))
                .Where(l => blocking.IsMatch(l.Text)))
            .Select(l => $"{l.File}:{l.Number}  {l.Text}")
            .ToArray();

        Assert.True(offenders.Length == 0,
            "the Linux head may only wait on a task from Program.Main, before the toolkit starts "
            + $"(issue #124):{Environment.NewLine}{string.Join(Environment.NewLine, offenders)}");
    }

    /// <summary>
    /// Walks up from the test binary to the directory holding the solution file. The tests
    /// run from <c>bin/</c>, several levels below, and the depth differs by configuration,
    /// so the marker is searched for rather than counted to.
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
