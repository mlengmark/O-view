namespace OView.Core.Providers.Jsonl;

/// <summary>
/// The names O-view gives the two local transcript surfaces (CLAUDE.md rule 9).
///
/// <para>They were literals in <see cref="TranscriptScopeReport"/> alone while nothing else
/// needed them. They are constants now because the same strings are <b>written into the rollup
/// store</b> beside every ingested request: the ledger could report how many rows it held and
/// nothing at all about where they came from, so a machine whose transcripts are 98.5% Cowork
/// and whose ledger holds only Claude Code rows looked exactly like a machine that had simply
/// used Claude a little (GitHub issue #218). A label that reaches the schema cannot be retyped
/// per call site — a second spelling would split one source into two rows of a breakdown whose
/// entire purpose is to be summed against the file counts above it.</para>
///
/// <para>They are display strings and storage keys at once, deliberately: the breakdown is
/// printed straight into the support bundle beneath the transcript counts that use the same
/// words, and a reader comparing the two sections must not have to translate between them.</para>
/// </summary>
public static class TranscriptSources
{
    /// <summary>Claude Code, CLI or hosted in Desktop — <c>~/.claude/projects/**/*.jsonl</c>.</summary>
    public const string ClaudeCode = "Claude Code";

    /// <summary>Cowork — <c>&lt;claude-data-root&gt;/local-agent-mode-sessions/…/audit.jsonl</c>.</summary>
    public const string Cowork = "Cowork";

    /// <summary>
    /// Rows written before the store recorded a source at all.
    ///
    /// <para><b>Not a third surface, and never counted as one.</b> Every install that predates
    /// this column carries a whole ledger of them, and the honest reading is "this build cannot
    /// say" — attributing them to Claude Code because that is the likelier source would be
    /// exactly the fabricated number rule 6 forbids, in the one report a person turns to when
    /// they already suspect the figures.</para>
    /// </summary>
    public const string Unattributed = "unattributed";

    /// <summary>The real surfaces, in the order the bundle prints them.</summary>
    public static IReadOnlyList<string> All => [ClaudeCode, Cowork];
}
