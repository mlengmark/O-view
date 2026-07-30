using OView.Core.Providers.Jsonl;

namespace OView.Core.Tests;

/// <summary>
/// Path identity differs by platform, and getting it wrong on Linux is silent: two
/// directories differing only in case get folded into one and the second is skipped —
/// no error, no warning, just missing usage.
///
/// <para>These tests assert the <b>correct behaviour for the platform they run on</b>
/// rather than skipping on one of them, so both CI legs verify their own answer.</para>
/// </summary>
public class PathIdentityTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("oview-pathid-").FullName;

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

    [Fact]
    public void ComparerMatchesTheFilesystemThisPlatformActuallyHas()
    {
        var same = PathIdentity.AreSame("/data/Alpha", "/data/alpha");

        if (OperatingSystem.IsWindows())
        {
            Assert.True(same, "Windows filesystems are case-insensitive; these are one path");
            Assert.Same(StringComparer.OrdinalIgnoreCase, PathIdentity.Comparer);
        }
        else
        {
            Assert.False(same, "Linux filesystems are case-sensitive; these are two paths");
            Assert.Same(StringComparer.Ordinal, PathIdentity.Comparer);
        }
    }

    [Fact]
    public void IdenticalPathsAreAlwaysTheSameEverywhere() =>
        Assert.True(PathIdentity.AreSame("/data/alpha", "/data/alpha"));

    [Fact]
    public void DifferentPathsAreNeverTheSameEverywhere() =>
        Assert.False(PathIdentity.AreSame("/data/alpha", "/data/beta"));

    [Fact]
    public void ComparisonAgreesWithComparer() =>
        Assert.Equal(
            PathIdentity.AreSame("/data/Alpha", "/data/alpha"),
            string.Equals("/data/Alpha", "/data/alpha", PathIdentity.Comparison));

    /// <summary>
    /// The regression, end to end. Two directories whose names differ only in case, each
    /// holding a transcript.
    ///
    /// <para>The assertion is the same on both platforms — <b>no transcript is lost</b> —
    /// but the mechanism differs. On Windows the two names are one directory holding both
    /// files; on Linux they are two directories holding one each. With the old
    /// <c>OrdinalIgnoreCase</c> visited set, Linux would report 1: the second directory
    /// looked already-walked and was skipped.</para>
    /// </summary>
    [Fact]
    public void CaseVaryingDirectoriesLoseNoTranscripts()
    {
        var root = Path.Combine(_dir, "projects");
        Directory.CreateDirectory(Path.Combine(root, "Alpha"));
        File.WriteAllText(Path.Combine(root, "Alpha", "a.jsonl"), "{}");
        Directory.CreateDirectory(Path.Combine(root, "alpha"));
        File.WriteAllText(Path.Combine(root, "alpha", "b.jsonl"), "{}");

        var found = TranscriptFileScan.Find(root, "*.jsonl");

        Assert.Equal(2, found.Count);
    }

    /// <summary>
    /// The guard the visited set exists for must still work: a directory reached twice is
    /// walked once. Uses a plain nested layout so it holds on both platforms without
    /// needing links.
    /// </summary>
    [Fact]
    public void EachDirectoryIsStillWalkedOnlyOnce()
    {
        var root = Path.Combine(_dir, "once");
        var nested = Path.Combine(root, "a", "b");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "only.jsonl"), "{}");

        var found = TranscriptFileScan.Find(root, "*.jsonl");

        Assert.Single(found);
    }
}
