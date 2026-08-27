namespace OView.Core.Providers.Jsonl;

/// <summary>
/// Locates Cowork transcripts. Cowork runs each session in a sandboxed Claude home under
/// <c>&lt;claude-data-root&gt;\local-agent-mode-sessions\&lt;org&gt;\&lt;user&gt;\&lt;session&gt;\</c>, and
/// writes usage records in two places inside it:
///
/// <list type="bullet">
///   <item><c>audit.jsonl</c> — older builds, at the session root.</item>
///   <item><c>.claude\projects\&lt;encoded-cwd&gt;\&lt;id&gt;.jsonl</c> — the sandbox's <b>own</b>
///     Claude Code home, written when the session runs Claude Code inside the sandbox.</item>
/// </list>
///
/// <para><b>The second one was documented as impossible and is not.</b> This class, and
/// CLAUDE.md rule 9 with it, asserted that the sandbox's <c>.claude\projects</c> was "always
/// empty" and that the transcript went to <c>audit.jsonl</c> instead — so the scan looked for
/// that one name. Measured on two machines: 4 such files here, and <b>38 on the machine that
/// reported issue #218</b>, none of them ever read. Their usage was dropped exactly as Cowork's
/// was before issue #44, one directory deeper and for the same reason — a scan that looks for
/// what it expects rather than for what is there.</para>
///
/// <para>It stayed invisible because a plain recursive enumeration of that tree returns nothing:
/// it contains the broken junction <see cref="TranscriptFileScan"/> exists to survive, so a
/// hand-rolled check agreed with the documentation and only the loop-safe walk disagreed.</para>
///
/// <para><b>Widening the pattern cannot double-count.</b> Ingestion de-duplicates on request id
/// across every file (rule 4) and tracks a watermark per path, so a request recorded in both a
/// sandbox transcript and an <c>audit.jsonl</c> is stored once. A file here that turns out to
/// hold no usage records costs one read and nothing else — <see cref="TranscriptReader"/> keeps
/// only assistant records carrying a request id and a usage object.</para>
///
/// <para>Read-only, like every other provider input.</para>
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
    /// Every transcript Cowork has written under one root, in either shape. Empty if the root
    /// does not exist — a user who has never opened Cowork is the normal case, not an error.
    /// </summary>
    public static IReadOnlyList<string> FindTranscripts(string root) =>
        TranscriptFileScan.Find(root, "*.jsonl");

    /// <summary>
    /// Every Cowork transcript across several roots, with mirrors collapsed.
    ///
    /// <para>MSIX write-redirection means a machine can see one set of files through both the
    /// canonical and the packaged path — same sessions, same bytes, two absolute paths, and
    /// neither is a link, so nothing about the paths says they are one tree.</para>
    ///
    /// <para><b>De-duplicated on the path <i>relative to its root</i>, which is what makes two
    /// mirrored files comparable.</b> The old rule kept both and leaned on ingestion's request-id
    /// de-duplication to stop the tokens being counted twice. That much was true, but the
    /// <i>file counts and byte totals were not</i>: the bundle reported "Cowork: 16 file(s),
    /// 46,994,714 bytes" for 8 files and 23 MB, and the figure halved and doubled through the
    /// day as the canonical root came and went. A count nobody can trust is a rule 6 failure
    /// even when the tokens behind it are right — and it costs a doubled read on every poll.</para>
    ///
    /// <para>Relative path, not file name: <c>audit.jsonl</c> repeats in every session directory,
    /// so collapsing on the name alone would discard real transcripts.</para>
    /// </summary>
    public static IReadOnlyList<string> FindTranscripts(IEnumerable<string> roots) =>
        roots
            .SelectMany(root => FindTranscripts(root)
                .Select(path => (Relative: Path.GetRelativePath(root, path), Path: path)))
            .GroupBy(found => found.Relative, PathIdentity.Comparer)
            .Select(group => group.First().Path)
            .ToList();
}
