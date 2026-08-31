using System.Globalization;
using Microsoft.Data.Sqlite;
using OView.Core.Models;
using OView.Core.Pricing;
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

    /// <summary>
    /// What stood beside the database when this store opened it (issue #213).
    ///
    /// <para>Surfaced rather than kept private: a defence that acts silently is the same class
    /// of problem as the thing it defends against, so this reaches the log and the diagnostics
    /// bundle. It also carries the case where the check could <b>not</b> be made, which a
    /// reader needs in order to know the difference between "the journal was fine" and "nobody
    /// established anything about the journal".</para>
    /// </summary>
    public StaleJournalCheck JournalGuard { get; }

    public RollupStore(string? dbPath = null)
    {
        _path = dbPath ?? DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        // BEFORE the connection, and this ordering is the whole defence: SQLite recovers from
        // a -wal as it opens the file, so by the time there is a connection to ask, an orphan
        // has already been folded in and quick_check reports ok on the result (issue #213).
        JournalGuard = StaleJournal.Guard(_path);

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

        AddAttributionColumns(connection);
        AddPricingColumns(connection);
    }

    /// <summary>
    /// The columns that say <b>how</b> a ledger row is priced (issues #255 and #257): which TTL
    /// each cache write used, and the two published pricing modifiers.
    ///
    /// <para>Added the same way the attribution columns are, and nullable for the same reason —
    /// NULL means "written before this build was tracking it", which is a different fact from
    /// zero. A NULL TTL pair is not "no cache writes"; it is a cache-write total with no
    /// attribution, which <see cref="GetDailyRollups"/> reports as
    /// <see cref="TokenSplit.CacheWriteTtlUnrecorded"/> and the panel names in its caveat.</para>
    ///
    /// <para><b>Adding the TTL columns re-reads the transcripts.</b> Existing rows cannot be
    /// back-filled from the store — the attribution is in the transcripts, and the ones that
    /// would answer for the oldest rows are the ones Claude Code has since deleted. Resetting
    /// the watermarks makes the next poll read every transcript still on disk from byte 0 and
    /// upsert its rows with the split, which recovers recent history exactly; what is out of
    /// that reach stays unattributed and says so. Wiping the store instead would have thrown
    /// away every day whose transcript is gone, which is the history ADR-0006 exists to keep.</para>
    /// </summary>
    private static void AddPricingColumns(SqliteConnection connection)
    {
        // Which TTL each cache write used. cache_creation_tokens stays the total, so these two
        // are a decomposition of a column that is already there rather than a replacement.
        var added = AddColumnIfMissing(connection, "ingested_requests", "cache_write_5m_tokens", "INTEGER");
        added |= AddColumnIfMissing(connection, "ingested_requests", "cache_write_1h_tokens", "INTEGER");

        // usage.speed and usage.inference_geo. NULL is the standard case as well as the
        // never-recorded one — see UsageModifiers.SpeedText for why those share a value.
        AddColumnIfMissing(connection, "ingested_requests", "speed", "TEXT");
        AddColumnIfMissing(connection, "ingested_requests", "inference_geo", "TEXT");

        if (added)
        {
            ResetWatermarks(connection);
        }
    }

    /// <summary>
    /// Rewinds every transcript watermark so the next poll re-reads what is still on disk.
    ///
    /// <para>An update rather than a delete: the rows also carry which surface wrote each file
    /// and when it last yielded a record, and losing that to a migration would blank the
    /// attribution section of the support bundle for no reason. The counters are zeroed and
    /// <c>counted_from</c> is cleared so the next <see cref="SetFileOffset"/> pins the fresh
    /// zero — a count carried over from before the rewind would describe a different read.</para>
    ///
    /// <para>Costs one full re-parse of every transcript on the first poll after upgrade. That
    /// is the first-run cost this store already pays once, and it runs off the UI thread
    /// (issue #125). Ingestion is idempotent, so nothing double-counts (rule 7).</para>
    /// </summary>
    private static void ResetWatermarks(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE file_offsets
            SET byte_offset = 0, file_length = 0, records = 0, counted_from = NULL
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// The columns that say <b>where</b> a ledger row and a watermark came from (issue #218).
    ///
    /// <para>Added rather than rebuilt into the CREATE statements above: a store that already
    /// exists is never re-created, so a column only listed there would reach a fresh install and
    /// no other. The ledger is rebuildable in principle, but a rebuild costs every day of
    /// history older than Claude Code's own ~30-day cleanup — precisely what ADR-0006 exists to
    /// keep — so a migration that adds a column is the only acceptable shape here.</para>
    ///
    /// <para><b>Every one of them is nullable with no default, and that is the design.</b> NULL
    /// means "written before this build was tracking it", which is a different fact from zero
    /// and is reported as a different word. Back-filling a default would convert an unknown into
    /// a confident figure in the one report a person reads when they already doubt the
    /// numbers.</para>
    /// </summary>
    private static void AddAttributionColumns(SqliteConnection connection)
    {
        // Which surface the request was parsed from.
        AddColumnIfMissing(connection, "ingested_requests", "source", "TEXT");

        // Same, for the watermark — so the per-source file accounting needs no path parsing,
        // and stays right if a root ever moves.
        AddColumnIfMissing(connection, "file_offsets", "source", "TEXT");

        // Records this store has actually ingested out of this file, cumulative.
        AddColumnIfMissing(connection, "file_offsets", "records", "INTEGER");

        // The byte offset that count started from. Without it the count is uninterpretable:
        // "0 records" from a file first seen by an older build says nothing, while "0 records"
        // counted from byte 0 of a fully-read file is the finding itself.
        AddColumnIfMissing(connection, "file_offsets", "counted_from", "INTEGER");

        // When this file last yielded a record. Separates "never produced anything" from
        // "produced plenty, months ago" without a second query.
        AddColumnIfMissing(connection, "file_offsets", "last_ingest_utc", "TEXT");
    }

    /// <summary>
    /// SQLite has no <c>ADD COLUMN IF NOT EXISTS</c>, so the column list is read first. Checked
    /// rather than attempted-and-caught because a duplicate-column error and a genuinely broken
    /// store arrive as the same <see cref="SqliteException"/>, and swallowing the second to
    /// tolerate the first is how a corrupt database gets treated as an up-to-date one.
    /// </summary>
    /// <returns>
    /// True when the column was actually added — that is, when this store is being migrated
    /// rather than opened. A migration that also has to re-read data needs to know the
    /// difference, and doing that work on every launch would re-parse every transcript every
    /// time (see <see cref="AddPricingColumns"/>).
    /// </returns>
    private static bool AddColumnIfMissing(
        SqliteConnection connection, string table, string column, string type)
    {
        using (var probe = connection.CreateCommand())
        {
            probe.CommandText = $"PRAGMA table_info({table});";
            using var reader = probe.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type};";
        alter.ExecuteNonQuery();
        return true;
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
                return RollupStoreReport.Read(
                    _connection, _path, RollupStoreReport.LiveInstance, JournalGuard);
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

    /// <summary>
    /// Records the resume point, and what reading up to it actually yielded.
    ///
    /// <para><paramref name="recordsAdded"/> and <paramref name="countedFrom"/> exist together
    /// or not at all: a record count is meaningless without the offset it was counted from
    /// (issue #218). Counting from byte 0 means the number covers the whole file, so a zero is
    /// evidence — the file was read end to end and produced nothing. A count that began
    /// mid-file, because an earlier build had already advanced the watermark, can only ever say
    /// "nothing since", and <see cref="RollupStoreReport"/> prints it as exactly that.</para>
    ///
    /// <para><c>counted_from</c> is written with COALESCE so it pins the <i>first</i> offset
    /// this build observed and never moves again; <c>records</c> accumulates beside it. A NULL
    /// pair is a watermark inherited from a build that recorded neither.</para>
    /// </summary>
    /// <param name="source">
    /// Which surface writes this file (<see cref="TranscriptSources"/>). Null leaves the
    /// existing value alone rather than clearing it, so a caller that does not know cannot
    /// erase what a caller that did know already stored.
    /// </param>
    public void SetFileOffset(
        string path,
        long offset,
        long fileLength,
        string? source = null,
        int recordsAdded = 0,
        long? countedFrom = null)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO file_offsets
                    (path, byte_offset, file_length, source, records, counted_from, last_ingest_utc)
                VALUES ($path, $offset, $length, $source, $added, $countedFrom, $ingestedAt)
                ON CONFLICT(path) DO UPDATE SET
                    byte_offset     = excluded.byte_offset,
                    file_length     = excluded.file_length,
                    source          = COALESCE(excluded.source, file_offsets.source),
                    records         = COALESCE(file_offsets.records, 0) + $added,
                    counted_from    = COALESCE(file_offsets.counted_from, excluded.counted_from),
                    last_ingest_utc = COALESCE(excluded.last_ingest_utc, file_offsets.last_ingest_utc)
                """;
            cmd.Parameters.AddWithValue("$path", path);
            cmd.Parameters.AddWithValue("$offset", offset);
            cmd.Parameters.AddWithValue("$length", fileLength);
            cmd.Parameters.AddWithValue("$source", (object?)source ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$added", recordsAdded);
            cmd.Parameters.AddWithValue("$countedFrom", (object?)countedFrom ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ingestedAt", recordsAdded > 0
                ? DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
                : (object)DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Upsert records in order. Later records for the same requestId overwrite earlier
    /// ones, so feeding a file in append order leaves the final (most complete) record.
    /// </summary>
    /// <param name="source">
    /// Which surface these were parsed from (<see cref="TranscriptSources"/>), stored beside
    /// each row so the ledger can be summed per source in the support bundle (issue #218).
    ///
    /// <para>Null writes NULL, which reads back as <see cref="TranscriptSources.Unattributed"/>
    /// — the honest answer for every row an older build wrote, and for any caller that does not
    /// know. It is deliberately not defaulted to the likelier surface.</para>
    /// </param>
    public void Ingest(IEnumerable<TranscriptRecord> records, string? source = null)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
                INSERT INTO ingested_requests
                    (request_id, utc_date, model, input_tokens, cache_creation_tokens,
                     cache_write_5m_tokens, cache_write_1h_tokens,
                     cache_read_tokens, output_tokens, last_timestamp, source,
                     speed, inference_geo)
                VALUES ($id, $date, $model, $input, $cacheW, $cacheW5m, $cacheW1h,
                        $cacheR, $output, $ts, $source, $speed, $geo)
                ON CONFLICT(request_id) DO UPDATE SET
                    utc_date = excluded.utc_date,
                    model = excluded.model,
                    input_tokens = excluded.input_tokens,
                    cache_creation_tokens = excluded.cache_creation_tokens,
                    cache_write_5m_tokens = excluded.cache_write_5m_tokens,
                    cache_write_1h_tokens = excluded.cache_write_1h_tokens,
                    cache_read_tokens = excluded.cache_read_tokens,
                    output_tokens = excluded.output_tokens,
                    last_timestamp = excluded.last_timestamp,
                    source = COALESCE(excluded.source, ingested_requests.source),
                    speed = excluded.speed,
                    inference_geo = excluded.inference_geo
                """;
            var pId = cmd.Parameters.Add("$id", SqliteType.Text);
            var pDate = cmd.Parameters.Add("$date", SqliteType.Text);
            var pModel = cmd.Parameters.Add("$model", SqliteType.Text);
            var pInput = cmd.Parameters.Add("$input", SqliteType.Integer);
            var pCacheW = cmd.Parameters.Add("$cacheW", SqliteType.Integer);
            var pCacheW5m = cmd.Parameters.Add("$cacheW5m", SqliteType.Integer);
            var pCacheW1h = cmd.Parameters.Add("$cacheW1h", SqliteType.Integer);
            var pCacheR = cmd.Parameters.Add("$cacheR", SqliteType.Integer);
            var pOutput = cmd.Parameters.Add("$output", SqliteType.Integer);
            var pTs = cmd.Parameters.Add("$ts", SqliteType.Text);
            var pSpeed = cmd.Parameters.Add("$speed", SqliteType.Text);
            var pGeo = cmd.Parameters.Add("$geo", SqliteType.Text);
            cmd.Parameters.AddWithValue("$source", (object?)source ?? DBNull.Value);

            foreach (var r in records)
            {
                pId.Value = r.RequestId;
                pDate.Value = r.TimestampUtc.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                pModel.Value = r.Model;
                pInput.Value = r.Tokens.Input;
                // The flat total stays the authority on how much was written, and the two TTL
                // columns say how it split — a row re-read by this build always agrees with
                // itself, and one written by an older build has a total with NULLs beside it.
                pCacheW.Value = r.Tokens.CacheWrite;
                pCacheW5m.Value = r.Tokens.CacheWrite5m;
                pCacheW1h.Value = r.Tokens.CacheWrite1h;
                pCacheR.Value = r.Tokens.CacheRead;
                pOutput.Value = r.Tokens.Output;
                pTs.Value = r.TimestampUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
                pSpeed.Value = (object?)r.Modifiers.SpeedText ?? DBNull.Value;
                pGeo.Value = (object?)r.Modifiers.InferenceGeoText ?? DBNull.Value;
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
                       cache_write_5m_tokens, cache_write_1h_tokens,
                       cache_read_tokens, output_tokens,
                       speed, inference_geo
                FROM ingested_requests
                WHERE last_timestamp >= $from AND last_timestamp < $to
                """;
            cmd.Parameters.AddWithValue("$from", fromUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$to", toUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));

            var buckets = new Dictionary<(DateOnly Date, string Model, UsageModifiers Modifiers), DailyRollup>();

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
                var modifiers = ReadModifiers(reader, 8, 9);
                var key = (LocalDays.DateOf(at, zone), model, modifiers);
                var running = buckets.GetValueOrDefault(key)
                    ?? new DailyRollup(key.Item1, model, TokenSplit.Empty, modifiers, 0);

                buckets[key] = running with
                {
                    Tokens = running.Tokens + ReadSplit(reader, 2),
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
            // Grouped by the modifiers as well as the model, for the reason DailyRollup gives:
            // two requests priced under different modifiers cannot share a bucket. NULL is a
            // group of its own in SQLite's GROUP BY, which is the behaviour wanted here.
            cmd.CommandText = """
                SELECT model,
                       SUM(input_tokens), SUM(cache_creation_tokens),
                       SUM(cache_write_5m_tokens), SUM(cache_write_1h_tokens),
                       SUM(cache_read_tokens), SUM(output_tokens), COUNT(*),
                       speed, inference_geo
                FROM ingested_requests
                WHERE last_timestamp >= $since
                GROUP BY model, speed, inference_geo
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
                    ReadSplit(reader, 1),
                    ReadModifiers(reader, 8, 9),
                    reader.GetInt64(7)));
            }
            return result;
        }
    }

    /// <summary>
    /// Six token counts from consecutive columns starting at <paramref name="offset"/>, in the
    /// order both queries above select them: input, cache-write total, 5m, 1h, cache read,
    /// output.
    ///
    /// <para><b>The unrecorded bucket is derived here rather than stored.</b> The flat total is
    /// the authority — it has been written by every build — and whatever it carries beyond the
    /// two TTL columns has no attribution, which is exactly one case at a fresh install and the
    /// whole of a pre-migration row. Deriving it means a NULL pair and a zero pair cannot
    /// disagree with the total, and a row an older build wrote needs no back-fill to be priced
    /// honestly (issue #255).</para>
    ///
    /// <para>NULL reads as zero, not as an error: a summed column is NULL when every row in the
    /// group predates it, which is the ordinary migration case.</para>
    /// </summary>
    private static TokenSplit ReadSplit(SqliteDataReader reader, int offset)
    {
        var total = Int64Or0(reader, offset + 1);
        var write5m = Int64Or0(reader, offset + 2);
        var write1h = Int64Or0(reader, offset + 3);

        return new TokenSplit(
            Int64Or0(reader, offset),
            write5m,
            write1h,
            Math.Max(0, total - write5m - write1h),
            Int64Or0(reader, offset + 4),
            Int64Or0(reader, offset + 5));
    }

    /// <summary>
    /// The stored pricing modifiers. Read back through <see cref="UsageModifiers.From"/>, the
    /// same function that classifies a transcript's own values, so a stored token and a
    /// transcript token cannot come to mean different things.
    /// </summary>
    private static UsageModifiers ReadModifiers(SqliteDataReader reader, int speed, int geo) =>
        UsageModifiers.From(
            reader.IsDBNull(speed) ? null : reader.GetString(speed),
            reader.IsDBNull(geo) ? null : reader.GetString(geo));

    private static long Int64Or0(SqliteDataReader reader, int column) =>
        reader.IsDBNull(column) ? 0 : reader.GetInt64(column);

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
