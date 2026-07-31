namespace OView.Core.Providers.Jsonl;

/// <summary>
/// Recursive file search that survives an unreadable directory instead of abandoning
/// the whole walk.
///
/// <see cref="Directory.GetFiles(string, string, SearchOption)"/> with
/// <see cref="SearchOption.AllDirectories"/> aborts the entire enumeration on the first
/// node it cannot read, and the natural `catch (IOException) → return []` around it then
/// reports "no transcripts" rather than "one folder was skipped". The Cowork tree
/// contains a broken directory junction (a `…-outputs` link whose target is gone), so
/// that shape would zero every token in the app the moment Cowork was scanned —
/// silently, because <see cref="DirectoryNotFoundException"/> derives from
/// <see cref="IOException"/> and looks like an ordinary miss (GitHub issue #44).
///
/// Walking directory by directory keeps a bad node local to itself.
/// </summary>
public static class TranscriptFileScan
{
    /// <summary>
    /// Depth ceiling, kept as a backstop even though links are now resolved before they are
    /// walked. Resolution can fail — a permission error, a racing rename — and when it does
    /// this is the only thing standing between a link loop and an unbounded walk. Real
    /// layouts here are 3–5 levels, so it only ever trips on something pathological.
    /// </summary>
    private const int MaxDepth = 32;

    /// <summary>
    /// Every file matching <paramref name="searchPattern"/> under <paramref name="root"/>.
    /// Empty if the root does not exist. Never throws.
    /// </summary>
    public static IReadOnlyList<string> Find(string root, string searchPattern)
    {
        var results = new List<string>();

        // Directory.Exists swallows its own errors and returns false, so it needs no
        // guard here; the per-directory try blocks below are what make the walk safe.
        if (!Directory.Exists(root))
        {
            return results;
        }

        // Guards against a link that leads back to a directory already walked.
        //
        // Keyed by platform path identity, not OrdinalIgnoreCase: on Linux, Alpha/ and
        // alpha/ are two directories, and folding them together would skip the second
        // silently — every transcript in it simply missing from the totals.
        //
        // It only works because links are resolved BEFORE being pushed (see Canonical), so
        // every path in here is already canonical. Comparing paths as walked would not
        // catch a symlink loop at all: `a/link -> ../..` generates
        // a/link/a/link/a/link/... — textually distinct every time, so the set never hits,
        // and only MaxDepth would stop it after a very large number of directories.
        var visited = new HashSet<string>(PathIdentity.Comparer);
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((Canonical(root), 0));

        while (pending.Count > 0)
        {
            var (dir, depth) = pending.Pop();

            if (!visited.Add(dir))
            {
                continue;
            }

            // Files and subdirectories are read in separate try blocks on purpose: a
            // directory whose *children* cannot be listed may still yield its own files.
            try
            {
                results.AddRange(Directory.EnumerateFiles(dir, searchPattern));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable node (broken junction, permissions) — skip just this one.
            }

            if (depth >= MaxDepth)
            {
                continue;
            }

            try
            {
                foreach (var child in Directory.EnumerateDirectories(dir))
                {
                    pending.Push((Canonical(child), depth + 1));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return results;
    }

    /// <summary>
    /// The directory a path actually refers to, following a symlink or junction to its
    /// final target.
    ///
    /// <para>Resolving here — at the moment a directory is queued, rather than when it is
    /// compared — is what keeps the whole walk in canonical space. It means a link back to
    /// an ancestor is recognised on the very next pop, instead of generating an ever-longer
    /// chain of distinct-looking paths that the visited set can never match.</para>
    ///
    /// <para>Falls back to the path as given whenever resolution says nothing useful:
    /// <see cref="Directory.ResolveLinkTarget"/> returns null for an ordinary directory,
    /// and throws for a dangling link whose target is gone. Neither is an error here — a
    /// dangling link simply fails to enumerate a moment later, which the per-directory
    /// catch already handles and which is how a broken junction was made survivable in the
    /// first place (issue #44).</para>
    /// </summary>
    private static string Canonical(string directory)
    {
        try
        {
            return Directory.ResolveLinkTarget(directory, returnFinalTarget: true)?.FullName
                   ?? directory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return directory;
        }
    }
}
