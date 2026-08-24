namespace OView.Core.Providers.Jsonl;

/// <summary>
/// Locates Cowork transcripts. Cowork runs each session in a sandboxed Claude home
/// under <c>&lt;claude-data-root&gt;\local-agent-mode-sessions\&lt;org&gt;\&lt;user&gt;\&lt;session&gt;\</c>,
/// which contains a <c>.claude\projects</c> directory that is always **empty** — the
/// transcript is written to <c>audit.jsonl</c> beside it instead.
///
/// That empty directory is why the gap went unnoticed: the folder
/// <see cref="ClaudeProjectsLocator"/> looks for does exist in the sandbox, it just never
/// holds anything. Cowork usage was therefore dropped entirely while the plan meters —
/// which are account-wide — kept counting it (GitHub issue #44).
///
/// Read-only, like every other provider input.
/// </summary>
public static class CoworkAuditLocator
{
    /// <summary>The one file name Cowork writes its transcript to.</summary>
    public const string AuditFileName = "audit.jsonl";

    /// <summary>Directory holding Cowork sessions, relative to a Claude data root.</summary>
    public const string SessionsDirectoryName = "local-agent-mode-sessions";

    /// <summary>
    /// Every Cowork session root worth scanning — one per Claude data root, so a
    /// packaged (MSIX) Desktop install is covered as well as an unpackaged one. Checking
    /// only <c>%APPDATA%</c> would find nothing at all on a machine where Desktop's data
    /// lives solely in its package store (<see cref="ClaudeDataRoots"/>).
    /// </summary>
    public static IReadOnlyList<string> DefaultRoots =>
        ClaudeDataRoots.All().Select(r => Path.Combine(r, SessionsDirectoryName)).ToList();

    /// <summary>
    /// All Cowork audit logs under one root. Empty if the root does not exist — a user
    /// who has never opened Cowork is the normal case, not an error.
    /// </summary>
    public static IReadOnlyList<string> FindAuditLogs(string root) =>
        TranscriptFileScan.Find(root, AuditFileName);

    /// <summary>
    /// All Cowork audit logs across several roots, with sessions exposed through more than
    /// one root counted once.
    ///
    /// <para>Roots legitimately expose the <i>same</i> sessions. MSIX write-redirection means
    /// <c>%APPDATA%\Claude</c> is not a directory at all but a redirect into
    /// <c>%LOCALAPPDATA%\Packages\…\LocalCache\Roaming\Claude</c>, so one set of files is
    /// visible through both paths — <c>fsutil hardlink list</c> on the canonical path prints
    /// the package path back, and the files match to the millisecond.</para>
    ///
    /// <para><b>Path text cannot detect this and neither can the platform.</b> The two paths
    /// differ as strings, so <c>Distinct</c> keeps both; and
    /// <c>Directory.ResolveLinkTarget(…, returnFinalTarget: true)</c> reports
    /// <i>not a link</i> for the redirect, because MSIX uses a reparse tag .NET does not
    /// classify as one (measured on net10.0, 2026-08-24). Real file identity would need
    /// <c>GetFileInformationByHandle</c>, which is Win32 and barred from Core.</para>
    ///
    /// <para>So identity is established from what the BCL does expose: the path <b>relative to
    /// its own root</b>. That is <c>&lt;org&gt;\&lt;user&gt;\&lt;session&gt;\audit.jsonl</c> — two
    /// UUIDs and a session id — so mirrored roots collapse exactly while two genuinely
    /// different sessions never share a key.</para>
    ///
    /// <para><b>Size and timestamp were tried in the key and removed.</b> A true MSIX mirror is
    /// one file, so those always match; but any other mirror — a test fixture, a copied
    /// profile, a backup — has its own timestamps, and including them made the de-duplication
    /// fail precisely where the paths already proved the answer. The existing
    /// two-roots-one-session test caught it.</para>
    ///
    /// <para>When one session does appear under two roots, the <b>newest</b> file wins, for the
    /// same reason freshness decides everywhere else in the app: the other root may be an old
    /// install's leftovers, and a stale copy must never shadow the live one.</para>
    ///
    /// <para>Totals were never wrong: ingestion de-duplicates on request id across every file
    /// (rule 4), which is why the union was taken deliberately in the first place. What was
    /// wrong is everything that <i>counts files</i> — the diagnostics bundle reported "Cowork:
    /// 8 file(s), 23,660,406 bytes" for four files totalling 11,830,203, and every poll read
    /// ~12 MB it already had.</para>
    /// </summary>
    public static IReadOnlyList<string> FindAuditLogs(IEnumerable<string> roots)
    {
        // Insertion-ordered, so the result stays stable for the diagnostics bundle and for
        // tests: the first root to offer a session fixes that session's position, even if a
        // later root supplies a newer file for it.
        var bySession = new Dictionary<string, string>(PathIdentity.Comparer);
        var order = new List<string>();

        foreach (var root in roots)
        {
            foreach (var file in FindAuditLogs(root))
            {
                var key = SessionKey(root, file);

                if (!bySession.TryGetValue(key, out var held))
                {
                    bySession[key] = file;
                    order.Add(key);
                }
                else if (LastWriteUtcOrMin(file) > LastWriteUtcOrMin(held))
                {
                    bySession[key] = file;
                }
            }
        }

        return [.. order.Select(k => bySession[k])];
    }

    /// <summary>
    /// What makes two paths the same session: where the file sits beneath its own root.
    /// Falls back to the full path when a relative one cannot be formed, which keeps the file
    /// in the list rather than silently collapsing it into another — dropping a real
    /// transcript is the failure this whole area exists to prevent (issue #44).
    /// </summary>
    private static string SessionKey(string root, string file)
    {
        try
        {
            // Separators normalised because the two roots can be written differently; case is
            // left to PathIdentity, which knows whether this platform folds it.
            return Path.GetRelativePath(root, file).Replace('\\', '/');
        }
        catch (ArgumentException)
        {
            return file;
        }
    }

    private static DateTime LastWriteUtcOrMin(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }
}
