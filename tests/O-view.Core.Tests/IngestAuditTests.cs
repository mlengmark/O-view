using OView.Core.Providers.Jsonl;
using OView.Core.Storage;

namespace OView.Core.Tests;

/// <summary>
/// The independent measurement (GitHub issue #218).
///
/// <para>Every other field the store contributes to a bundle is written by the same code path
/// whose correctness is in question, and on an install that predates the source column they are
/// silent about the only thing being asked. This pass re-reads the transcripts and compares them
/// against what is actually stored, so a missing surface becomes a number rather than an
/// inference drawn across two sections.</para>
/// </summary>
public class IngestAuditTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("oview-ingestaudit-").FullName;

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

    private string DbPath => Path.Combine(_dir, "usage.db");

    private static string ClaudeCodeRecord(string requestId) =>
        "{\"type\":\"assistant\",\"requestId\":\"" + requestId + "\","
        + "\"timestamp\":\"2026-08-20T10:00:00Z\","
        + "\"message\":{\"model\":\"claude-opus-5\",\"usage\":"
        + "{\"input_tokens\":10,\"output_tokens\":120}}}";

    private static string CoworkRecord(string requestId) =>
        "{\"type\":\"assistant\",\"request_id\":\"" + requestId + "\","
        + "\"timestamp\":\"2026-08-21T10:00:00Z\","
        + "\"message\":{\"model\":\"claude-opus-5\",\"usage\":"
        + "{\"input_tokens\":20,\"output_tokens\":300}}}";

    private (string Projects, string Cowork) BuildLayout(string? claudeCode, string? cowork)
    {
        var projects = Directory.CreateDirectory(Path.Combine(_dir, "projects")).FullName;
        var sessions = Directory.CreateDirectory(
            Path.Combine(_dir, "data", CoworkAuditLocator.SessionsDirectoryName)).FullName;
        var session = Directory.CreateDirectory(Path.Combine(sessions, "org", "user", "local_1")).FullName;

        if (claudeCode is not null)
        {
            File.WriteAllText(Path.Combine(projects, "session.jsonl"), claudeCode);
        }

        if (cowork is not null)
        {
            File.WriteAllText(Path.Combine(session, CoworkAuditLocator.AuditFileName), cowork);
        }

        return (projects, sessions);
    }

    /// <summary>
    /// <b>The measurement issue #218 needs.</b> A surface whose transcripts are on disk and
    /// whose requests are not in the ledger is reported as missing, with the tokens that go with
    /// them — and it works on a store carrying no attribution at all, which is every install
    /// that predates the source column.
    /// </summary>
    [Fact]
    public void RequestsOnDiskButAbsentFromTheLedgerAreReportedAsMissing()
    {
        var (projects, cowork) = BuildLayout(
            ClaudeCodeRecord("cc-1") + "\n",
            CoworkRecord("cw-1") + "\n" + CoworkRecord("cw-2") + "\n");

        // Only Claude Code is ingested. Cowork's transcripts sit on disk, unread — the exact
        // state the bundle could not previously distinguish from Cowork being counted.
        using (var store = new RollupStore(DbPath))
        {
            new JsonlUsageProvider(store, projects, []).GetSnapshot(DateTimeOffset.UnixEpoch);
        }

        var audit = IngestAuditReport.Run(DbPath, projects, [cowork]);
        var byName = audit.Sources.ToDictionary(s => s.Source, StringComparer.Ordinal);

        Assert.Equal(1, byName[TranscriptSources.ClaudeCode].Present);
        Assert.Equal(0, byName[TranscriptSources.ClaudeCode].Missing);

        Assert.Equal(2, byName[TranscriptSources.Cowork].Requests);
        Assert.Equal(0, byName[TranscriptSources.Cowork].Present);
        Assert.Equal(2, byName[TranscriptSources.Cowork].Missing);

        // Tokens as well as counts: "2 requests missing" is a different report on a machine
        // where those two requests are the whole month's usage.
        Assert.Equal((20 + 300) * 2, byName[TranscriptSources.Cowork].Tokens);
        Assert.Equal(0, byName[TranscriptSources.Cowork].TokensStored);

        Assert.Contains("MISSING", audit.ToClipboardText(), StringComparison.Ordinal);
    }

    /// <summary>Everything ingested reconciles exactly — the case that must not cry wolf.</summary>
    [Fact]
    public void AFullyIngestedMachineReportsNothingMissing()
    {
        var (projects, cowork) = BuildLayout(
            ClaudeCodeRecord("cc-1") + "\n",
            CoworkRecord("cw-1") + "\n");

        using (var store = new RollupStore(DbPath))
        {
            new JsonlUsageProvider(store, projects, [cowork]).GetSnapshot(DateTimeOffset.UnixEpoch);
        }

        var audit = IngestAuditReport.Run(DbPath, projects, [cowork]);

        Assert.All(audit.Sources, source => Assert.Equal(0, source.Missing));
        Assert.Equal(2, audit.LedgerRows);
    }

    /// <summary>
    /// Streaming writes the same request several times and only the last is complete (rule 4).
    /// The audit de-duplicates the way the store's upsert does, or it would report a token total
    /// no build has ever stored and then blame the store for the difference.
    /// </summary>
    [Fact]
    public void RepeatedRecordsForOneRequestCountOnce()
    {
        var (projects, cowork) = BuildLayout(
            ClaudeCodeRecord("cc-1") + "\n" + ClaudeCodeRecord("cc-1") + "\n" + ClaudeCodeRecord("cc-1") + "\n",
            cowork: null);

        using (var store = new RollupStore(DbPath))
        {
            new JsonlUsageProvider(store, projects, [cowork]).GetSnapshot(DateTimeOffset.UnixEpoch);
        }

        var claudeCode = IngestAuditReport.Run(DbPath, projects, [cowork]).Sources
            .Single(s => s.Source == TranscriptSources.ClaudeCode);

        Assert.Equal(3, claudeCode.Records);
        Assert.Equal(1, claudeCode.Requests);
        Assert.Equal(10 + 120, claudeCode.Tokens);
    }

    /// <summary>
    /// A request id present in both surfaces' files is stored once, under whichever reached it
    /// first. The split is approximate by exactly that much, and the report says so rather than
    /// leaving a reader to discover it from two totals that do not add up.
    /// </summary>
    [Fact]
    public void ARequestFoundUnderBothSurfacesIsCountedAsShared()
    {
        var (projects, cowork) = BuildLayout(
            ClaudeCodeRecord("shared-1") + "\n",
            CoworkRecord("shared-1") + "\n");

        using (var store = new RollupStore(DbPath))
        {
            new JsonlUsageProvider(store, projects, [cowork]).GetSnapshot(DateTimeOffset.UnixEpoch);
        }

        var audit = IngestAuditReport.Run(DbPath, projects, [cowork]);

        Assert.Equal(1, audit.SharedRequests);
        Assert.True(audit.HasOverlap);
        Assert.Contains("more than one source", audit.ToClipboardText(), StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Producing a bundle must never change what the bundle describes.</b> The store is
    /// opened read-only, so diagnosing a suspected journal rollback (#213) cannot perform one,
    /// and a machine with no database yet does not acquire one by being diagnosed.
    /// </summary>
    [Fact]
    public void TheAuditNeverWritesToTheStore()
    {
        var (projects, cowork) = BuildLayout(ClaudeCodeRecord("cc-1") + "\n", CoworkRecord("cw-1") + "\n");

        var missing = IngestAuditReport.Run(DbPath, projects, [cowork]);

        Assert.NotNull(missing.Failure);
        Assert.False(File.Exists(DbPath));

        using (var store = new RollupStore(DbPath))
        {
            new JsonlUsageProvider(store, projects, [cowork]).GetSnapshot(DateTimeOffset.UnixEpoch);
        }

        var before = new FileInfo(DbPath).Length;
        IngestAuditReport.Run(DbPath, projects, [cowork]);

        Assert.Equal(before, new FileInfo(DbPath).Length);
        Assert.Equal(2, RollupStoreReport.Inspect(DbPath).LedgerRows);
    }

    /// <summary>
    /// A bundle produced without this pass says so. An absent section cannot be told apart from
    /// one that failed to render, and this one is absent from most bundles by design.
    /// </summary>
    [Fact]
    public void ABundleWithoutTheAuditSaysHowToGetIt()
    {
        Assert.Contains("--diagnose", IngestAuditReport.NotRun.ToClipboardText(), StringComparison.Ordinal);
    }
}
