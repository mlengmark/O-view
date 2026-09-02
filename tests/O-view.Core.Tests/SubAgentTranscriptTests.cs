using OView.Core.Providers.Jsonl;
using OView.Core.Storage;

namespace OView.Core.Tests;

/// <summary>
/// Sub-agent transcripts are counted (GitHub issue #271).
///
/// <para><b>Why this file exists.</b> A sub-agent's turns are not written into the session
/// transcript beside the rest of the conversation. Claude Code gives each one its own file, one
/// directory below the session file:</para>
///
/// <code>
/// ~/.claude/projects/&lt;encoded-cwd&gt;/&lt;parent-session-id&gt;/subagents/agent-&lt;agent-id&gt;.jsonl
/// </code>
///
/// <para>Those records draw on the same plan window as the session that spawned them, so leaving
/// them out would put the tiles in direct conflict with Claude's own meter. They are ingested
/// today — measured on the development machine, 2026-08-31 took in <b>230 sub-agent requests
/// worth 62.0M tokens</b> — but only because <see cref="TranscriptFileScan"/> walks recursively.
/// Nothing asserted it, and nothing wrote the layout down.</para>
///
/// <para><b>That is the whole hazard.</b> The extra directory level is invisible to anyone
/// reading the scan: a future narrowing for performance — a top-level enumeration, a tighter
/// pattern, a depth ceiling — would silently drop every sub-agent token with no failing test to
/// argue back. It is the shape of issue #44 (a scan that looked for what it expected rather than
/// for what was there) and issue #224 (real usage one directory below where the scan looked), and
/// this file is here so it cannot be the shape of a third.</para>
/// </summary>
public class SubAgentTranscriptTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("oview-subagent-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        GC.SuppressFinalize(this);
    }

    private const string ParentSessionId = "0af0c879-4290-4f9e-b8a8-ca4562ec1150";

    private string DbPath => Path.Combine(_dir, "usage.db");

    /// <summary>
    /// An ordinary session record — what the parent conversation writes.
    /// </summary>
    private static string SessionRecord(string requestId, long outputTokens) =>
        "{\"type\":\"assistant\",\"requestId\":\"" + requestId + "\","
        + "\"timestamp\":\"2026-08-31T10:00:00Z\",\"isSidechain\":false,"
        + "\"message\":{\"model\":\"claude-opus-5\",\"usage\":"
        + "{\"input_tokens\":2,\"cache_creation_input_tokens\":100,"
        + "\"cache_read_input_tokens\":200,\"output_tokens\":" + outputTokens + "}},"
        + "\"sessionId\":\"" + ParentSessionId + "\"}";

    /// <summary>
    /// A sub-agent record, carrying the four fields that distinguish one.
    ///
    /// <para>Written out in full rather than reduced to the fields the parser reads, because the
    /// point of the fixture is to be the shape that is actually on disk. <c>sessionId</c> is the
    /// <b>parent's</b> id, not a new one — the sub-agent does not get a session of its own, which
    /// is why the register in <see cref="CoworkSessionIndex"/> has nothing to match on and why
    /// the directory name is the only thing tying the file back to its session.</para>
    /// </summary>
    private static string SubAgentRecord(string requestId, long outputTokens) =>
        "{\"type\":\"assistant\",\"requestId\":\"" + requestId + "\","
        + "\"timestamp\":\"2026-08-31T10:05:00Z\",\"isSidechain\":true,"
        + "\"agentId\":\"a93da32b6fda5604a\","
        + "\"attributionAgent\":\"general-purpose\",\"attributionSkill\":\"security-review\","
        + "\"message\":{\"model\":\"claude-opus-5\",\"usage\":"
        + "{\"input_tokens\":2,\"cache_creation_input_tokens\":100,"
        + "\"cache_read_input_tokens\":200,\"output_tokens\":" + outputTokens + "}},"
        + "\"sessionId\":\"" + ParentSessionId + "\"}";

    /// <summary>
    /// The projects root as the real machine lays it out: the session file and the directory
    /// holding its sub-agents are <b>siblings</b>, both under the encoded working directory.
    /// </summary>
    private string BuildProjectsRoot(string? sessionLine, string? subAgentLine)
    {
        var projects = Directory.CreateDirectory(Path.Combine(_dir, "projects")).FullName;
        var encodedCwd = Directory.CreateDirectory(
            Path.Combine(projects, "C--Users-Someone-Projects-Thing")).FullName;

        if (sessionLine is not null)
        {
            File.WriteAllText(Path.Combine(encodedCwd, ParentSessionId + ".jsonl"), sessionLine + "\n");
        }

        if (subAgentLine is not null)
        {
            var subagents = Directory.CreateDirectory(
                Path.Combine(encodedCwd, ParentSessionId, "subagents")).FullName;
            File.WriteAllText(
                Path.Combine(subagents, "agent-a93da32b6fda5604a.jsonl"), subAgentLine + "\n");
        }

        return projects;
    }

    /// <summary>Everything the store holds, in output tokens. UTC named explicitly (issue #211).</summary>
    private long TotalOutputTokens()
    {
        using var store = new RollupStore(DbPath);
        return store.GetDailyRollups(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, TimeZoneInfo.Utc)
            .Sum(r => r.Tokens.Output);
    }

    private void Ingest(string projectsRoot)
    {
        using var store = new RollupStore(DbPath);
        new JsonlUsageProvider(store, projectsRoot, [], []).GetSnapshot(DateTimeOffset.UnixEpoch);
    }

    /// <summary>
    /// The one that matters: a sub-agent's tokens reach the store through the ordinary provider
    /// run, with no session transcript beside it to carry them.
    /// </summary>
    [Fact]
    public void SubAgentTokensReachTheStore()
    {
        var projects = BuildProjectsRoot(sessionLine: null, subAgentLine: SubAgentRecord("req_sub", 700));

        Ingest(projects);

        Assert.Equal(700, TotalOutputTokens());
    }

    /// <summary>
    /// Both files are read on one pass. A scan that found the session transcript and stopped at
    /// the top level would still pass <see cref="SubAgentTokensReachTheStore"/>'s sibling in
    /// spirit but report only the parent here — which is the field symptom, since a session with
    /// sub-agents always has both.
    /// </summary>
    [Fact]
    public void ParentSessionAndSubAgentAreBothCounted()
    {
        var projects = BuildProjectsRoot(SessionRecord("req_parent", 120), SubAgentRecord("req_sub", 700));

        Ingest(projects);

        Assert.Equal(820, TotalOutputTokens());
    }

    /// <summary>
    /// The locator itself, asserted directly rather than through ingestion.
    ///
    /// <para>This is the assertion that names the actual requirement — <b>the walk must descend
    /// past the top level of the projects root</b> — so a change that breaks it fails here, at
    /// the line responsible, rather than only in the totals two layers up.</para>
    /// </summary>
    [Fact]
    public void TheProjectsLocatorDescendsIntoASessionsSubAgentDirectory()
    {
        var projects = BuildProjectsRoot(SessionRecord("req_parent", 120), SubAgentRecord("req_sub", 700));

        var found = ClaudeProjectsLocator.FindTranscripts(projects);

        Assert.Contains(found, p => Path.GetFileName(p) == "agent-a93da32b6fda5604a.jsonl");
        Assert.Contains(found, p => Path.GetFileName(p) == ParentSessionId + ".jsonl");
    }

    /// <summary>
    /// De-duplication spans the two files (rule 4). A sub-agent transcript and its parent are
    /// separate paths with separate watermarks, so nothing but the request id stops a request
    /// recorded in both from being counted twice.
    ///
    /// <para><b>The sub-agent file carries a second, unshared request on purpose.</b> Without it
    /// the expected total is reachable by reading the parent alone, so the test would pass just
    /// as happily against a scan that never descended — asserting the absence of double-counting
    /// while silently tolerating the absence of the data. The 50 is what makes the number
    /// distinguish all three outcomes: 750 correct, 700 sub-agent dropped, 1450 counted twice.</para>
    /// </summary>
    [Fact]
    public void ARequestRecordedInBothFilesIsCountedOnce()
    {
        var projects = BuildProjectsRoot(
            SessionRecord("req_shared", 700),
            SubAgentRecord("req_shared", 700) + "\n" + SubAgentRecord("req_sub_only", 50));

        Ingest(projects);

        Assert.Equal(750, TotalOutputTokens());
    }

    /// <summary>
    /// Ingesting twice changes nothing (rule 7), through the sub-agent path specifically. The
    /// per-file watermark is keyed by path and the extra directory level is part of that path.
    /// </summary>
    [Fact]
    public void ASecondPollDoesNotDoubleCountASubAgent()
    {
        var projects = BuildProjectsRoot(SessionRecord("req_parent", 120), SubAgentRecord("req_sub", 700));

        Ingest(projects);
        Ingest(projects);

        Assert.Equal(820, TotalOutputTokens());
    }
}
