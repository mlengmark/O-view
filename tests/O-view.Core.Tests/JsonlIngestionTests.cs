using OView.Core.Pricing;
using OView.Core.Providers.Jsonl;
using OView.Core.Storage;

namespace OView.Core.Tests;

/// <summary>
/// Covers the two silent double-counting bugs the design is exposed to (build-plan
/// Phase 2: both fail silently and produce confident, wrong numbers).
/// </summary>
public class JsonlIngestionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("oview-tests-").FullName;
    private readonly RollupStore _store;

    public JsonlIngestionTests()
    {
        _store = new RollupStore(Path.Combine(_dir, "usage.db"));
    }

    public void Dispose()
    {
        _store.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private static string AssistantLine(string requestId, string timestamp, long output, long input = 2) =>
        $"{{\"type\":\"assistant\",\"requestId\":\"{requestId}\",\"timestamp\":\"{timestamp}\"," +
        $"\"message\":{{\"model\":\"claude-opus-4-8\",\"usage\":{{\"input_tokens\":{input}," +
        $"\"cache_creation_input_tokens\":100,\"cache_read_input_tokens\":200,\"output_tokens\":{output}}}}}}}";

    private string WriteTranscript(string name, params string[] lines)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllLines(path, lines);
        return path;
    }

    // UTC named explicitly rather than left to the machine: these assertions are about totals,
    // and which day a row lands in is not the question here (issue #211).
    private long TotalOutputTokens() =>
        _store.GetDailyRollups(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, TimeZoneInfo.Utc)
            .Sum(r => r.Tokens.Output);

    // ── MANDATORY TEST 1: requestId de-duplication ─────────────────────────────
    // The real file had 28 records for 12 ids; a naive sum overcounts ~2.3×.
    [Fact]
    public void DuplicateRequestIds_SumEachRequestOnce_LastOccurrenceWins()
    {
        var path = WriteTranscript("session.jsonl",
            AssistantLine("req_A", "2026-07-20T12:00:00.000Z", output: 10),   // partial
            AssistantLine("req_A", "2026-07-20T12:00:01.000Z", output: 50),   // partial
            AssistantLine("req_A", "2026-07-20T12:00:02.000Z", output: 120),  // final
            AssistantLine("req_B", "2026-07-20T12:05:00.000Z", output: 30),   // partial
            AssistantLine("req_B", "2026-07-20T12:05:01.000Z", output: 80));  // final

        _store.Ingest(TranscriptReader.ReadFile(path));

        // Naive summation would report 290. Correct: 120 + 80.
        Assert.Equal(200, TotalOutputTokens());
    }

    // ── MANDATORY TEST 2: idempotent ingest ────────────────────────────────────
    [Fact]
    public void IngestingSameTranscriptTwice_TotalsUnchanged()
    {
        var path = WriteTranscript("session.jsonl",
            AssistantLine("req_A", "2026-07-20T12:00:00.000Z", output: 120),
            AssistantLine("req_B", "2026-07-20T12:05:00.000Z", output: 80));

        _store.Ingest(TranscriptReader.ReadFile(path));
        var afterFirst = TotalOutputTokens();

        _store.Ingest(TranscriptReader.ReadFile(path));

        Assert.Equal(200, afterFirst);
        Assert.Equal(afterFirst, TotalOutputTokens());
    }

    [Fact]
    public void GrowingFile_ReIngest_DoesNotDoubleCountEarlierRequests()
    {
        // Claude Code appends to live transcripts; each poll re-reads the whole file.
        var path = WriteTranscript("session.jsonl",
            AssistantLine("req_A", "2026-07-20T12:00:00.000Z", output: 120));
        _store.Ingest(TranscriptReader.ReadFile(path));

        File.AppendAllLines(path, [
            AssistantLine("req_A", "2026-07-20T12:00:05.000Z", output: 150),  // req_A updated
            AssistantLine("req_B", "2026-07-20T12:05:00.000Z", output: 80),   // new request
        ]);
        _store.Ingest(TranscriptReader.ReadFile(path));

        Assert.Equal(230, TotalOutputTokens());
    }

    [Fact]
    public void TruncatedFinalLine_IsSkipped_NotFatal()
    {
        var path = Path.Combine(_dir, "session.jsonl");
        File.WriteAllText(path,
            AssistantLine("req_A", "2026-07-20T12:00:00.000Z", output: 120) + "\n" +
            "{\"type\":\"assistant\",\"requestId\":\"req_B\",\"time");  // partial flush

        var records = TranscriptReader.ReadFile(path);

        Assert.Single(records);
        Assert.Equal("req_A", records[0].RequestId);
    }

    [Fact]
    public void MalformedAndNonAssistantLines_AreSkipped()
    {
        var path = WriteTranscript("session.jsonl",
            "{\"type\":\"user\",\"content\":\"hello\"}",
            "not json at all",
            "{\"type\":\"queue-operation\"}",
            "",
            AssistantLine("req_A", "2026-07-20T12:00:00.000Z", output: 120),
            "{\"type\":\"assistant\",\"requestId\":\"req_no_usage\",\"timestamp\":\"2026-07-20T12:01:00.000Z\",\"message\":{\"model\":\"m\"}}",
            "{\"type\":\"some-future-type\",\"requestId\":\"x\"}");

        var records = TranscriptReader.ReadFile(path);

        Assert.Single(records);
        Assert.Equal("req_A", records[0].RequestId);
    }

    [Fact]
    public void FileOpenForWriting_CanStillBeRead()
    {
        // Claude Code holds live transcripts open for append while we scan.
        var path = WriteTranscript("session.jsonl",
            AssistantLine("req_A", "2026-07-20T12:00:00.000Z", output: 120));

        using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var records = TranscriptReader.ReadFile(path);

        Assert.Single(records);
    }

    [Fact]
    public void SameRequestIdAcrossFiles_CountedOnce()
    {
        // Session resumption can copy records into a new transcript.
        var a = WriteTranscript("a.jsonl", AssistantLine("req_A", "2026-07-20T12:00:00.000Z", output: 120));
        var b = WriteTranscript("b.jsonl", AssistantLine("req_A", "2026-07-20T12:00:00.000Z", output: 120));

        _store.Ingest(TranscriptReader.ReadFile(a));
        _store.Ingest(TranscriptReader.ReadFile(b));

        Assert.Equal(120, TotalOutputTokens());
    }

    [Theory]
    [InlineData("<synthetic>")]
    [InlineData("<Synthetic>")]     // casing must not decide whether one is stored
    [InlineData("<SYNTHETIC>")]
    public void SyntheticPlaceholderRecords_AreSkipped(string model)
    {
        // Claude Code writes error/interruption placeholders with this model and zero
        // usage. They are not API calls.
        //
        // This is the ONLY place they are handled (issue #57). CostEstimator and
        // ModelDisplayName each used to carry their own branch for the id — unreachable
        // because of this filter, and case-INsensitive where this one was not, so an
        // upstream casing change would have silently promoted the dead code to live and
        // changed whether these records count toward DailyRollup.RequestCount.
        var path = WriteTranscript("session.jsonl",
            "{\"type\":\"assistant\",\"requestId\":\"req_synth\",\"timestamp\":\"2026-07-20T12:00:00.000Z\"," +
            "\"message\":{\"model\":\"" + model + "\",\"usage\":{\"input_tokens\":0,\"output_tokens\":0," +
            "\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0}}}",
            AssistantLine("req_A", "2026-07-20T12:01:00.000Z", output: 120));

        var records = TranscriptReader.ReadFile(path);

        Assert.Single(records);
        Assert.Equal("req_A", records[0].RequestId);
    }

    [Fact]
    public void SyntheticRecords_NeverReachTheStore_SoTheyCannotBeCountedOrPriced()
    {
        // The end-to-end guarantee the two downstream branches were duplicating: no row
        // with this model id can enter the ledger, whatever usage the record claims.
        // Deliberately NON-zero here — the guarantee must rest on the record's shape,
        // not on its usage happening to be zero on the transcripts measured so far.
        var path = WriteTranscript("session.jsonl",
            "{\"type\":\"assistant\",\"requestId\":\"req_synth\",\"timestamp\":\"2026-07-20T12:00:00.000Z\"," +
            "\"message\":{\"model\":\"<synthetic>\",\"usage\":{\"input_tokens\":5000,\"output_tokens\":5000," +
            "\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0}}}",
            AssistantLine("req_A", "2026-07-20T12:01:00.000Z", output: 120));

        _store.Ingest(TranscriptReader.ReadFile(path));

        var rollups = _store.GetDailyRollups(
            DateTimeOffset.MinValue, DateTimeOffset.MaxValue, TimeZoneInfo.Utc);

        Assert.DoesNotContain("<synthetic>", rollups.Select(r => r.Model));
        Assert.Equal(120, TotalOutputTokens());
        // And not counted as a request either — the behaviour that has always shipped.
        Assert.Equal(1, rollups.Sum(r => r.RequestCount));
    }

    // A WindowsPathMangling_Resolves test used to sit here, and it was the only caller of
    // ClaudeProjectsLocator.MangleCwd anywhere. Ingestion locates transcripts by walking
    // TranscriptFileScan, never by mangling a cwd into a directory name, so the method was
    // production code kept alive solely by its own test. Both are gone; the convention it
    // documented is recorded in docs/findings/jsonl-schema.md, which is where it belongs.
}
