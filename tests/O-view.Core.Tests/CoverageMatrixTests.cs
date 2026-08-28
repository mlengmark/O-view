using OView.Core.Models;
using OView.Core.Providers.Jsonl;
using OView.Core.Providers.PlanHistory;
using OView.Core.Storage;

namespace OView.Core.Tests;

/// <summary>
/// What O-view covers, per Claude surface, asserted in one place (GitHub issue #171).
///
/// <para>The answer already existed — CLAUDE.md rule 9, two findings documents and three
/// locator classes — but nothing pinned it, and it has been got wrong in shipping code
/// three times. Cowork usage was read by nothing at all (#44). A Cowork user was told their
/// source was not counted while it was being counted (#58). A CLI-only user was told they
/// had no usage data while their tiles were full (#170). Every one of those was a surface
/// silently falling out of a picture nobody asserted.</para>
///
/// <para>So each row below builds that surface's real on-disk layout and asserts what
/// resolves from it — deleting a locator fails a test that names the surface it broke,
/// rather than producing an empty tile and no error.</para>
///
/// <list type="table">
/// <item><term>Claude Code CLI</term><description>tokens ✓, percentages ✗</description></item>
/// <item><term>Claude Code in Desktop</term><description>tokens ✓, percentages ✓ — same transcripts</description></item>
/// <item><term>Claude Desktop</term><description>tokens ✓, percentages ✓</description></item>
/// <item><term>Cowork</term><description>tokens ✓, percentages ✗</description></item>
/// <item><term>Chat</term><description>neither — no local record exists</description></item>
/// </list>
/// </summary>
public class CoverageMatrixTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("oview-matrix-").FullName;
    private readonly RollupStore _store;

    public CoverageMatrixTests() => _store = new RollupStore(Path.Combine(_dir, "usage.db"));

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string ProjectsRoot => Path.Combine(_dir, "profile", ".claude", "projects");
    private string CoworkRoot => Path.Combine(_dir, "claude-data", CoworkAuditLocator.SessionsDirectoryName);
    private string PlanFile => Path.Combine(_dir, "claude-data", PlanHistoryLocator.FileName);

    /// <summary>
    /// One assistant record, in the shape both surfaces write. The id key is the only
    /// difference between them and it is spelled by the caller, because that spelling is
    /// precisely what this file exists to pin (rule 4).
    /// </summary>
    private static string UsageLine(string idKey, string id) =>
        $"{{\"type\":\"assistant\",\"{idKey}\":\"{id}\",\"timestamp\":\"2026-08-23T12:00:00.000Z\"," +
        "\"message\":{\"model\":\"claude-opus-5\",\"usage\":{\"input_tokens\":2," +
        "\"cache_creation_input_tokens\":100,\"cache_read_input_tokens\":900,\"output_tokens\":50}}}";

    /// <summary>A Claude Code record: camelCase <c>requestId</c>.</summary>
    private static string ClaudeCodeLine(string id) => UsageLine("requestId", id);

    /// <summary>A Cowork record: snake_case <c>request_id</c>, otherwise identical.</summary>
    private static string CoworkLine(string id) => UsageLine("request_id", id);

    /// <summary>Claude Code, whether run from the CLI or hosted inside Desktop — same path.</summary>
    private void GiveClaudeCodeSession(string id)
    {
        var dir = Path.Combine(ProjectsRoot, "C--Users-someone-repo");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{id}.jsonl"), ClaudeCodeLine(id) + "\n");
    }

    /// <summary>
    /// A Cowork sandbox, including the <c>.claude\projects</c> directory it really contains
    /// and which is <b>always empty</b> — the detail that made #44 invisible, because a
    /// projects-only scan finds the folder and concludes it succeeded.
    /// </summary>
    private void GiveCoworkSession(string id)
    {
        var session = Path.Combine(CoworkRoot, "org", "user", id);
        Directory.CreateDirectory(Path.Combine(session, ".claude", "projects"));
        File.WriteAllText(Path.Combine(session, CoworkAuditLocator.AuditFileName), CoworkLine(id) + "\n");
    }

    /// <summary>The file only Claude Desktop writes — the sole source of the two percentages.</summary>
    private void GiveDesktopPlanHistory()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PlanFile)!);
        var at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        File.WriteAllText(PlanFile,
            "{\"samples\":[{\"t\":" + at + ",\"org\":\"org-1\",\"u\":{\"fh\":25,\"sd\":3}}]}");
    }

    private TranscriptScopeReport Scope() => TranscriptScopeReport.Inspect(ProjectsRoot, [CoworkRoot]);

    /// <summary>Ingests through the real provider and returns what actually landed in the store.</summary>
    private long IngestedTokens()
    {
        new JsonlUsageProvider(_store, ProjectsRoot, [CoworkRoot]).GetSnapshot(DateTimeOffset.UtcNow);
        return _store.GetDailyRollups(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, TimeZoneInfo.Utc)
            .Sum(r => r.TotalTokens);
    }

    private bool PercentagesAvailable() =>
        PlanHistoryDiagnostics.Inspect(PlanFile).Status == PlanDataStatus.Ok;

    // ── the matrix, one row at a time ───────────────────────────────────────────────

    /// <summary>
    /// Claude Code CLI: transcripts are read and counted, and there are no percentages —
    /// that file belongs to Desktop. Both halves matter; the second is issue #170.
    /// </summary>
    [Fact]
    public void ClaudeCodeCli_CountsTokens_ButHasNoPercentages()
    {
        GiveClaudeCodeSession("req_cli");

        Assert.Equal(1, Scope().Sources.Single(s => s.Label == "Claude Code").FileCount);
        Assert.Equal(1052, IngestedTokens());
        Assert.False(PercentagesAvailable());
    }

    /// <summary>
    /// Claude Code hosted inside Desktop writes to the ordinary user-profile location, so
    /// it is the CLI row plus percentages. "Desktop" is not the dividing line, and copy
    /// that says otherwise is wrong (rule 9).
    /// </summary>
    [Fact]
    public void ClaudeCodeHostedInDesktop_UsesTheSameTranscriptsAndAddsPercentages()
    {
        GiveClaudeCodeSession("req_hosted");
        GiveDesktopPlanHistory();

        Assert.Equal(1, Scope().Sources.Single(s => s.Label == "Claude Code").FileCount);
        Assert.Equal(1052, IngestedTokens());
        Assert.True(PercentagesAvailable());
    }

    /// <summary>
    /// Cowork: counted, from <c>audit.jsonl</c> under its sandbox root, and with the
    /// snake_case id spelling. Reading only the camelCase one ingests nothing from here —
    /// no error, just an empty tile (rule 4).
    /// </summary>
    [Fact]
    public void Cowork_CountsTokens_FromAuditLogs_ButHasNoPercentages()
    {
        GiveCoworkSession("req_cowork");

        Assert.Equal(1, Scope().Sources.Single(s => s.Label == "Cowork").FileCount);
        Assert.Equal(1052, IngestedTokens());
        Assert.False(PercentagesAvailable());
    }

    /// <summary>
    /// Chat: nothing, and that is correct rather than broken — claude.ai keeps no local
    /// usage record at all. Pinned so nobody later "fixes" a gap that cannot be closed.
    /// </summary>
    [Fact]
    public void Chat_IsCountedNowhere_AndThatIsNotAFailure()
    {
        // A profile where the user has only ever used chat: both roots resolve, neither
        // holds anything, and no plan file exists.
        Assert.Equal(TranscriptScopeStatus.NoTranscripts, Scope().Status);
        Assert.Equal(0, IngestedTokens());
        Assert.False(PercentagesAvailable());
    }

    // ── the traps each row is exposed to ────────────────────────────────────────────

    /// <summary>
    /// A Cowork-only machine must not read as "Claude Code present". The sandbox contains
    /// an empty <c>.claude\projects</c>, which is exactly what made a projects-only scan
    /// look successful while every Cowork token went uncounted (#44).
    /// </summary>
    [Fact]
    public void ACoworkSandboxDoesNotRegisterAsAClaudeCodeSource()
    {
        GiveCoworkSession("req_only_cowork");

        var scope = Scope();

        Assert.Equal(0, scope.Sources.Single(s => s.Label == "Claude Code").FileCount);
        Assert.Equal(["Cowork"], scope.PresentSources);
    }

    /// <summary>Both surfaces at once: each is found, and the totals add rather than replace.</summary>
    [Fact]
    public void BothTranscriptSourcesAreCountedTogether()
    {
        GiveClaudeCodeSession("req_a");
        GiveCoworkSession("req_b");

        var scope = Scope();

        Assert.Equal(2, scope.TotalFiles);
        Assert.Equal(["Claude Code", "Cowork"], scope.PresentSources);
        Assert.Equal(2104, IngestedTokens());
    }

    /// <summary>
    /// The same request seen through both surfaces is counted once. MSIX redirection can
    /// expose one set of files through two roots, so the union is taken deliberately and
    /// de-duplication on request id is what makes that safe (rule 4, rule 7).
    /// </summary>
    [Fact]
    public void ARequestSeenThroughBothSourcesIsCountedOnce()
    {
        GiveClaudeCodeSession("req_shared");
        GiveCoworkSession("req_shared");

        Assert.Equal(2, Scope().TotalFiles);
        Assert.Equal(1052, IngestedTokens());
    }

    /// <summary>Re-ingesting changes nothing — the store is re-fed on every poll (rule 7).</summary>
    [Fact]
    public void IngestingTwiceDoesNotDoubleTheTotals()
    {
        GiveClaudeCodeSession("req_twice");

        Assert.Equal(1052, IngestedTokens());
        Assert.Equal(1052, IngestedTokens());
    }

    // ── what the panel says about all this ──────────────────────────────────────────

    /// <summary>
    /// The coverage line names the surfaces actually feeding the figures, so a user can
    /// tell a low number caused by using Claude less from one caused by O-view not looking
    /// where they work.
    /// </summary>
    [Fact]
    public void TheCoverageLineNamesBothSurfacesWhenBothArePresent()
    {
        GiveClaudeCodeSession("req_a");
        GiveCoworkSession("req_b");

        var line = Scope().CoverageLine();

        Assert.Equal("Counting local transcripts: Claude Code · Cowork.", line);
    }

    /// <summary>
    /// An absent surface is named rather than omitted. "Counting: Claude Code" alone cannot
    /// be told apart from a build that forgot Cowork existed — which is how #44 hid.
    /// </summary>
    [Fact]
    public void TheCoverageLineNamesTheSurfaceThatFoundNothing()
    {
        GiveClaudeCodeSession("req_a");

        var line = Scope().CoverageLine();

        Assert.Contains("Counting local transcripts: Claude Code", line, StringComparison.Ordinal);
        Assert.Contains("no Cowork sessions found", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>"local transcripts", not just "Counting"</b> (issue #245). Naming Cowork unqualified
    /// said all Cowork usage was counted, and it is not: a cloud-container session writes no
    /// transcript on this machine at all — measured 2026-08-28, no registration and no transcript.
    ///
    /// <para>The line's job is still #171's: report which surfaces left files <i>here</i>, so a
    /// small figure can be told apart from O-view not looking where the user works. The missing
    /// qualifier was what turned that into an overstatement.</para>
    /// </summary>
    [Fact]
    public void TheCoverageLineSaysItIsCountingLocalTranscripts()
    {
        GiveCoworkSession("req_a");

        Assert.StartsWith("Counting local transcripts:", Scope().CoverageLine(), StringComparison.Ordinal);
    }

    /// <summary>
    /// This line no longer names chat, and that is the change rather than a regression
    /// (issue #245).
    ///
    /// <para>It used to end "Chat keeps no local record", justified by a heavy chat user with a
    /// small token figure having <i>no other way</i> to learn why. That premise stopped holding in
    /// issue #235: <see cref="PanelText.TokenScopeCaveat"/> states it beneath the tiles, always
    /// visible, where this line sits inside a disclosure most readers never open. The permanent
    /// gap is now stated once, in the more visible of the two.</para>
    ///
    /// <para>Two notes saying overlapping things is how the panel's copy drifted apart before —
    /// the same reasoning <see cref="TheCoverageLineIsEmptyWhenNothingWasFound"/> already
    /// applies to <c>Explain()</c>.</para>
    /// </summary>
    [Fact]
    public void TheCoverageLineLeavesThePermanentGapToTheAlwaysVisibleNote()
    {
        GiveClaudeCodeSession("req_a");
        GiveCoworkSession("req_b");

        var line = Scope().CoverageLine();

        Assert.DoesNotContain("chat", line, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("chat", PanelText.TokenScopeCaveat, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// With nothing found at all there is no coverage to state, and Explain() owns that
    /// state with a far longer message. Two notes saying overlapping things is how the
    /// panel's copy drifted apart before.
    /// </summary>
    [Fact]
    public void TheCoverageLineIsEmptyWhenNothingWasFound()
    {
        Assert.Equal("", Scope().CoverageLine());
        Assert.NotEqual("", Scope().Explain());
    }

    /// <summary>Paths belong in Copy diagnostics, which resolves them — never in a sentence (#58).</summary>
    [Fact]
    public void TheCoverageLineHardCodesNoPath()
    {
        GiveClaudeCodeSession("req_a");

        var line = Scope().CoverageLine();

        Assert.DoesNotContain(".claude", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("APPDATA", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_dir, line, StringComparison.OrdinalIgnoreCase);
    }
}
