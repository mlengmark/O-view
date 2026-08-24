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
    /// All Cowork audit logs across several roots.
    ///
    /// Roots may legitimately expose the *same* sessions: MSIX write-redirection means a
    /// machine can see one set of files through both the canonical and packaged paths
    /// (observed on the dev machine — identical session ids and identical totals through
    /// both). Reading them twice is safe rather than double-counting, because ingestion
    /// de-duplicates on request id across every file (CLAUDE.md rule 4), so the union is
    /// taken deliberately instead of guessing which root is authoritative.
    /// </summary>
    public static IReadOnlyList<string> FindAuditLogs(IEnumerable<string> roots) =>
        roots.SelectMany(FindAuditLogs).ToList();
}
