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

    private long TotalOutputTokens() =>
        _store.GetDailyRollups(DateOnly.MinValue, DateOnly.MaxValue).Sum(r => r.OutputTokens);

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

    [Fact]
    public void SyntheticPlaceholderRecords_AreSkipped()
    {
        // Claude Code writes error/interruption placeholders with model "<synthetic>"
        // and zero usage. They are not API calls.
        var path = WriteTranscript("session.jsonl",
            "{\"type\":\"assistant\",\"requestId\":\"req_synth\",\"timestamp\":\"2026-07-20T12:00:00.000Z\"," +
            "\"message\":{\"model\":\"<synthetic>\",\"usage\":{\"input_tokens\":0,\"output_tokens\":0," +
            "\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0}}}",
            AssistantLine("req_A", "2026-07-20T12:01:00.000Z", output: 120));

        var records = TranscriptReader.ReadFile(path);

        Assert.Single(records);
        Assert.Equal("req_A", records[0].RequestId);
    }

    [Fact]
    public void WindowsPathMangling_Resolves()
    {
        Assert.Equal("C--Users-X", ClaudeProjectsLocator.MangleCwd(@"C:\Users\X"));
        Assert.Equal("C--Users-Maximilian", ClaudeProjectsLocator.MangleCwd(@"C:\Users\Maximilian"));
    }
}
