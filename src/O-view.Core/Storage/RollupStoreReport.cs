using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using OView.Core.Providers.Jsonl;

namespace OView.Core.Storage;

/// <summary>
/// One surface's share of the ledger (issue #218).
/// </summary>
/// <param name="Source">A <see cref="TranscriptSources"/> label, or <c>unattributed</c>.</param>
/// <param name="Rows">Ledger rows carrying it.</param>
/// <param name="Tokens">Their four token fields summed — the figure the tiles are made of.</param>
public sealed record LedgerSource(string Source, long Rows, long Tokens, string? FirstDay, string? LastDay);

/// <summary>
/// One surface's watermarks: how many of its files the store believes it has consumed, and
/// what consuming them actually produced.
/// </summary>
/// <param name="Files">Watermarks carrying this source label.</param>
/// <param name="FullyRead">Those whose offset has reached the length recorded beside it.</param>
/// <param name="Records">Records ingested out of them, across the files this build could count.</param>
/// <param name="UnknownCoverage">
/// Watermarks inherited from a build that recorded no counting window. Their record counts can
/// never be more than "nothing since we started looking", so they are excluded from
/// <paramref name="Silent"/> rather than counted as evidence either way.
/// </param>
/// <param name="Silent">
/// Files this build read from byte 0 through to the end that yielded no ledger row at all.
///
/// <para>An observation, not a verdict: a transcript carrying no assistant record produces the
/// same shape legitimately. It earns its place because the alternative reading — a watermark
/// advanced over content that was never stored — is invisible in every other field, and on an
/// append-only transcript it is permanent, since nothing ever revisits a file that is "0
/// behind" (issue #218).</para>
///
/// <para><b>It cannot see the inherited case, and must not pretend to.</b> A file already
/// watermarked at EOF by an earlier build is never re-read, so this build writes nothing about
/// it and it is counted under <paramref name="UnknownCoverage"/> instead. That case is what
/// <see cref="IngestAuditReport"/> exists to answer.</para>
/// </param>
public sealed record WatermarkSource(
    string Source, int Files, int FullyRead, long Records, int UnknownCoverage, int Silent);

/// <summary>
/// What the rollup store actually holds, and whether it can still be written.
///
/// <para><b>The bundle had nothing about the store at all</b>, which is what made a stalled
/// ingest undiagnosable: every other field describes an <i>input</i> — which paths exist, how
/// many samples they hold, how fresh they are — and all of them can read perfectly while the
/// token tiles have not moved in a week. Three support reports in a row led with
/// <c>status : Ok</c> while exactly that was happening.</para>
///
/// <para><b><see cref="Origin"/> is the field this exists for.</b> The same bundle can be
/// produced two ways: by the running tray, from the connection it has held open all along, or
/// by <c>--diagnose</c>, which opens its own. Those two should agree. On the machine that
/// prompted this they demonstrably did not — the tray reported six weekly observations and no
/// quarantined stores while the same directory held five and fifteen — so the report says
/// which reader produced it, and a mismatch between the two becomes evidence rather than a
/// confusion.</para>
///
/// <para><b>Writes are probed, not assumed.</b> <c>PRAGMA quick_check</c> is a read, so a
/// store that reads perfectly and fails every write passes it and then throws on every ingest
/// into a catch that used to say nothing. <c>BEGIN IMMEDIATE</c> takes the write lock and
/// rolls straight back, which exercises that path and leaves the database byte-identical.</para>
/// </summary>
/// <param name="Origin">Which reader produced this — the live instance, or a fresh connection.</param>
/// <param name="WritesAccepted">Whether a write lock could be taken. Null when it was not attempted.</param>
/// <param name="FilesBehind">
/// Tracked transcripts whose file on disk is longer than the length recorded beside their
/// offset. <b>This is the number that proves an ingest has stalled</b>, and it is the one no
/// other field can stand in for.
/// </param>
/// <param name="JournalLag">
/// How much older the <c>-wal</c> is than the database beside it (issue #213). Null when there
/// is no journal.
///
/// <para><b>A positive number here is the finding.</b> SQLite writes the journal on every
/// commit and the database only on checkpoint, so a live journal is the newer of the two — one
/// that trails its database by hours cannot be a continuation of it, and SQLite will still
/// recover from it and present the store as it stood when that file was written. This is the
/// field that makes that visible; on the machine this was measured on, everything else read
/// <c>ok</c>.</para>
/// </param>
/// <param name="JournalGuard">
/// What the startup guard did about it, or null when this reader did not run one — which is
/// every reader but the live instance, and is deliberately not reported as "nothing was
/// wrong".
/// </param>
public sealed record RollupStoreReport(
    string Path,
    string Origin,
    long FileBytes,
    long WalBytes,
    string JournalMode,
    string Integrity,
    bool? WritesAccepted,
    string? Failure,
    long LedgerRows,
    string? FirstDay,
    string? LastDay,
    string? NewestTimestamp,
    int TrackedFiles,
    int FilesBehind,
    long UnreadBytes,
    int FilesGone,
    TimeSpan? JournalLag = null,
    StaleJournalCheck? JournalGuard = null,
    IReadOnlyList<LedgerSource>? LedgerBySource = null,
    IReadOnlyList<WatermarkSource>? WatermarksBySource = null,
    bool AttributionRecorded = true)
{
    /// <summary>Produced by the running app, from the connection it already holds.</summary>
    public const string LiveInstance = "live instance";

    /// <summary>Produced by a reader that opened the file for this report alone.</summary>
    public const string OpenedForReport = "opened for this report";

    /// <summary>When the store could not be inspected at all — named, never omitted.</summary>
    public static RollupStoreReport Unavailable(string path, string origin, string failure) =>
        new(path, origin, 0, 0, "?", "?", null, failure, 0, null, null, null, 0, 0, 0, 0);

    public string ToClipboardText()
    {
        var text = new StringBuilder();
        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"  rollup store  : {Path}"));
        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"    read by     : {Origin}"));

        if (Failure is { Length: > 0 } failure)
        {
            text.AppendLine($"    unreadable  : {failure}");
            return text.ToString();
        }

        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"    file        : {FileBytes:N0} bytes, wal {WalBytes:N0} bytes, journal {JournalMode}"));
        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"    health      : integrity {Integrity}, writes {(WritesAccepted switch
            {
                true => "accepted",
                false => "REFUSED",
                _ => "not probed",
            })}"));

        // Its own line rather than appended to `file`, because it is the one number on this
        // report that can be alarming while every other field reads ok (issue #213).
        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"    journal     : {DescribeJournalLag()}, guard {JournalGuard?.Describe() ?? "not run by this reader"}"));
        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"    ledger      : {LedgerRows:N0} row(s), {FirstDay ?? "-"} .. {LastDay ?? "-"}, newest {NewestTimestamp ?? "none"}"));

        AppendLedgerBySource(text);

        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"    transcripts : {TrackedFiles} tracked, {FilesBehind} behind by {UnreadBytes:N0} bytes, {FilesGone} gone"));

        AppendWatermarksBySource(text);

        return text.ToString();
    }

    /// <summary>
    /// Which surface the ledger is actually made of (issue #218).
    ///
    /// <para><b>This is the line that turns the row count above it into evidence.</b> A bundle
    /// could report 58 MB of Cowork transcripts a few lines earlier and 407 ledger rows here,
    /// and no reader — including the person who wrote both — could say whether those rows
    /// included any Cowork at all. The transcript section and this one are printed in the same
    /// words for the same reason: they are meant to be read against each other.</para>
    ///
    /// <para>A surface with no rows is printed rather than omitted, exactly as
    /// <see cref="Providers.Jsonl.TranscriptScopeReport.CoverageLine"/> names an absent one:
    /// "Cowork 0 row(s)" beside 36 Cowork files is the whole report, and it cannot be seen if
    /// the zero rows mean the line is skipped.</para>
    /// </summary>
    private void AppendLedgerBySource(StringBuilder text)
    {
        if (!AttributionRecorded)
        {
            // Distinguished from "every row is unattributed": that is a fact about the rows,
            // this is a fact about the schema, and only one of them means re-running later
            // would help.
            text.AppendLine("      by source : not recorded by the build that wrote this store");
            return;
        }

        foreach (var source in OrderedSources(LedgerBySource))
        {
            var row = LedgerBySource?.FirstOrDefault(s => s.Source == source);
            var span = row is { Rows: > 0 } ? $", {row.FirstDay} .. {row.LastDay}" : "";
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"      {source,-11}: {row?.Rows ?? 0:N0} row(s), {row?.Tokens ?? 0:N0} tokens{span}"));
        }
    }

    /// <summary>
    /// What the watermarks claim, per surface — and specifically how many files were read end
    /// to end and produced nothing.
    ///
    /// <para>"0 behind by 0 bytes" on the line above says every tracked file has been consumed.
    /// It has never said whether consuming them stored anything, and those are different
    /// machines: one is idle, the other has silently lost every token in an append-only file it
    /// will never re-read. The <c>!!</c> line is the second one, named outright rather than left
    /// to be inferred from two numbers on different lines.</para>
    /// </summary>
    private void AppendWatermarksBySource(StringBuilder text)
    {
        if (!AttributionRecorded)
        {
            return;
        }

        var silent = 0;

        foreach (var source in OrderedSources(WatermarksBySource))
        {
            var row = WatermarksBySource?.FirstOrDefault(s => s.Source == source);
            if (row is null)
            {
                text.AppendLine($"      {source,-11}: no watermarks");
                continue;
            }

            silent += row.Silent;
            var partial = row.UnknownCoverage > 0
                ? $", {row.UnknownCoverage} counted from mid-file"
                : "";
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"      {source,-11}: {row.Files} file(s), {row.FullyRead} fully read, {row.Records:N0} record(s) ingested{partial}"));
        }

        if (silent > 0)
        {
            // Stated as the observation, not as the diagnosis. A transcript that genuinely
            // carries no assistant record yet produces exactly this shape, and a line that
            // called it a stale watermark would be asserting a cause the store cannot see
            // (rule 6). What it is for is the case where the number is large and the files are
            // not new — which the transcript sizes a few lines above will say.
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"      !! {silent} file(s) read whole from byte 0 and produced no ledger row"));
        }
    }

    /// <summary>
    /// The real surfaces first and always, then whatever else the store holds — which is
    /// <c>unattributed</c> on any install that predates the source column, and would be a new
    /// label on any build that adds one. Nothing found in the database is dropped from the
    /// report on the grounds that this build did not expect it.
    /// </summary>
    private static IEnumerable<string> OrderedSources<T>(IReadOnlyList<T>? rows)
        where T : class =>
        TranscriptSources.All.Concat(
            (rows ?? [])
            .Select(r => r switch
            {
                LedgerSource l => l.Source,
                WatermarkSource w => w.Source,
                _ => "",
            })
            .Where(s => s.Length > 0 && !TranscriptSources.All.Contains(s))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal));

    /// <summary>
    /// The journal's age in words. States the direction, because "6.2h" alone does not say
    /// which of the two files is behind and only one of those is a problem.
    /// </summary>
    private string DescribeJournalLag() => JournalLag switch
    {
        null => "none",
        { TotalSeconds: <= 0 } lag => string.Create(CultureInfo.InvariantCulture,
            $"{-lag.TotalHours:0.0}h newer than the database"),
        { } lag => string.Create(CultureInfo.InvariantCulture,
            $"{lag.TotalHours:0.0}h OLDER than the database"),
    };

    /// <summary>
    /// Inspects a store file this process does not already have open. Used by
    /// <c>--diagnose</c>, which runs before the single-instance guard and therefore has no
    /// engine to ask.
    ///
    /// <para>Read-only apart from the write probe, which rolls back. Never throws: a bundle
    /// that fails is worse than one with a gap, and the user producing it is already reporting
    /// a problem.</para>
    /// </summary>
    public static RollupStoreReport Inspect(string? dbPath = null)
    {
        var path = dbPath ?? RollupStore.DefaultPath;

        try
        {
            if (!File.Exists(path))
            {
                return Unavailable(path, OpenedForReport, "no database file yet");
            }

            using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            connection.Open();
            return Read(connection, path, OpenedForReport);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            return Unavailable(path, OpenedForReport, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// The shared reader. Takes an open connection so the live instance and a fresh one
    /// produce byte-identical sections apart from <see cref="Origin"/> — which is the whole
    /// point of comparing them.
    /// </summary>
    internal static RollupStoreReport Read(
        SqliteConnection connection, string path, string origin, StaleJournalCheck? journalGuard = null)
    {
        var (rows, firstDay, lastDay, newest) = ReadLedger(connection);
        var (tracked, behind, unread, gone) = ReadOffsets(connection);

        // Probed rather than inferred from a failed query: a store written by an older build
        // genuinely has no such column, and that is a different report from one whose rows are
        // all unattributed. Only the first is fixed by waiting for the next poll.
        var attribution = HasColumn(connection, "ingested_requests", "source")
                          && HasColumn(connection, "file_offsets", "counted_from");

        return new RollupStoreReport(
            path,
            origin,
            SizeOf(path),
            SizeOf(path + "-wal"),
            Scalar(connection, "PRAGMA journal_mode;"),
            Scalar(connection, "PRAGMA quick_check;"),
            ProbeWrite(connection),
            null,
            rows,
            firstDay,
            lastDay,
            newest,
            tracked,
            behind,
            unread,
            gone,
            StaleJournal.Lag(path),
            journalGuard,
            attribution ? ReadLedgerBySource(connection) : [],
            attribution ? ReadWatermarksBySource(connection) : [],
            attribution);
    }

    /// <summary>Whether a column exists, without assuming a failed query means it does not.</summary>
    private static bool HasColumn(SqliteConnection connection, string table, string column)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info({table});";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    /// <summary>
    /// Rows and tokens per surface. Tokens are the four fields summed — the same arithmetic the
    /// tiles do, so the breakdown can be compared against what the user is looking at rather
    /// than against a figure only this report produces.
    /// </summary>
    private static IReadOnlyList<LedgerSource> ReadLedgerBySource(SqliteConnection connection)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT COALESCE(source, '{TranscriptSources.Unattributed}'),
                       COUNT(*),
                       SUM(input_tokens + cache_creation_tokens + cache_read_tokens + output_tokens),
                       MIN(utc_date),
                       MAX(utc_date)
                FROM ingested_requests
                GROUP BY 1
                """;

            var sources = new List<LedgerSource>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                sources.Add(new LedgerSource(
                    reader.GetString(0),
                    reader.GetInt64(1),
                    reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
            }

            return sources;
        }
        catch (SqliteException)
        {
            return [];
        }
    }

    /// <summary>
    /// Watermarks per surface, and how many of them are silent.
    ///
    /// <para>A file counts as silent only when all three hold: counting began at byte 0 (so the
    /// number covers the whole file), the offset has reached the recorded length (so there is
    /// nothing left to explain the absence), and the file was not empty. Anything short of that
    /// is reported as unknown coverage instead — a watermark inherited from an earlier build can
    /// only ever say "nothing since", which is not evidence of anything.</para>
    /// </summary>
    private static IReadOnlyList<WatermarkSource> ReadWatermarksBySource(SqliteConnection connection)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT COALESCE(source, '{TranscriptSources.Unattributed}'),
                       COUNT(*),
                       SUM(CASE WHEN byte_offset >= file_length AND file_length > 0 THEN 1 ELSE 0 END),
                       SUM(COALESCE(records, 0)),
                       SUM(CASE WHEN counted_from IS NULL THEN 1 ELSE 0 END),
                       SUM(CASE WHEN counted_from = 0
                                 AND COALESCE(records, 0) = 0
                                 AND file_length > 0
                                 AND byte_offset >= file_length
                                THEN 1 ELSE 0 END)
                FROM file_offsets
                GROUP BY 1
                """;

            var sources = new List<WatermarkSource>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                sources.Add(new WatermarkSource(
                    reader.GetString(0),
                    reader.GetInt32(1),
                    reader.GetInt32(2),
                    reader.GetInt64(3),
                    reader.GetInt32(4),
                    reader.GetInt32(5)));
            }

            return sources;
        }
        catch (SqliteException)
        {
            return [];
        }
    }

    /// <summary>
    /// Takes the write lock and rolls back. Nothing is written, and the one question
    /// <c>quick_check</c> cannot answer is answered.
    /// </summary>
    private static bool? ProbeWrite(SqliteConnection connection)
    {
        try
        {
            using var transaction = connection.BeginTransaction(deferred: false);
            transaction.Rollback();
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            // A transaction already in flight on this connection — the live instance mid-ingest.
            // Not a refusal, and not something to report as one.
            return null;
        }
    }

    private static (long Rows, string? First, string? Last, string? Newest) ReadLedger(SqliteConnection connection)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "SELECT COUNT(*), MIN(utc_date), MAX(utc_date), MAX(last_timestamp) FROM ingested_requests;";
            using var reader = cmd.ExecuteReader();
            return reader.Read()
                ? (reader.GetInt64(0),
                   reader.IsDBNull(1) ? null : reader.GetString(1),
                   reader.IsDBNull(2) ? null : reader.GetString(2),
                   reader.IsDBNull(3) ? null : reader.GetString(3))
                : (0, null, null, null);
        }
        catch (SqliteException)
        {
            return (0, null, null, null);
        }
    }

    /// <summary>
    /// Compares every recorded offset against the file it belongs to. A file longer than the
    /// length stored beside its offset has content the app believes it has accounted for and
    /// has not read — the signature of a stalled ingest, and invisible everywhere else.
    /// </summary>
    private static (int Tracked, int Behind, long Unread, int Gone) ReadOffsets(SqliteConnection connection)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT path, file_length FROM file_offsets;";

            var tracked = 0;
            var behind = 0;
            var gone = 0;
            long unread = 0;

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                tracked++;
                var recorded = reader.GetInt64(1);
                var actual = SizeOf(reader.GetString(0), missing: -1);

                if (actual < 0)
                {
                    gone++;
                }
                else if (actual > recorded)
                {
                    behind++;
                    unread += actual - recorded;
                }
            }

            return (tracked, behind, unread, gone);
        }
        catch (SqliteException)
        {
            return (0, 0, 0, 0);
        }
    }

    private static string Scalar(SqliteConnection connection, string sql)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            return cmd.ExecuteScalar()?.ToString() ?? "?";
        }
        catch (SqliteException ex)
        {
            return ex.SqliteErrorCode.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static long SizeOf(string path, long missing = 0)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : missing;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return missing;
        }
    }
}
