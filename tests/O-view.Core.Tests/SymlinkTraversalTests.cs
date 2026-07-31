using OView.Core.Providers.Jsonl;

namespace OView.Core.Tests;

/// <summary>
/// Link traversal in the transcript walk. The Windows shape of this — a broken junction
/// zeroing an entire scan — is covered by
/// <see cref="CoworkIngestionTests.BrokenJunction_SkipsThatNodeOnly_DoesNotZeroTheScan"/>
/// (issue #44). These are the Linux shapes, which behave differently enough to need their
/// own cases: symlinks can point at their own ancestors, and a dangling one resolves to a
/// path that simply is not there.
/// </summary>
public class SymlinkTraversalTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("oview-symlink-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Creating a symlink needs elevation or Developer Mode on Windows. Linux never fails
    /// here, so callers assert that a failure could only have been Windows — a silent skip
    /// on Linux would hide the very cases these tests exist for.
    /// </summary>
    private static bool TryCreateSymlink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void SkipOnlyIfWindows() =>
        Assert.True(
            OperatingSystem.IsWindows(),
            "symlink creation must succeed on Linux — a skip here would hide the case under test");

    /// <summary>
    /// The loop. <c>deep/link -> root</c> makes the tree infinitely deep, and every path
    /// through it looks different from the last: root/deep/link/deep/link/... Comparing
    /// paths as walked never matches, so before links were resolved at push time only the
    /// depth ceiling stopped this — after enumerating an enormous number of directories.
    /// </summary>
    [Fact]
    public void SymlinkLoopTerminatesAndStillFindsEveryTranscript()
    {
        var root = Path.Combine(_dir, "projects");
        var deep = Path.Combine(root, "deep");
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(root, "top.jsonl"), "{}");
        File.WriteAllText(Path.Combine(deep, "inner.jsonl"), "{}");

        if (!TryCreateSymlink(Path.Combine(deep, "link"), root))
        {
            SkipOnlyIfWindows();
            return;
        }

        var found = TranscriptFileScan.Find(root, "*.jsonl");

        // Terminates, and each real file appears exactly once despite being reachable
        // through the loop as well as directly.
        Assert.Equal(2, found.Count);
    }

    /// <summary>A link pointing at its own parent — the tightest possible loop.</summary>
    [Fact]
    public void SelfReferentialLinkDoesNotRepeatFiles()
    {
        var root = Path.Combine(_dir, "self");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "one.jsonl"), "{}");

        if (!TryCreateSymlink(Path.Combine(root, "me"), root))
        {
            SkipOnlyIfWindows();
            return;
        }

        var found = TranscriptFileScan.Find(root, "*.jsonl");

        Assert.Single(found);
    }

    /// <summary>
    /// The Linux restatement of issue #44: one unreadable node must cost only itself, not
    /// the whole tree. A dangling symlink resolves to a path that does not exist, so
    /// enumerating it throws <see cref="DirectoryNotFoundException"/> — an
    /// <see cref="IOException"/>, which is exactly the shape that used to swallow an entire
    /// scan into "no transcripts on this machine".
    /// </summary>
    [Fact]
    public void DanglingSymlinkCostsOnlyItself()
    {
        var root = Path.Combine(_dir, "dangling-root");
        var sibling = Path.Combine(root, "real");
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "kept.jsonl"), "{}");

        if (!TryCreateSymlink(Path.Combine(root, "gone"), Path.Combine(_dir, "never-existed")))
        {
            SkipOnlyIfWindows();
            return;
        }

        var found = TranscriptFileScan.Find(root, "*.jsonl");

        Assert.Single(found);
        Assert.EndsWith("kept.jsonl", found[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// Two routes to the same directory must not count its files twice. Ingestion
    /// de-duplicates by request id so totals would survive, but the scope report counts
    /// *files* and would overstate the evidence (issue #58).
    /// </summary>
    [Fact]
    public void TwoRoutesToOneDirectoryYieldItsFilesOnce()
    {
        var root = Path.Combine(_dir, "aliased");
        var real = Path.Combine(root, "real");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "once.jsonl"), "{}");

        if (!TryCreateSymlink(Path.Combine(root, "alias"), real))
        {
            SkipOnlyIfWindows();
            return;
        }

        var found = TranscriptFileScan.Find(root, "*.jsonl");

        Assert.Single(found);
    }

    /// <summary>
    /// A link to a directory that is NOT otherwise reachable must still be followed — the
    /// loop guard must not turn into "ignore links".
    /// </summary>
    [Fact]
    public void ALinkToAnUnrelatedDirectoryIsStillFollowed()
    {
        var root = Path.Combine(_dir, "reach");
        Directory.CreateDirectory(root);
        var outside = Path.Combine(_dir, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "elsewhere.jsonl"), "{}");

        if (!TryCreateSymlink(Path.Combine(root, "out"), outside))
        {
            SkipOnlyIfWindows();
            return;
        }

        var found = TranscriptFileScan.Find(root, "*.jsonl");

        Assert.Single(found);
        Assert.EndsWith("elsewhere.jsonl", found[0], StringComparison.Ordinal);
    }
}
