using System.Diagnostics;
using System.Text.RegularExpressions;
using OView.Linux.Platform;

namespace OView.Linux.Tests;

/// <summary>
/// The theme seam froze v0.6.1 on the first left click (issue #124): it waited on a D-Bus
/// round trip from Avalonia's UI thread, and the reply continuation was posted back to the
/// thread already blocked waiting for it.
///
/// <para>These tests cannot open a panel — there is no desktop here — but they can assert
/// the two properties whose absence caused it: that a read returns promptly with no bus to
/// talk to, and that nothing in the head waits synchronously on a task anywhere the
/// dispatcher can reach.</para>
/// </summary>
public class PortalThemeSourceTests
{
    /// <summary>
    /// CI has no session bus and no portal, which is the harshest case and the one that
    /// used to hang: with a bus present the call at least completes.
    ///
    /// <para>The bound is deliberately loose. It is not measuring how fast the lookup is —
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
            $"a theme read took {watch.ElapsedMilliseconds} ms — it is talking to the bus on the caller's thread");
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
            $"500 theme reads took {watch.ElapsedMilliseconds} ms — reads are not served from the cache");
    }

    /// <summary>
    /// Priming is the one call allowed to wait, and it must still terminate on a machine
    /// with nothing to answer it — otherwise a broken portal hangs startup instead of the
    /// first click.
    /// </summary>
    [Fact]
    public async Task PrimingTerminatesWithNoPortalPresent()
    {
        await PortalThemeSource.PrimeAsync().WaitAsync(TimeSpan.FromSeconds(20));
    }

    /// <summary>
    /// The structural guard, and the test that would actually have caught #124.
    ///
    /// <para>Both tests above pass against the broken implementation on this machine, because
    /// with no bus the call fails fast and never reaches the deadlock. The deadlock needs a
    /// live session bus <i>and</i> a dispatcher, which is precisely the combination no CI
    /// runner has — so the property has to be asserted over the source rather than observed
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
    /// run from <c>bin/</c>, several levels below, and the depth differs by configuration —
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
