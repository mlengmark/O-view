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
    /// Depth ceiling. Junctions can point at their own ancestors, which makes the tree
    /// infinitely deep even though <see cref="_visited"/> stops exact revisits. Real
    /// layouts here are 3–5 levels, so this only ever trips on a loop.
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

        // Guards against a junction that resolves back to a directory already walked.
        // Keyed by platform path identity, not OrdinalIgnoreCase: on Linux, Alpha/ and
        // alpha/ are two directories, and folding them together would skip the second
        // silently — every transcript in it simply missing from the totals.
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));

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
                    pending.Push((child, depth + 1));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return results;
    }
}
