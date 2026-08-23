namespace OView.Core.Providers;

/// <summary>
/// Where Claude Code keeps its per-user configuration — the directory O-view's transcript
/// scan starts from.
///
/// <para>Anthropic documents exactly two cases, so this is a rule rather than a search:</para>
///
/// <blockquote>"On Windows, <c>~/.claude</c> resolves to <c>%USERPROFILE%\.claude</c>. If you
/// set <c>CLAUDE_CONFIG_DIR</c>, every <c>~/.claude</c> path on this page lives under that
/// directory instead."</blockquote>
///
/// <para>O-view honoured only the first, so anyone who had relocated their configuration was
/// invisible — their transcripts were never found and their token tiles read zero, with no
/// indication why. That is the same silent-empty-tile failure as issues #44 and #58, arriving
/// through a third door.</para>
///
/// <para><b>Two cases, not a hunt.</b> There is no probing of likely directories and no
/// user-facing folder picker: the contract is documented, so following it is complete. A
/// picker would add a setting that can go stale and be pointed at the wrong place, to solve
/// a problem the environment variable already solves correctly.</para>
///
/// <para>Read on every call rather than cached. The variable is process-scoped and O-view is
/// designed to run for days, but a cached value would also survive a change made while it
/// runs — and re-reading an environment variable costs nothing worth optimising.</para>
/// </summary>
public static class ClaudeConfigDir
{
    /// <summary>The documented override. Set, it replaces the whole <c>~/.claude</c> path.</summary>
    public const string OverrideVariable = "CLAUDE_CONFIG_DIR";

    /// <summary>Directory name under the user profile when no override is set.</summary>
    public const string DefaultFolderName = ".claude";

    /// <summary>
    /// The configuration directory in effect: the override when it is set to something
    /// usable, otherwise <c>~/.claude</c>.
    /// </summary>
    public static string Path => Resolve(Environment.GetEnvironmentVariable(OverrideVariable));

    /// <summary>
    /// Testable core. Whitespace and empty are treated as unset — an exported-but-blank
    /// variable is a shell accident, not an instruction to read the filesystem root, and
    /// honouring it literally would point the scan at somewhere with no transcripts and no
    /// explanation.
    /// </summary>
    public static string Resolve(string? overrideValue) =>
        string.IsNullOrWhiteSpace(overrideValue)
            ? System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), DefaultFolderName)
            : overrideValue.Trim();
}
