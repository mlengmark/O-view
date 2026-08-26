using System.Globalization;
using Microsoft.Data.Sqlite;
using OView.Core.Models;
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
/// wins. Daily (local date × model) rollups are served by aggregation over the ledger —
/// bucketed from last_timestamp at query time, because a local day straddles two UTC
/// ones and the stored utc_date cannot answer for it (issue #211).
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
            -- Every time-ranged read goes through last_timestamp now: the panel's daily
            -- buckets (local days, so utc_date cannot answer — issue #211), the session
            -- window, and the window-start narrowing. ix_requests_date still serves the
            -- store report's MIN/MAX over the ingested day.
            CREATE INDEX IF NOT EXISTS ix_requests_timestamp ON ingested_requests(last_timestamp);
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
    /// This store's own view of itself, read through the connection it already holds.
    ///
    /// <para><b>Deliberately not a fresh connection.</b> A separate reader answers "what is in
    /// the file"; this answers "what does the running app believe is in the file", and the two
    /// disagreeing is a finding rather than a detail. On the machine this was written for they
    /// did disagree, repeatedly, about the same directory.</para>
    ///
    /// <para>Under the same lock as every other command here — <see cref="SqliteConnection"/>
    /// is not thread-safe, and two commands on one handle corrupt the reader state and return
    /// wrong numbers rather than throwing, which is the worst failure this app can have.</para>
    /// </summary>
    public RollupStoreReport Inspect()
    {
        lock (_gate)
        {
            try
            {
                return RollupStoreReport.Read(_connection, _path, RollupStoreReport.LiveInstance);
            }
            catch (Exception ex)
            {
                return RollupStoreReport.Unavailable(
                    _path, RollupStoreReport.LiveInstance, $"{ex.GetType().Name}: {ex.Message}");
            }
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

    /// <summary>
    /// Daily (local date × model) rollups for the instants in <c>[fromUtc, toUtc)</c>.
    ///
    /// <para><b>Bucketed by the caller's local day, not by <c>utc_date</c></b> (issue #211).
    /// Users mean local days, and one of those straddles two UTC ones, so the column the rows
    /// are stored under cannot answer the question — the bucket comes from
    /// <c>last_timestamp</c>, which <c>ingested_requests</c> already carries in full. No
    /// schema change, and nothing about ingestion moves: storing a local date at ingest time
    /// would bake this machine's offset into the row, which is wrong for anyone who travels
    /// and wrong for every historical row after a DST change.</para>
    ///
    /// <para><b>What it costs.</b> Filtering on <c>last_timestamp</c> gives up
    /// <c>ix_requests_date</c>, so <c>ix_requests_timestamp</c> was added to serve this range
    /// scan. SQLite cannot do the timezone conversion, so the rows come back at request grain
    /// and are grouped here — one pass over a 31-day slice of the ledger. Measured rather than
    /// assumed: see <c>RollupStoreQueryCostTests</c>, which builds a ledger the size of a real
    /// one and holds this to the same order as the UTC-keyed query it replaced.</para>
    ///
    /// <para>Half-open on purpose. A local day ends exactly where the next begins, and an
    /// inclusive upper bound would count the boundary instant in both.</para>
    /// </summary>
    public IReadOnlyList<DailyRollup> GetDailyRollups(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, TimeZoneInfo zone)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT last_timestamp, model,
                       input_tokens, cache_creation_tokens,
                       cache_read_tokens, output_tokens
                FROM ingested_requests
                WHERE last_timestamp >= $from AND last_timestamp < $to
                """;
            cmd.Parameters.AddWithValue("$from", fromUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$to", toUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));

            var buckets = new Dictionary<(DateOnly Date, string Model), DailyRollup>();

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (!DateTimeOffset.TryParse(
                        reader.GetString(0), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
                {
                    continue;   // an unparseable stamp is one row, not a failed panel
                }

                var model = reader.GetString(1);
                var key = (LocalDays.DateOf(at, zone), model);
                var running = buckets.GetValueOrDefault(key)
                    ?? new DailyRollup(key.Item1, model, 0, 0, 0, 0, 0);

                buckets[key] = running with
                {
                    InputTokens = running.InputTokens + reader.GetInt64(2),
                    CacheCreationTokens = running.CacheCreationTokens + reader.GetInt64(3),
                    CacheReadTokens = running.CacheReadTokens + reader.GetInt64(4),
                    OutputTokens = running.OutputTokens + reader.GetInt64(5),
                    RequestCount = running.RequestCount + 1,
                };
            }

            return buckets.Values
                .OrderBy(r => r.Date)
                .ThenBy(r => r.Model, StringComparer.Ordinal)
                .ToList();
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
    /// <summary>
    /// Earliest recorded request in <c>(afterUtc, throughUtc]</c>, or null when there was no
    /// activity in that span.
    ///
    /// <para>Exists to narrow the five-hour window's start bracket (GitHub issue #185). A
    /// request at time T proves the window was already running at T, so T is a valid — and
    /// usually much tighter — upper bound than the plan-history sample that first observed
    /// the new window. The bound is only ever moved <i>down</i>, so this cannot make a
    /// forecast later or claim a reset earlier than the evidence allows.</para>
    ///
    /// <para>Exclusive at the lower end deliberately: a request exactly at the previous
    /// sample belongs to the window that was ending, and admitting it would widen the
    /// bracket instead of narrowing it.</para>
    ///
    /// <para>The column holds the <i>last</i> occurrence of each request id (rule 4's
    /// de-duplication keeps the final streamed row). That is still a valid upper bound —
    /// the request demonstrably existed by then — and is at most a few seconds late.</para>
    /// </summary>
    public DateTimeOffset? EarliestRequestBetween(DateTimeOffset afterUtc, DateTimeOffset throughUtc)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT MIN(last_timestamp) FROM ingested_requests
                WHERE last_timestamp > $after AND last_timestamp <= $through
                """;
            cmd.Parameters.AddWithValue("$after", afterUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$through", throughUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));

            return cmd.ExecuteScalar() is string text
                && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
                ? at
                : null;
        }
    }

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
    /// Timestamp of the oldest ingested record, or null when the store is empty.
    ///
    /// <para>This is where recorded history begins, and it draws the line between a day with
    /// no usage and a day with no data — the distinction rule 6 makes the graph render
    /// differently. An instant rather than a date, because which calendar day it lands on
    /// depends on the reader's timezone (issue #211).</para>
    /// </summary>
    public DateTimeOffset? EarliestActivityUtc()
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT MIN(last_timestamp) FROM ingested_requests";
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
