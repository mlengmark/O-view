namespace OView.Core.Providers.Jsonl;

/// <summary>
/// Locates Cowork transcripts. Cowork runs each session in a sandboxed Claude home
/// under <c>%APPDATA%\Claude\local-agent-mode-sessions\&lt;org&gt;\&lt;user&gt;\&lt;session&gt;\</c>,
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

    /// <summary>Default Cowork session root: %APPDATA%\Claude\local-agent-mode-sessions</summary>
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Claude",
        "local-agent-mode-sessions");

    /// <summary>
    /// All Cowork audit logs under the root. Empty if the root does not exist — a user
    /// who has never opened Cowork is the normal case, not an error.
    /// </summary>
    public static IReadOnlyList<string> FindAuditLogs(string root) =>
        TranscriptFileScan.Find(root, AuditFileName);
}
