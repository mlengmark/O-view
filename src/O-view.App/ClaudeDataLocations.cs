namespace OView.App;

/// <summary>
/// Every place the engine reads Claude's files from, stated explicitly. Used to run the real
/// engine against a fixture (the smoke test, <c>tests/fixtures/claude-home</c>) instead of
/// this machine.
///
/// <para>All-or-nothing on purpose. A run that named some locations and defaulted the rest
/// would quietly read the developer's own Cowork sessions or cached usage beside the fixture,
/// and pass or fail depending on whose machine it ran on — the failure mode issue #212 removed
/// for injected providers.</para>
/// </summary>
/// <param name="AccountFile">Claude Code's <c>.claude.json</c>: the account and its organization.</param>
/// <param name="CachedUsageFile">The file carrying Claude Code's cached usage figures (usually the same <c>.claude.json</c>).</param>
/// <param name="PlanHistoryFile">Claude Desktop's <c>plan-usage-history.json</c>.</param>
/// <param name="ProjectsRoot">Claude Code's <c>projects</c> folder of transcripts.</param>
/// <param name="CoworkRoots">Cowork's transcript roots; empty for none.</param>
/// <param name="SessionRegistryRoots">Cowork's session registry roots; empty for none.</param>
public sealed record ClaudeDataLocations(
    string AccountFile,
    string CachedUsageFile,
    string PlanHistoryFile,
    string ProjectsRoot,
    IReadOnlyList<string> CoworkRoots,
    IReadOnlyList<string> SessionRegistryRoots)
{
    /// <summary>The standard layout under one home folder, as Claude Code and Desktop write it on Windows.</summary>
    public static ClaudeDataLocations UnderHome(string home)
    {
        var claudeDir = Path.Combine(home, ".claude");
        var accountFile = Path.Combine(claudeDir, ".claude.json");
        return new(
            AccountFile: accountFile,
            CachedUsageFile: accountFile,
            PlanHistoryFile: Path.Combine(home, "AppData", "Roaming", "Claude", "plan-usage-history.json"),
            ProjectsRoot: Path.Combine(claudeDir, "projects"),
            CoworkRoots: [],
            SessionRegistryRoots: []);
    }
}
