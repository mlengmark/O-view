using System.Text;
using OView.Core.Models;
using OView.Core.Providers.Jsonl;
using OView.Core.Storage;

namespace OView.Core.Tests;

/// <summary>
/// Incremental reads are an optimisation over an idempotent store, so a bug here
/// does not throw — it silently loses or double-counts records. These pin the
/// resumption boundary.
/// </summary>
public class IncrementalReadTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("oview-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Line(string requestId, long output) =>
        $"{{\"type\":\"assistant\",\"requestId\":\"{requestId}\",\"timestamp\":\"2026-07-21T10:00:00.000Z\"," +
        $"\"message\":{{\"model\":\"claude-opus-4-8\",\"usage\":{{\"input_tokens\":1," +
        $"\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0,\"output_tokens\":{output}}}}}}}";

    private string Write(string name, params string[] lines)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return path;
    }

    [Fact]
    public void ResumingFromOffset_ReadsOnlyNewRecords()
    {
        var path = Write("s.jsonl", Line("req_A", 10), Line("req_B", 20));
        var (first, offset) = TranscriptReader.ReadFrom(path, 0);
        Assert.Equal(2, first.Count);

        File.AppendAllText(path, Line("req_C", 30) + "\n");
        var (second, _) = TranscriptReader.ReadFrom(path, offset);

        Assert.Single(second);
        Assert.Equal("req_C", second[0].RequestId);
    }

    [Fact]
    public void NoNewContent_YieldsNothing_AndHoldsOffset()
    {
        var path = Write("s.jsonl", Line("req_A", 10));
        var (_, offset) = TranscriptReader.ReadFrom(path, 0);

        var (records, next) = TranscriptReader.ReadFrom(path, offset);

        Assert.Empty(records);
        Assert.Equal(offset, next);
    }

    [Fact]
    public void HalfWrittenLine_IsLeftForNextPoll_ThenReadWhole()
    {
        // Poll lands mid-write: the partial line must be neither parsed nor skipped.
        var path = Path.Combine(_dir, "s.jsonl");
        File.WriteAllText(path, Line("req_A", 10) + "\n" + "{\"type\":\"assistant\",\"requestId\":\"req_B");

        var (first, offset) = TranscriptReader.ReadFrom(path, 0);
        Assert.Single(first);
        Assert.Equal("req_A", first[0].RequestId);

        // The writer finishes the line.
        File.WriteAllText(path, Line("req_A", 10) + "\n" + Line("req_B", 20) + "\n");
        var (second, _) = TranscriptReader.ReadFrom(path, offset);

        Assert.Single(second);
        Assert.Equal("req_B", second[0].RequestId);
        Assert.Equal(20, second[0].OutputTokens);
    }

    [Fact]
    public void FileReplacedWithShorterContent_RereadsFromStart()
    {
        var path = Write("s.jsonl", Line("req_A", 10), Line("req_B", 20), Line("req_C", 30));
        var (_, offset) = TranscriptReader.ReadFrom(path, 0);

        // Rotated/replaced: the stored offset now points past the end.
        File.WriteAllText(path, Line("req_X", 99) + "\n");
        var (records, _) = TranscriptReader.ReadFrom(path, offset);

        Assert.Single(records);
        Assert.Equal("req_X", records[0].RequestId);
    }

    [Fact]
    public void MultiByteCharacters_DoNotDesyncTheOffset()
    {
        // Offsets are byte-based; UTF-8 content must not shift the boundary.
        var path = Path.Combine(_dir, "s.jsonl");
        var withUnicode = "{\"type\":\"user\",\"content\":\"café — 日本語 🎉\"}";
        File.WriteAllText(path, withUnicode + "\n" + Line("req_A", 10) + "\n", new UTF8Encoding(false));

        var (first, offset) = TranscriptReader.ReadFrom(path, 0);
        Assert.Single(first);

        File.AppendAllText(path, Line("req_B", 20) + "\n", new UTF8Encoding(false));
        var (second, _) = TranscriptReader.ReadFrom(path, offset);

        Assert.Single(second);
        Assert.Equal("req_B", second[0].RequestId);
    }

    [Fact]
    public void EndToEnd_IncrementalIngest_MatchesFullRescan()
    {
        // The optimisation must not change the answer.
        var projects = Path.Combine(_dir, "projects", "C--Users-X");
        Directory.CreateDirectory(projects);
        var path = Path.Combine(projects, "s.jsonl");
        File.WriteAllText(path, Line("req_A", 10) + "\n" + Line("req_B", 20) + "\n");

        using var incremental = new RollupStore(Path.Combine(_dir, "inc.db"));
        var provider = new JsonlUsageProvider(incremental, Path.Combine(_dir, "projects"));
        provider.GetSnapshot(DateTimeOffset.UtcNow);
        File.AppendAllText(path, Line("req_C", 30) + "\n");
        provider.GetSnapshot(DateTimeOffset.UtcNow);
        provider.GetSnapshot(DateTimeOffset.UtcNow);   // no-op poll

        using var full = new RollupStore(Path.Combine(_dir, "full.db"));
        full.Ingest(TranscriptReader.ReadFile(path));

        var incTotal = incremental.GetDailyRollups(DateOnly.MinValue, DateOnly.MaxValue).Sum(r => r.OutputTokens);
        var fullTotal = full.GetDailyRollups(DateOnly.MinValue, DateOnly.MaxValue).Sum(r => r.OutputTokens);

        Assert.Equal(60, fullTotal);
        Assert.Equal(fullTotal, incTotal);
    }

    [Fact]
    public void OffsetsPersist_AcrossStoreReopen()
    {
        var db = Path.Combine(_dir, "usage.db");
        using (var store = new RollupStore(db))
        {
            store.SetFileOffset(@"C:\x\s.jsonl", 1234, 5678);
        }

        using var reopened = new RollupStore(db);
        var (offset, length) = reopened.GetFileOffset(@"C:\x\s.jsonl");

        Assert.Equal(1234, offset);
        Assert.Equal(5678, length);
    }

    [Fact]
    public void UnknownFile_HasZeroOffset()
    {
        using var store = new RollupStore(Path.Combine(_dir, "usage.db"));

        Assert.Equal((0L, 0L), store.GetFileOffset(@"C:\never\seen.jsonl"));
    }
}
