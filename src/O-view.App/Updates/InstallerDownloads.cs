namespace OView.App.Updates;

/// <summary>
/// The temp directory auto-update downloads installers into, and the sweep that stops it
/// growing without bound (issue #159).
///
/// <para><b>Why anything is needed at all.</b> Each download is named from the version it
/// carries — <c>O-view-Setup-0.6.4.exe</c> — so every release lands as a new file rather than
/// overwriting the last. The success path deletes nothing and could not: the head hands the
/// file to the installer and exits immediately so the exe can be replaced underneath it, so
/// there is no moment after a successful update at which this process is still alive to tidy
/// up. At ~71 MB an installer and a release most days, that is on the order of 2 GB a month
/// on every machine with automatic updates on, in a tray app whose entire other on-disk
/// footprint is ~7 MB. <c>%TEMP%</c> is not self-cleaning — Storage Sense can reach it but is
/// off by default, and Disk Cleanup has to be run by hand.</para>
///
/// <para><b>So the sweep runs before the next download instead.</b> That is the one moment
/// this process is guaranteed to be alive and to know the directory matters. Everything
/// already there is safe to remove: the file about to be downloaded does not exist yet, and
/// any older installer either already ran or already failed. That makes deleting all of them
/// simpler and no more dangerous than a retention count.</para>
///
/// <para>Lives in <c>App</c> rather than beside the download in the Windows head so it can be
/// tested against a real temp directory without a desktop. The head owns the HTTP and the
/// hand-off; this owns the rule about what may be left behind.</para>
/// </summary>
public static class InstallerDownloads
{
    /// <summary>
    /// Matches only what this app downloaded. Scoped rather than emptying the directory, so a
    /// stray file someone else put there is not collateral.
    /// </summary>
    public const string InstallerPattern = "O-view-Setup-*.exe";

    /// <summary>Where <c>UpdateService</c> downloads to. Named here so the sweep cannot drift from it.</summary>
    public static string DefaultDirectory => Path.Combine(Path.GetTempPath(), "O-view-update");

    /// <summary>
    /// Deletes every installer already in <paramref name="directory"/>, returning how many
    /// went. Never throws.
    ///
    /// <para><b>Best-effort per file, deliberately.</b> A locked one — an installer still
    /// running from a previous attempt is the realistic case — is skipped rather than thrown
    /// from, and the rest are still swept. Housekeeping must never be the reason an update
    /// fails: the caller's next line downloads the installer the user is waiting for, and
    /// failing that because a stale file could not be removed would trade a disk leak for a
    /// machine stuck on an old version.</para>
    ///
    /// <para>A directory that does not exist is not a failure — there is nothing to sweep, and
    /// the caller creates it either way.</para>
    /// </summary>
    /// <param name="directory">Directory to sweep. Only <see cref="InstallerPattern"/> is touched.</param>
    /// <param name="log">Optional log, written to only when something was actually deleted.</param>
    /// <param name="delete">
    /// How to remove one file. The seam exists for the test that a file which cannot be
    /// deleted does not stop the sweep — the real failures are OS- and state-specific
    /// (a running exe on Windows, a read-only directory on Linux) and cannot be provoked the
    /// same way on both, whereas the guarantee under test is the same on both.
    /// </param>
    public static int Prune(string directory, IAppLog? log = null, Action<string>? delete = null)
    {
        delete ??= File.Delete;

        string[] stale;
        try
        {
            stale = Directory.GetFiles(directory, InstallerPattern);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        var removed = 0;
        long bytes = 0;

        foreach (var file in stale)
        {
            try
            {
                // Read before the delete: afterwards there is nothing left to measure, and
                // the size is the whole point of the log line.
                var length = new FileInfo(file).Length;
                delete(file);
                removed++;
                bytes += length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use, or not ours to remove. The next update sweeps again.
            }
        }

        if (removed > 0)
        {
            log?.Write($"pruned {removed} stale installer(s) from {directory} ({bytes / (1024 * 1024)} MB)");
        }

        return removed;
    }
}
