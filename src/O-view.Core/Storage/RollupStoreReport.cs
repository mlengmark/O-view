using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace OView.Core.Storage;

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
    int FilesGone)
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
        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"    ledger      : {LedgerRows:N0} row(s), {FirstDay ?? "-"} .. {LastDay ?? "-"}, newest {NewestTimestamp ?? "none"}"));
        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"    transcripts : {TrackedFiles} tracked, {FilesBehind} behind by {UnreadBytes:N0} bytes, {FilesGone} gone"));

        return text.ToString();
    }

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
    internal static RollupStoreReport Read(SqliteConnection connection, string path, string origin)
    {
        var (rows, firstDay, lastDay, newest) = ReadLedger(connection);
        var (tracked, behind, unread, gone) = ReadOffsets(connection);

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
            gone);
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
