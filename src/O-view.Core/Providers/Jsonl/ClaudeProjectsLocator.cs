namespace OView.Core.Providers.Jsonl;

/// <summary>
/// Locates Claude Code transcript files. Usage limits are account-wide, so all
/// project directories are scanned, not just the current one.
/// </summary>
public static class ClaudeProjectsLocator
{
    /// <summary>Default transcript root: %USERPROFILE%\.claude\projects</summary>
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude",
        "projects");

    /// <summary>
    /// All transcript files under the root. Empty if the root does not exist.
    /// Delegates to <see cref="TranscriptFileScan"/> so one unreadable directory costs
    /// that directory rather than every transcript on the machine (issue #44).
    /// </summary>
    public static IReadOnlyList<string> FindTranscripts(string root) =>
        TranscriptFileScan.Find(root, "*.jsonl");

    /// <summary>
    /// Windows path mangling as Claude Code applies it to project directory names:
    /// separators and the drive colon become '-', e.g. C:\Users\X → C--Users-X.
    /// Written for Windows deliberately — do not adapt a POSIX implementation
    /// (docs/findings/jsonl-schema.md).
    /// </summary>
    public static string MangleCwd(string cwd)
    {
        var chars = cwd.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] is ':' or '\\' or '/')
            {
                chars[i] = '-';
            }
        }
        return new string(chars);
    }
}
