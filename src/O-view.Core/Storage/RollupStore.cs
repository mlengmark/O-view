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

    private readonly string _path;
    private SqliteConnection _connection;

    /// <summary>
    /// Serialises access to the single connection above.
    ///
    /// <para>Needed since the poll moved off the UI thread (issue #125): a scheduled poll
    /// ingests on the thread pool while a panel opened at the same moment aggregates on the
    /// UI thread. <see cref="SqliteConnection"/> is not thread-safe, and two commands on
    /// one handle is not a race that surfaces as an exception — it corrupts the reader
    /// state and returns wrong numbers, which rule 6 treats as the worst possible failure
    /// mode for this app.</para>
    ///
    /// <para>A lock rather than a connection per thread because the store deliberately owns
    /// exactly one handle: pooling is off so that <see cref="Dispose"/> actually releases
    /// the file, which tests and uninstall both depend on. Contention is not a concern at a
    /// 60 s cadence.</para>
    /// </summary>
    private readonly Lock _gate = new();

    public RollupStore(string? dbPath = null)
    {
        _path = dbPath ?? DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        _connection = Connect(_path);

        // The rollup store is a derived cache — rebuildable from JSONL and the plan
        // history — so a corrupt file must never be fatal. A malformed DB otherwise
        // throws SQLITE_CORRUPT on every query, and because the snapshot path touches
        // the store that blanks the ENTIRE usage display, not just history (issue #16).
        // Detect corruption up front and rebuild from empty, keeping the bad file.
        if (!IsHealthy(_connection))
        {
            RebuildFromCorrupt();
        }

        EnsureSchema(_connection);
    }

    private static SqliteConnection Connect(string path)
    {
        // Pooling off: the store owns one long-lived connection, and pooled handles
        // keep the file locked after Dispose (breaks tests and uninstall).
        var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        return connection;
    }

    /// <summary>
    /// Whole-database integrity probe. Returns false on a malformed file — either
    /// quick_check reports errors, or the read itself throws SQLITE_CORRUPT/NOTADB.
    /// </summary>
    private static bool IsHealthy(SqliteConnection connection)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA quick_check;";
            using var reader = cmd.ExecuteReader();
            return reader.Read() && reader.GetString(0) == "ok";
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private void RebuildFromCorrupt()
    {
        _connection.Dispose();
        BackUpCorruptFiles(_path);
        _connection = Connect(_path);   // originals moved aside → a fresh, empty DB
    }

    /// <summary>
    /// Move the malformed DB (and its WAL/SHM sidecars) aside rather than deleting them,
    /// so the corruption can still be examined. Best-effort: a file that cannot be moved
    /// is deleted instead, and even a total failure leaves a usable empty DB behind.
    ///
    /// <para>The stamp makes each set unique, which is the point — two corruptions a week
    /// apart must not overwrite each other. It also means the directory only ever grows, so
    /// older generations are pruned here, immediately after the move: this is the exact
    /// moment the directory is known to have gained one, and nothing else in the app ever
    /// looks at it (issue #160). What survives is named in the diagnostics bundle, which is
    /// what makes discarding the rest defensible rather than destroying evidence.</para>
    ///
    /// <para>The prune never throws, for the same reason the move does not: this is the
    /// self-heal path, and rule 7 / issue #16 require it to leave a usable empty database
    /// behind whatever else fails.</para>
    /// </summary>
    private static void BackUpCorruptFiles(string path)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
        {
            if (!File.Exists(file)) continue;
            try
            {
                File.Move(file, $"{file}{CorruptBackups.Marker}{stamp}", overwrite: true);
            }
            catch (IOException)
            {
                try { File.Delete(file); } catch (IOException) { }
            }
        }

        CorruptBackups.Prune(path);
    }

    private static void EnsureSchema(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
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
            CREATE TABLE IF NOT EXISTS file_offsets (
                path        TEXT PRIMARY KEY,
                byte_offset INTEGER NOT NULL,
                file_length INTEGER NOT NULL
            );
            -- Read-only since ADR-0011 (weekly resets live in weekly-resets.json now).
            -- Still created so the legacy read works against a fresh database.
            CREATE TABLE IF NOT EXISTS weekly_resets (
                reset_at TEXT PRIMARY KEY
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Weekly resets recorded by versions before ADR-0011, when this store doubled as the
    /// weekly-reset log. It no longer does — that state is unrebuildable and this store
    /// wipes itself on corruption, so it moved to <see cref="WeeklyResetLog"/>. This reader
    /// stays so an upgrade can carry the old rows across; the table is never written again.
    /// </summary>
    public IReadOnlyList<DateTimeOffset> GetLegacyWeeklyResets()
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT reset_at FROM weekly_resets ORDER BY reset_at";
            var result = new List<DateTimeOffset>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal));
            }
            return result;
        }
    }

    /// <summary>
    /// Resume point for a transcript: how far it was parsed, and how long it was at
    /// the time. Both are needed — a file shorter than recorded was replaced, and its
    /// offset must not be trusted. Zeroes when the file is unknown.
    /// </summary>
    public (long Offset, long KnownLength) GetFileOffset(string path)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT byte_offset, file_length FROM file_offsets WHERE path = $path";
            cmd.Parameters.AddWithValue("$path", path);

            using var reader = cmd.ExecuteReader();
            return reader.Read() ? (reader.GetInt64(0), reader.GetInt64(1)) : (0L, 0L);
        }
    }

    public void SetFileOffset(string path, long offset, long fileLength)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO file_offsets (path, byte_offset, file_length)
                VALUES ($path, $offset, $length)
                ON CONFLICT(path) DO UPDATE SET
                    byte_offset = excluded.byte_offset,
                    file_length = excluded.file_length
                """;
            cmd.Parameters.AddWithValue("$path", path);
            cmd.Parameters.AddWithValue("$offset", offset);
            cmd.Parameters.AddWithValue("$length", fileLength);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Upsert records in order. Later records for the same requestId overwrite earlier
    /// ones, so feeding a file in append order leaves the final (most complete) record.
    /// </summary>
    public void Ingest(IEnumerable<TranscriptRecord> records)
    {
        lock (_gate)
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
    }

    /// <summary>Daily (UTC date × model) rollups for [from, to] inclusive.</summary>
    public IReadOnlyList<DailyRollup> GetDailyRollups(DateOnly fromUtc, DateOnly toUtc)
    {
        lock (_gate)
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
    }

    // CountRecordedDays lived here, counting distinct dates with usage to drive the
    // "N of 31 days recorded" label. It is gone rather than left unused: it could only answer
    // "how many days had usage", which is not what coverage means, and a day the user spent
    // away from Claude was indistinguishable from a day before the store existed. Coverage is
    // now derived in PanelStatistics.Build from the same first-recorded-day boundary the graph
    // draws, so the label and the chart cannot disagree (issue #142). Leaving a second, wrong
    // answer to the same question in place is how it would come back.

    /// <summary>
    /// Per-model totals for requests at or after <paramref name="sinceUtc"/>. The daily
    /// rollups are too coarse for a 5-hour window, so this reads the request ledger
    /// directly — the same de-duplicated rows, just filtered by time rather than day.
    /// </summary>
    public IReadOnlyList<DailyRollup> GetUsageSince(DateTimeOffset sinceUtc)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT model,
                       SUM(input_tokens), SUM(cache_creation_tokens),
                       SUM(cache_read_tokens), SUM(output_tokens), COUNT(*)
                FROM ingested_requests
                WHERE last_timestamp >= $since
                GROUP BY model
                ORDER BY model
                """;
            cmd.Parameters.AddWithValue("$since", sinceUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));

            var result = new List<DailyRollup>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new DailyRollup(
                    DateOnly.FromDateTime(sinceUtc.UtcDateTime),
                    reader.GetString(0),
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3),
                    reader.GetInt64(4),
                    reader.GetInt64(5)));
            }
            return result;
        }
    }

    /// <summary>Timestamp of the newest ingested record, or null when the store is empty.</summary>
    public DateTimeOffset? LatestActivityUtc()
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT MAX(last_timestamp) FROM ingested_requests";
            return cmd.ExecuteScalar() is string s
                ? DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)
                : null;
        }
    }

    /// <summary>
    /// Closes the connection. Taken under the gate so a poll still writing on the thread
    /// pool cannot be disposed out from under itself while the app shuts down.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _connection.Dispose();
        }
    }
}
