using Microsoft.Data.Sqlite;
using OView.Core.Models;
using OView.Core.Pricing;
using OView.Core.Providers.Jsonl;
using OView.Core.Storage;

namespace OView.Core.Tests;

/// <summary>
/// The cache-write TTL split, from the transcript to the priced figure (GitHub issue #255).
///
/// <para><c>usage.cache_creation</c> carries which TTL each write used, and Anthropic bills the
/// two at different published prices — 1.25× base input for a 5-minute write, 2× for a 1-hour
/// one. The reader took the flat <c>cache_creation_input_tokens</c> beside it and stopped, so
/// every write was priced at the 5-minute rate while the transcripts on the machine this was
/// measured on were almost entirely 1-hour.</para>
///
/// <para>These cover the path rather than the arithmetic — <see cref="CostEstimatorTests"/>
/// pins the two rates. What can silently go wrong here is the attribution being dropped
/// somewhere between the file and the tile, and each hop has its own case.</para>
/// </summary>
public class CacheWriteTtlTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("oview-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string DbPath => Path.Combine(_dir, "usage.db");

    /// <summary>
    /// An assistant record in the shape real transcripts carry it: the flat total, and the
    /// <c>cache_creation</c> object beside it holding the split.
    /// </summary>
    private static string AssistantLine(
        string requestId, long write5m, long write1h,
        string? speed = "standard", string? geo = "not_available")
    {
        var modifiers = (speed is null ? "" : $",\"speed\":\"{speed}\"")
                        + (geo is null ? "" : $",\"inference_geo\":\"{geo}\"");

        return $"{{\"type\":\"assistant\",\"requestId\":\"{requestId}\","
               + "\"timestamp\":\"2026-08-30T12:00:00.000Z\","
               + "\"message\":{\"model\":\"claude-opus-5\",\"usage\":{\"input_tokens\":2,"
               + $"\"cache_creation_input_tokens\":{write5m + write1h},"
               + $"\"cache_creation\":{{\"ephemeral_5m_input_tokens\":{write5m},"
               + $"\"ephemeral_1h_input_tokens\":{write1h}}},"
               + $"\"cache_read_input_tokens\":200,\"output_tokens\":10{modifiers}}}}}}}";
    }

    /// <summary>A record from before <c>cache_creation</c> was read, or one that lacks it.</summary>
    private static string AssistantLineWithoutSplit(string requestId, long cacheWrite) =>
        $"{{\"type\":\"assistant\",\"requestId\":\"{requestId}\","
        + "\"timestamp\":\"2026-08-30T12:00:00.000Z\","
        + "\"message\":{\"model\":\"claude-opus-5\",\"usage\":{\"input_tokens\":2,"
        + $"\"cache_creation_input_tokens\":{cacheWrite},"
        + "\"cache_read_input_tokens\":200,\"output_tokens\":10}}}";

    private string WriteTranscript(string name, params string[] lines)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public void TheReaderTakesTheTtlSplitFromCacheCreation()
    {
        var path = WriteTranscript("s.jsonl", AssistantLine("r1", write5m: 1_582, write1h: 18_206_312));

        var record = Assert.Single(TranscriptReader.ReadFile(path));

        Assert.Equal(1_582, record.Tokens.CacheWrite5m);
        Assert.Equal(18_206_312, record.Tokens.CacheWrite1h);
        Assert.Equal(0, record.Tokens.CacheWriteTtlUnrecorded);

        // The flat field still accounts for every write, so nothing is lost or double-counted.
        Assert.Equal(18_207_894, record.Tokens.CacheWrite);
    }

    /// <summary>
    /// A record with no <c>cache_creation</c> object keeps its writes rather than dropping
    /// them, and keeps them <i>unattributed</i> rather than being assigned to the TTL the rest
    /// of the file happens to use. Guessing the majority would be a fabricated attribution.
    /// </summary>
    [Fact]
    public void ARecordWithNoSplitKeepsItsWritesUnattributed()
    {
        var path = WriteTranscript("s.jsonl", AssistantLineWithoutSplit("r1", cacheWrite: 5_000));

        var record = Assert.Single(TranscriptReader.ReadFile(path));

        Assert.Equal(5_000, record.Tokens.CacheWriteTtlUnrecorded);
        Assert.Equal(5_000, record.Tokens.CacheWrite);
        Assert.Equal(0, record.Tokens.CacheWrite5m);
        Assert.Equal(0, record.Tokens.CacheWrite1h);
    }

    [Fact]
    public void TheReaderReadsBothPricingModifiers()
    {
        var path = WriteTranscript("s.jsonl",
            AssistantLine("standard", 1, 0),
            AssistantLine("fast", 1, 0, speed: "fast", geo: "us"),
            AssistantLine("absent", 1, 0, speed: null, geo: null),
            AssistantLine("odd", 1, 0, speed: "turbo"));

        var records = TranscriptReader.ReadFile(path).ToDictionary(r => r.RequestId);

        Assert.Equal(UsageModifiers.Standard, records["standard"].Modifiers);
        Assert.Equal(UsageModifiers.Standard, records["absent"].Modifiers);
        Assert.True(records["fast"].Modifiers.Fast);
        Assert.True(records["fast"].Modifiers.UsInference);

        // An unrecognised value is not standard. Reading it as standard would price the request
        // at the cheaper rate with nothing on screen to say so (issue #257).
        Assert.True(records["odd"].Modifiers.IsUnpriceable);
    }

    [Fact]
    public void TheStoreRoundTripsTheSplitAndTheModifiers()
    {
        var path = WriteTranscript("s.jsonl",
            AssistantLine("r1", write5m: 100, write1h: 900),
            AssistantLine("r2", write5m: 0, write1h: 1_000, speed: "fast", geo: "us"));

        using var store = new RollupStore(DbPath);
        store.Ingest(TranscriptReader.ReadFile(path));

        var rollups = store.GetDailyRollups(
            DateTimeOffset.MinValue, DateTimeOffset.MaxValue, TimeZoneInfo.Utc);

        // Two buckets, not one: the modifiers are part of the key, because the two rows price
        // differently and a shared bucket would price both at whichever modifier was kept.
        Assert.Equal(2, rollups.Count);

        var standard = rollups.Single(r => r.Modifiers == UsageModifiers.Standard);
        Assert.Equal(100, standard.Tokens.CacheWrite5m);
        Assert.Equal(900, standard.Tokens.CacheWrite1h);
        Assert.Equal(0, standard.Tokens.CacheWriteTtlUnrecorded);

        var fast = rollups.Single(r => r.Modifiers.Fast);
        Assert.True(fast.Modifiers.UsInference);
        Assert.Equal(1_000, fast.Tokens.CacheWrite1h);
    }

    /// <summary>
    /// A row an older build wrote has a cache-write total and NULL beside it. It must read back
    /// as unattributed rather than as zero writes — the total is the authority, and losing it
    /// would silently drop cache-write value out of every historical figure.
    /// </summary>
    [Fact]
    public void RowsFromBeforeTheSplitReadBackAsTtlUnrecorded()
    {
        WriteLegacyRow("legacy", cacheWrite: 4_000);

        using var store = new RollupStore(DbPath);
        var rollup = Assert.Single(store.GetDailyRollups(
            DateTimeOffset.MinValue, DateTimeOffset.MaxValue, TimeZoneInfo.Utc));

        Assert.Equal(4_000, rollup.Tokens.CacheWriteTtlUnrecorded);
        Assert.Equal(4_000, rollup.Tokens.CacheWrite);
        Assert.Equal(UsageModifiers.Standard, rollup.Modifiers);
    }

    /// <summary>
    /// Opening a store that predates the TTL columns rewinds every transcript watermark, so the
    /// next poll re-reads what is still on disk and replaces those unattributed rows with
    /// attributed ones. This is the migration decision the issue settled on: recover what can be
    /// recovered, and leave the rest honestly labelled rather than wiping history that no
    /// transcript can rebuild.
    /// </summary>
    [Fact]
    public void OpeningAPreSplitStoreRewindsTheWatermarksSoTranscriptsAreReRead()
    {
        var db = CreatePreSplitDatabase();
        using (var legacy = new SqliteConnection($"Data Source={db};Pooling=False"))
        {
            legacy.Open();
            using var cmd = legacy.CreateCommand();
            cmd.CommandText = """
                INSERT INTO file_offsets (path, byte_offset, file_length, source, records, counted_from)
                VALUES ('s.jsonl', 4096, 4096, 'claude-code', 12, 0)
                """;
            cmd.ExecuteNonQuery();
        }

        using (var store = new RollupStore(db))
        {
            // Opening it is the migration.
            Assert.Equal((0L, 0L), store.GetFileOffset("s.jsonl"));
        }

        // Re-opening must not rewind again: doing the work on every launch would re-parse every
        // transcript on every start rather than once.
        using (var store = new RollupStore(db))
        {
            store.SetFileOffset("s.jsonl", 2048, 2048);
        }

        using (var store = new RollupStore(db))
        {
            Assert.Equal((2048L, 2048L), store.GetFileOffset("s.jsonl"));
        }
    }

    /// <summary>
    /// A store in the shape the build before this one left behind: the ledger without the TTL
    /// or modifier columns. Written by hand rather than by an older binary, which is the only
    /// way to test a migration from a version that is no longer in the tree.
    /// </summary>
    private string CreatePreSplitDatabase()
    {
        var path = Path.Combine(_dir, "legacy.db");
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
                path         TEXT PRIMARY KEY,
                byte_offset  INTEGER NOT NULL,
                file_length  INTEGER NOT NULL,
                source       TEXT,
                records      INTEGER,
                counted_from INTEGER
            );
            """;
        cmd.ExecuteNonQuery();
        return path;
    }

    private void WriteLegacyRow(string requestId, long cacheWrite)
    {
        var path = CreatePreSplitDatabase();
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO ingested_requests
                    (request_id, utc_date, model, input_tokens, cache_creation_tokens,
                     cache_read_tokens, output_tokens, last_timestamp)
                VALUES ($id, '2026-08-30', 'claude-opus-5', 2, $write, 200, 10,
                        '2026-08-30T12:00:00.0000000Z')
                """;
            cmd.Parameters.AddWithValue("$id", requestId);
            cmd.Parameters.AddWithValue("$write", cacheWrite);
            cmd.ExecuteNonQuery();
        }

        File.Move(path, DbPath, overwrite: true);
    }
}
