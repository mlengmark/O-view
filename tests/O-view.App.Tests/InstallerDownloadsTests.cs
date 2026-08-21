using OView.App.Updates;

namespace OView.App.Tests;

/// <summary>
/// The temp-directory sweep (issue #159).
///
/// <para>What was actually wrong was not a bug in any one line — every line did what it said.
/// It was that the success path had no owner for the file it left behind, and could not have
/// one: the app hands the installer to another process and exits so its exe can be replaced.
/// Measured on a machine updating since v0.4.6, that came to 20 files and 1.33 GB.</para>
///
/// <para>So these assert the two properties the sweep has to hold at once: it removes what
/// this app downloaded, and it is never the reason an update fails.</para>
/// </summary>
public class InstallerDownloadsTests
{
    private static string Installer(TempDir dir, string version, int bytes = 16)
    {
        var path = dir.File($"O-view-Setup-{version}.exe");
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    /// <summary>
    /// The leak itself. Several releases' worth of installers, none of which anything else
    /// will ever remove.
    /// </summary>
    [Fact]
    public void EveryPreviouslyDownloadedInstallerIsRemoved()
    {
        using var dir = new TempDir();
        Installer(dir, "0.6.3");
        Installer(dir, "0.6.4");
        Installer(dir, "0.6.10");

        Assert.Equal(3, InstallerDownloads.Prune(dir.Path));

        Assert.Empty(Directory.GetFiles(dir.Path, InstallerDownloads.InstallerPattern));
    }

    /// <summary>
    /// Scoped to the pattern, not "empty the directory". <c>%TEMP%\O-view-update</c> is a
    /// path anything can write to, and a sweep that took everything would be destroying files
    /// this app never created and knows nothing about.
    /// </summary>
    [Fact]
    public void AFileThisAppDidNotDownloadIsLeftAlone()
    {
        using var dir = new TempDir();
        Installer(dir, "0.6.4");

        var stranger = dir.File("notes.txt");
        File.WriteAllText(stranger, "not ours");
        var otherApp = dir.File("SomethingElse-Setup-1.0.exe");
        File.WriteAllText(otherApp, "also not ours");

        Assert.Equal(1, InstallerDownloads.Prune(dir.Path));

        Assert.True(File.Exists(stranger));
        Assert.True(File.Exists(otherApp));
    }

    /// <summary>
    /// The guarantee that makes the sweep safe to put in front of a download: a file that
    /// cannot be deleted — realistically an installer still running from a previous attempt —
    /// is skipped, the rest still go, and nothing propagates to the caller. Failing an update
    /// because a stale file was locked would trade a disk leak for a machine stranded on an
    /// old version, which is the worse of the two.
    /// </summary>
    [Fact]
    public void AFileThatCannotBeDeletedDoesNotStopTheSweep()
    {
        using var dir = new TempDir();
        var locked = Installer(dir, "0.6.4");
        Installer(dir, "0.6.5");
        Installer(dir, "0.6.6");

        var removed = InstallerDownloads.Prune(dir.Path, delete: path =>
        {
            if (path == locked)
            {
                throw new IOException("The process cannot access the file because it is being used by another process.");
            }
            File.Delete(path);
        });

        Assert.Equal(2, removed);
        Assert.True(File.Exists(locked));
        Assert.Single(Directory.GetFiles(dir.Path, InstallerDownloads.InstallerPattern));
    }

    /// <summary>
    /// The same guarantee against a real lock rather than a stand-in. Only Windows holds an
    /// open file against deletion, and this is a Windows-only code path anyway — on Linux the
    /// unlink succeeds, which is not a failure of the sweep, so the assertion is that it
    /// worked rather than that it was skipped.
    /// </summary>
    [Fact]
    public void ARealOpenHandleIsSurvived()
    {
        using var dir = new TempDir();
        var held = Installer(dir, "0.6.4");
        Installer(dir, "0.6.5");

        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var removed = InstallerDownloads.Prune(dir.Path);

            Assert.Equal(OperatingSystem.IsWindows() ? 1 : 2, removed);
        }

        // Either way the other installer went, and the call returned rather than throwing.
        Assert.False(File.Exists(dir.File("O-view-Setup-0.6.5.exe")));
    }

    /// <summary>
    /// A first run has no directory yet. That is nothing to sweep, not a failure — and the
    /// caller creates it on the next line regardless.
    /// </summary>
    [Fact]
    public void AnAbsentDirectoryIsNotAnError()
    {
        using var dir = new TempDir();

        Assert.Equal(0, InstallerDownloads.Prune(Path.Combine(dir.Path, "never-created")));
    }

    /// <summary>
    /// The sweep and the download have to agree on where the directory is, or the sweep
    /// tidies somewhere nothing was ever written. Naming it once is what enforces that; this
    /// pins the value the Windows head is compiled against.
    /// </summary>
    [Fact]
    public void TheSweptDirectoryIsTheOneUpdatesAreDownloadedTo()
    {
        Assert.Equal(
            Path.Combine(Path.GetTempPath(), "O-view-update"),
            InstallerDownloads.DefaultDirectory);
    }

    /// <summary>Nothing is logged on the ordinary case of an already-clean directory.</summary>
    [Fact]
    public void ACleanDirectoryIsSweptSilently()
    {
        using var dir = new TempDir();
        var log = new ListLog();

        Assert.Equal(0, InstallerDownloads.Prune(dir.Path, log));

        Assert.Empty(log.Lines);
    }
}
