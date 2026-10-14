namespace OView.Core.Providers.Jsonl;

/// <summary>
/// Locates Claude Code transcript files. Usage limits are account-wide, so all
/// project directories are scanned, not just the current one.
/// </summary>
public static class ClaudeProjectsLocator
{
    /// <summary>
    /// Default transcript root: <c>projects</c> under Claude Code's configuration directory.
    ///
    /// <para>Which is <c>%USERPROFILE%\.claude</c> unless <c>CLAUDE_CONFIG_DIR</c> says
    /// otherwise — see <see cref="ClaudeConfigDir"/>. This used to hard-code the profile
    /// path, so a user who had relocated their configuration had no transcripts found and a
    /// token tile reading zero, with nothing to explain it.</para>
    /// </summary>
    public static string DefaultRoot => Path.Combine(ClaudeConfigDir.Path, "projects");

    /// <summary>
    /// All transcript files under the root. Empty if the root does not exist.
    /// Delegates to <see cref="TranscriptFileScan"/> so one unreadable directory costs
    /// that directory rather than every transcript on the machine (issue #44).
    /// </summary>
    public static IReadOnlyList<string> FindTranscripts(string root) =>
        TranscriptFileScan.Find(root, "*.jsonl");

    // A MangleCwd helper used to live here, reproducing how Claude Code turns a working
    // directory into a project folder name (C:\Users\X → C--Users-X). It has since moved to
    // ClaudeCliRefresher, whose OwnProjectFolder uses it for real: finding the mangled name
    // of its own working directory, so TranscriptScopeReport can leave that one folder's
    // transcripts out of the count it shows the user. Transcripts are still only ever found
    // by walking TranscriptFileScan, never by reconstructing a directory name — the
    // convention is recorded in docs/findings/jsonl-schema.md.
}
