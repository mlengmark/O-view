using System.Globalization;
using Microsoft.Data.Sqlite;
using OView.Core.Providers.Jsonl;

namespace OView.Core.Storage;

/// <summary>
/// Persistent usage history (ADR-0006). Claude Code deletes its own transcripts after
/// ~30 days, so 31-day figures can never be served from JSONL directly — this store
/// accumulates from install date and survives that cleanup.
///
/// Idempotency and requestId de-duplication share one mechanism: a per-request ledger
/// with request_id as PRIMARY KEY, upserted on every ingest (never blind INSERT —
/// CLAUDE.md rule 7). Re-ingesting the same transcript rewrites identical rows;
/// streaming duplicates of a request overwrite in file order so the last occurrence
/// wins. Daily (UTC date × model) rollups are served by aggregation over the ledger.
/// Ledger rows hold ids, dates, models, and token counts only — no conversation
/// content (ADR-0006 privacy rationale).
/// </summary>
public sealed class RollupStore : IDisposable
{
    /// <summary>Default location: %LOCALAPPDATA%\O-view\usage.db</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "O-view",
        "usage.db");

    private readonly SqliteConnection _connection;

    public RollupStore(string? dbPath = null)
    {
        var path = dbPath ?? DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Pooling off: the store owns one long-lived connection, and pooled handles
        // keep the file locked after Dispose (breaks tests and uninstall).
        _connection = new SqliteConnection($"Data Source={path};Pooling=False");
        _connection.Open();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS ingested_requests (
                request_id            TEXT PRIMARY KEY,
                utc_date              TEXT NOT NULL,
                model                 TEXT NOT NULL,
                input_tokens          INTEGER NOT NULL,
                cache_creation_tokens INTEGER NOT NULL,
                cache_read_tokens     INTEGER NOT NULL,
                output_tokens         INTEGER NOT NULL,
                last_timestamp        TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_requests_date ON ingested_requests(utc_date);
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Upsert records in order. Later records for the same requestId overwrite earlier
    /// ones, so feeding a file in append order leaves the final (most complete) record.
    /// </summary>
    public void Ingest(IEnumerable<TranscriptRecord> records)
    {
        using var transaction = _connection.BeginTransaction();
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO ingested_requests
                (request_id, utc_date, model, input_tokens, cache_creation_tokens,
                 cache_read_tokens, output_tokens, last_timestamp)
            VALUES ($id, $date, $model, $input, $cacheW, $cacheR, $output, $ts)
            ON CONFLICT(request_id) DO UPDATE SET
                utc_date = excluded.utc_date,
                model = excluded.model,
                input_tokens = excluded.input_tokens,
                cache_creation_tokens = excluded.cache_creation_tokens,
                cache_read_tokens = excluded.cache_read_tokens,
                output_tokens = excluded.output_tokens,
                last_timestamp = excluded.last_timestamp
            """;
        var pId = cmd.Parameters.Add("$id", SqliteType.Text);
        var pDate = cmd.Parameters.Add("$date", SqliteType.Text);
        var pModel = cmd.Parameters.Add("$model", SqliteType.Text);
        var pInput = cmd.Parameters.Add("$input", SqliteType.Integer);
        var pCacheW = cmd.Parameters.Add("$cacheW", SqliteType.Integer);
        var pCacheR = cmd.Parameters.Add("$cacheR", SqliteType.Integer);
        var pOutput = cmd.Parameters.Add("$output", SqliteType.Integer);
        var pTs = cmd.Parameters.Add("$ts", SqliteType.Text);

        foreach (var r in records)
        {
            pId.Value = r.RequestId;
            pDate.Value = r.TimestampUtc.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            pModel.Value = r.Model;
            pInput.Value = r.InputTokens;
            pCacheW.Value = r.CacheCreationTokens;
            pCacheR.Value = r.CacheReadTokens;
            pOutput.Value = r.OutputTokens;
            pTs.Value = r.TimestampUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>Daily (UTC date × model) rollups for [from, to] inclusive.</summary>
    public IReadOnlyList<DailyRollup> GetDailyRollups(DateOnly fromUtc, DateOnly toUtc)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT utc_date, model,
                   SUM(input_tokens), SUM(cache_creation_tokens),
                   SUM(cache_read_tokens), SUM(output_tokens), COUNT(*)
            FROM ingested_requests
            WHERE utc_date >= $from AND utc_date <= $to
            GROUP BY utc_date, model
            ORDER BY utc_date, model
            """;
        cmd.Parameters.AddWithValue("$from", fromUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$to", toUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        var result = new List<DailyRollup>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new DailyRollup(
                DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6)));
        }
        return result;
    }

    /// <summary>
    /// Distinct UTC dates with any recorded usage in [from, to] — drives the honest
    /// "N of 31 days recorded" coverage label (ADR-0006: partial history must state
    /// its coverage; it must never read as low usage).
    /// </summary>
    public int CountRecordedDays(DateOnly fromUtc, DateOnly toUtc)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(DISTINCT utc_date) FROM ingested_requests WHERE utc_date >= $from AND utc_date <= $to";
        cmd.Parameters.AddWithValue("$from", fromUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$to", toUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Timestamp of the newest ingested record, or null when the store is empty.</summary>
    public DateTimeOffset? LatestActivityUtc()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT MAX(last_timestamp) FROM ingested_requests";
        return cmd.ExecuteScalar() is string s
            ? DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)
            : null;
    }

    public void Dispose() => _connection.Dispose();
}
