using OView.Core.Pricing;
using Microsoft.Data.Sqlite;
using OView.Core.Providers.Jsonl;
using OView.Core.Storage;

namespace OView.Core.Tests;

/// <summary>
/// Where the ledger's rows came from (GitHub issue #218).
///
/// <para>The store could report how many rows it held and nothing whatever about their source.
/// On the machine that prompted this, 98.5% of the transcript bytes were Cowork and the bundle's
/// two facts — "36 Cowork files, 58 MB" and "407 ledger rows" — could not be read against each
/// other by anyone, including the person who wrote both lines.</para>
/// </summary>
public class IngestAttributionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("oview-attribution-").FullName;

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

    /// <summary>Claude Code's spelling of the request id.</summary>
    private static string ClaudeCodeRecord(string requestId, long outputTokens = 120) =>
        "{\"type\":\"assistant\",\"requestId\":\"" + requestId + "\","
        + "\"timestamp\":\"2026-08-20T10:00:00Z\","
        + "\"message\":{\"model\":\"claude-opus-5\",\"usage\":"
        + "{\"input_tokens\":10,\"output_tokens\":" + outputTokens + "}}}";

    /// <summary>Cowork's spelling of the same field, on an otherwise identical record (rule 4).</summary>
    private static string CoworkRecord(string requestId, long outputTokens = 300) =>
        "{\"type\":\"assistant\",\"request_id\":\"" + requestId + "\","
        + "\"timestamp\":\"2026-08-21T10:00:00Z\","
        + "\"message\":{\"model\":\"claude-opus-5\",\"usage\":"
        + "{\"input_tokens\":20,\"output_tokens\":" + outputTokens + "}}}";

    /// <summary>A projects root and a Cowork sandbox, laid out as the real machine has them.</summary>
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
    /// <b>The line the whole change exists for.</b> Both surfaces ingest, and the ledger says
    /// which rows are whose — so "58 MB of Cowork transcripts" and "407 rows" stop being two
    /// unrelated numbers in the same report.
    /// </summary>
    [Fact]
    public void TheLedgerRecordsWhichSurfaceEachRequestCameFrom()
    {
        var (projects, cowork) = BuildLayout(
            ClaudeCodeRecord("cc-1") + "\n" + ClaudeCodeRecord("cc-2") + "\n",
            CoworkRecord("cw-1") + "\n");

        using (var store = new RollupStore(DbPath))
        {
            new JsonlUsageProvider(store, projects, [cowork]).GetSnapshot(DateTimeOffset.UnixEpoch);
        }

        var report = RollupStoreReport.Inspect(DbPath);
        var byName = report.LedgerBySource!.ToDictionary(s => s.Source, StringComparer.Ordinal);

        Assert.Equal(2, byName[TranscriptSources.ClaudeCode].Rows);
        Assert.Equal(1, byName[TranscriptSources.Cowork].Rows);

        // Tokens, not just rows: the tiles are made of these, and a source contributing many
        // rows of nothing is a different machine from one contributing few rows of plenty.
        Assert.Equal((10 + 120) * 2, byName[TranscriptSources.ClaudeCode].Tokens);
        Assert.Equal(20 + 300, byName[TranscriptSources.Cowork].Tokens);
    }

    /// <summary>
    /// <b>The attribution that was inverted.</b> Cowork runs its sessions through Claude Code,
    /// so its transcripts land under the Claude Code root — and labelling by locator therefore
    /// stamped every one of them "Claude Code". Measured on the development machine: 28 of 30
    /// files there, 107.7 MB of 107.9 MB, belonged to registered Cowork sessions while the
    /// bundle reported <c>Cowork: 0 rows</c>.
    ///
    /// <para>The register is the authority. A transcript whose file name is a registered
    /// <c>cliSessionId</c> is Cowork's, wherever it sits.</para>
    /// </summary>
    [Fact]
    public void ATranscriptWrittenByARegisteredCoworkSessionIsAttributedToCowork()
    {
        var (projects, cowork) = BuildLayout(ClaudeCodeRecord("cc-1") + "\n", cowork: null);

        // The same location, one file per surface: one registered to Cowork, one not.
        File.Move(Path.Combine(projects, "session.jsonl"), Path.Combine(projects, "sess-cowork.jsonl"));
        File.WriteAllText(Path.Combine(projects, "sess-cli.jsonl"), ClaudeCodeRecord("cc-2", 500) + "\n");

        var registry = RegisterCoworkSession("sess-cowork");

        using (var store = new RollupStore(DbPath))
        {
            new JsonlUsageProvider(store, projects, [cowork], [registry])
                .GetSnapshot(DateTimeOffset.UnixEpoch);
        }

        var byName = RollupStoreReport.Inspect(DbPath).LedgerBySource!
            .ToDictionary(s => s.Source, StringComparer.Ordinal);

        Assert.Equal(1, byName[TranscriptSources.Cowork].Rows);
        Assert.Equal(10 + 120, byName[TranscriptSources.Cowork].Tokens);
        Assert.Equal(1, byName[TranscriptSources.ClaudeCode].Rows);
        Assert.Equal(10 + 500, byName[TranscriptSources.ClaudeCode].Tokens);
    }

    /// <summary>
    /// The match is on the session id, not on where the file sits — so it survives Claude Code
    /// moving transcripts again, which is the change that broke the previous rule.
    /// </summary>
    [Fact]
    public void TheMatchIsBySessionIdRatherThanByLocation()
    {
        var (projects, cowork) = BuildLayout(claudeCode: null, cowork: null);
        var nested = Directory.CreateDirectory(
            Path.Combine(projects, "some--encoding--nobody--predicted")).FullName;
        File.WriteAllText(Path.Combine(nested, "sess-moved.jsonl"), ClaudeCodeRecord("cc-1") + "\n");

        var registry = RegisterCoworkSession("sess-moved");

        using (var store = new RollupStore(DbPath))
        {
            new JsonlUsageProvider(store, projects, [cowork], [registry])
                .GetSnapshot(DateTimeOffset.UnixEpoch);
        }

        var cowokRows = RollupStoreReport.Inspect(DbPath).LedgerBySource!
            .Single(s => s.Source == TranscriptSources.Cowork);

        Assert.Equal(1, cowokRows.Rows);
    }

    /// <summary>
    /// No registry named means nothing is reclassified. A provider that reached for a machine
    /// default here would attribute a test's fixtures from this developer's own sessions — the
    /// hazard issue #212 was about.
    /// </summary>
    [Fact]
    public void WithNoRegistryEverythingUnderTheProjectsRootStaysClaudeCode()
    {
        var (projects, cowork) = BuildLayout(ClaudeCodeRecord("cc-1") + "\n", cowork: null);

        using (var store = new RollupStore(DbPath))
        {
            new JsonlUsageProvider(store, projects, [cowork]).GetSnapshot(DateTimeOffset.UnixEpoch);
        }

        var bySource = RollupStoreReport.Inspect(DbPath).LedgerBySource!;

        Assert.Equal(1, bySource.Single(s => s.Source == TranscriptSources.ClaudeCode).Rows);

        // A surface that contributed nothing has no row here at all — the report supplies the
        // zero when it prints, so its absence is the same statement.
        Assert.DoesNotContain(bySource, s => s.Source == TranscriptSources.Cowork);
    }

    /// <summary>Writes a Cowork registration naming <paramref name="sessionId"/>, and returns its root.</summary>
    private string RegisterCoworkSession(string sessionId)
    {
        var root = Directory.CreateDirectory(
            Path.Combine(_dir, CoworkSessionReport.SessionsDirectoryName, "org", "user")).FullName;

        File.WriteAllText(
            Path.Combine(root, $"local_{sessionId}.json"),
            $$"""{"cliSessionId": "{{sessionId}}", "cwd": "C:\\work", "lastActivityAt": 1787650518127}""");

        return Path.Combine(_dir, CoworkSessionReport.SessionsDirectoryName);
    }

    /// <summary>
    /// A surface with no rows is printed, not omitted. "Cowork 0 row(s)" beside 36 Cowork files
    /// in the section above is the entire report — and it cannot be seen if a zero means the
    /// line is skipped. Same reasoning as <see cref="TranscriptScopeReport.CoverageLine"/>
    /// naming an absent surface rather than listing only what was found (issue #44).
    /// </summary>
    [Fact]
    public void ASurfaceThatContributedNothingIsNamedWithItsZero()
    {
        var (projects, cowork) = BuildLayout(ClaudeCodeRecord("cc-1") + "\n", cowork: null);

        using (var store = new RollupStore(DbPath))
        {
            new JsonlUsageProvider(store, projects, [cowork]).GetSnapshot(DateTimeOffset.UnixEpoch);
        }

        var text = RollupStoreReport.Inspect(DbPath).ToClipboardText();

        Assert.Contains("Cowork     : 0 row(s)", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Rows written before the column existed are <c>unattributed</c> — never folded into the
    /// likelier surface. Every existing install carries a whole ledger of them, and a report
    /// that quietly called them Claude Code would be a fabricated number in the one document a
    /// person reads when they already doubt the figures (rule 6).
    /// </summary>
    [Fact]
    public void RowsIngestedWithoutASourceAreReportedAsUnattributed()
    {
        using (var store = new RollupStore(DbPath))
        {
            store.Ingest([new TranscriptRecord(
                "legacy-1", DateTimeOffset.Parse("2026-07-01T00:00:00Z"), "claude-opus-5",
                new TokenSplit(10, 0, 0, 0, 0, 120), UsageModifiers.Standard)]);
        }

        var report = RollupStoreReport.Inspect(DbPath);
        var unattributed = report.LedgerBySource!
            .Single(s => s.Source == TranscriptSources.Unattributed);

        Assert.Equal(1, unattributed.Rows);
        Assert.Contains("unattributed", report.ToClipboardText(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A store whose schema has no source column at all says so, and does not report every row
    /// as unattributed instead. Those are different facts about different machines, and only one
    /// of them is fixed by waiting for the next poll.
    /// </summary>
    [Fact]
    public void AStoreWrittenBeforeTheColumnExistedIsDistinguishedFromOneWithNoAttribution()
    {
        WriteLegacySchema(DbPath);

        var report = RollupStoreReport.Inspect(DbPath);

        Assert.False(report.AttributionRecorded);
        Assert.Contains("not recorded by the build that wrote this store",
            report.ToClipboardText(), StringComparison.Ordinal);
    }

    /// <summary>Opening the store migrates it in place, without touching the rows it holds.</summary>
    [Fact]
    public void OpeningALegacyStoreAddsTheColumnsAndKeepsTheHistory()
    {
        WriteLegacySchema(DbPath, withRow: true);

        using (var store = new RollupStore(DbPath))
        {
            Assert.Equal(RollupStoreReport.LiveInstance, store.Inspect().Origin);
        }

        var report = RollupStoreReport.Inspect(DbPath);

        Assert.True(report.AttributionRecorded);

        // The point of migrating rather than rebuilding: the store accumulates from install
        // date precisely because Claude Code deletes its own transcripts after ~30 days
        // (ADR-0006), so a rebuild silently costs every day older than that.
        Assert.Equal(1, report.LedgerRows);
    }

    /// <summary>
    /// A watermark advanced over a file that produced no row is counted and named. On an
    /// append-only transcript nothing ever revisits a file that is "0 behind", so this is the
    /// only place such a file is ever mentioned again.
    /// </summary>
    [Fact]
    public void AFileReadWholeThatStoredNothingIsCounted()
    {
        // Parses cleanly and carries no assistant record — the same shape a file whose records
        // the reader cannot understand would leave behind.
        var (projects, cowork) = BuildLayout(
            "{\"type\":\"user\",\"timestamp\":\"2026-08-20T10:00:00Z\"}\n", cowork: null);

        using (var store = new RollupStore(DbPath))
        {
            new JsonlUsageProvider(store, projects, [cowork]).GetSnapshot(DateTimeOffset.UnixEpoch);
        }

        var report = RollupStoreReport.Inspect(DbPath);
        var claudeCode = report.WatermarksBySource!
            .Single(s => s.Source == TranscriptSources.ClaudeCode);

        Assert.Equal(1, claudeCode.Files);
        Assert.Equal(1, claudeCode.FullyRead);
        Assert.Equal(0, claudeCode.Records);
        Assert.Equal(1, claudeCode.Silent);
        Assert.Contains("produced no ledger row", report.ToClipboardText(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A watermark inherited from a build that recorded no counting window is reported as
    /// unknown, not as silent. "Nothing since we started looking" is not evidence that nothing
    /// was ever stored, and the file is never re-read to find out — which is exactly why
    /// <see cref="IngestAuditReport"/> exists.
    /// </summary>
    [Fact]
    public void AnInheritedWatermarkIsUnknownRatherThanSilent()
    {
        var (projects, _) = BuildLayout(ClaudeCodeRecord("cc-1") + "\n", cowork: null);
        var transcript = Path.Combine(projects, "session.jsonl");

        // A watermark at EOF with no source, no count and no counting window: precisely what an
        // older build leaves behind.
        using (var store = new RollupStore(DbPath))
        {
            store.SetFileOffset(transcript, new FileInfo(transcript).Length, new FileInfo(transcript).Length);
        }

        var report = RollupStoreReport.Inspect(DbPath);
        var inherited = report.WatermarksBySource!
            .Single(s => s.Source == TranscriptSources.Unattributed);

        Assert.Equal(1, inherited.Files);
        Assert.Equal(1, inherited.FullyRead);
        Assert.Equal(1, inherited.UnknownCoverage);
        Assert.Equal(0, inherited.Silent);
    }

    /// <summary>The schema as it stood before the attribution columns were added.</summary>
    private static void WriteLegacySchema(string path, bool withRow = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE ingested_requests (
                request_id            TEXT PRIMARY KEY,
                utc_date              TEXT NOT NULL,
                model                 TEXT NOT NULL,
                input_tokens          INTEGER NOT NULL,
                cache_creation_tokens INTEGER NOT NULL,
                cache_read_tokens     INTEGER NOT NULL,
                output_tokens         INTEGER NOT NULL,
                last_timestamp        TEXT NOT NULL
            );
            CREATE TABLE file_offsets (
                path        TEXT PRIMARY KEY,
                byte_offset INTEGER NOT NULL,
                file_length INTEGER NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();

        if (!withRow) return;

        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO ingested_requests VALUES
                ('legacy-1', '2026-07-01', 'claude-opus-5', 10, 0, 0, 120, '2026-07-01T00:00:00.0000000Z');
            """;
        insert.ExecuteNonQuery();
    }
}
