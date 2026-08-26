using System.Globalization;

namespace OView.Core.Storage;

/// <summary>What the guard found, and what it did about it.</summary>
public enum StaleJournalVerdict
{
    /// <summary>No journal beside the database — the ordinary state after a clean shutdown.</summary>
    NoJournal,

    /// <summary>A journal young enough to be a genuine continuation of its database.</summary>
    Current,

    /// <summary>
    /// Something else holds the store open, so its timestamps cannot be trusted and nothing
    /// was touched. Never a finding about the journal — only about what could be established.
    /// </summary>
    InUse,

    /// <summary>An orphan was moved aside; the database opens on its own content.</summary>
    Quarantined,

    /// <summary>An orphan was found and could not be moved. Named, never swallowed.</summary>
    QuarantineFailed,
}

/// <summary>
/// One startup check: what stood beside the database, and what the guard did.
/// </summary>
/// <param name="Lag">
/// How much older the journal is than its database — <c>db mtime − wal mtime</c>. Null when
/// there was no journal, or when nothing could be established about it.
/// </param>
/// <param name="Stamp">The quarantine suffix, when files were moved aside.</param>
/// <param name="Failure">Why a quarantine failed, when one did.</param>
public sealed record StaleJournalCheck(
    StaleJournalVerdict Verdict,
    TimeSpan? Lag,
    long WalBytes,
    string? Stamp = null,
    string? Failure = null)
{
    public static StaleJournalCheck None { get; } = new(StaleJournalVerdict.NoJournal, null, 0);

    /// <summary>Whether this is worth a log line and a place in the bundle.</summary>
    public bool IsNoteworthy => Verdict is StaleJournalVerdict.Quarantined
        or StaleJournalVerdict.QuarantineFailed
        or StaleJournalVerdict.InUse;

    /// <summary>One line of facts, for the log and the diagnostics bundle.</summary>
    public string Describe() => Verdict switch
    {
        StaleJournalVerdict.NoJournal => "none",
        StaleJournalVerdict.Current => string.Create(CultureInfo.InvariantCulture,
            $"current ({WalBytes:N0} bytes, {Age()} behind the database)"),
        StaleJournalVerdict.InUse => string.Create(CultureInfo.InvariantCulture,
            $"NOT CHECKED — the store was open elsewhere ({WalBytes:N0} bytes)"),
        StaleJournalVerdict.Quarantined => string.Create(CultureInfo.InvariantCulture,
            $"QUARANTINED as {Stamp} — {WalBytes:N0} bytes, {Age()} older than the database"),
        StaleJournalVerdict.QuarantineFailed => string.Create(CultureInfo.InvariantCulture,
            $"ORPHAN LEFT IN PLACE — {Age()} older than the database, could not move it: {Failure}"),
        _ => Verdict.ToString(),
    };

    private string Age() => Lag is { } lag
        ? string.Create(CultureInfo.InvariantCulture, $"{lag.TotalHours:0.0}h")
        : "age unknown";
}

/// <summary>
/// Refuses to open the rollup store behind a journal that cannot belong to it
/// (<a href="https://github.com/mlengmark/O-view/issues/213">issue #213</a>).
///
/// <para><b>The failure this prevents.</b> SQLite recovers from a <c>-wal</c> on open and
/// treats its frames as the newest version of the pages they cover, overriding newer content
/// in the main file. That is correct when the journal is a genuine continuation, which is the
/// normal case. When the journal is an <i>orphan</i>, it presents the store as it stood when
/// that file was written — and the recovered state then becomes the truth. Measured on the
/// development machine: the same database read twice, differing only in whether a stale
/// journal sat beside it, gave 6,917 rows and 5,072 rows. Five days of history, and
/// <c>PRAGMA quick_check</c> returned <c>ok</c> both times.</para>
///
/// <para>That makes it two failures at once, and the second is the serious one: confidently
/// wrong numbers with every diagnostic reporting health (rule 6), and permanent loss of rows
/// that cannot be rebuilt for transcripts Claude Code has since deleted.</para>
///
/// <para><b>Why the age is compared against the database rather than against now.</b> A
/// machine switched off for a week has an old journal and an old database, and nothing is
/// wrong with either. What cannot happen is a journal materially <i>older</i> than the file it
/// is supposed to be continuing: SQLite writes the journal on every commit and the database
/// only on checkpoint, so a live journal is the newer of the two almost all the time.</para>
///
/// <para><b>The timestamps are only read while both files are held exclusively</b>, and that
/// is not caution for its own sake. Windows does not update a file's directory entry while a
/// handle is open, so a journal being written <i>right now</i> by another process can carry a
/// last-write time from minutes ago. Reading the mtimes of a store somebody else has open and
/// concluding "not being written" is exactly the inference that data is wrong. Holding both
/// files with <see cref="FileShare.None"/> establishes that nobody has them open, which is
/// what makes the comparison meaningful — and when the handles cannot be taken, the guard says
/// so and touches nothing.</para>
///
/// <para><b>On Unix that probe is weaker</b> and is not what carries the guarantee there.
/// .NET emulates <see cref="FileShare"/> with <c>flock</c>, while SQLite locks with
/// <c>fcntl</c> byte ranges — different mechanisms, so an open SQLite connection does not
/// necessarily fail the probe. What protects both platforms is that this runs at startup,
/// behind the single-instance guard, when no other copy of O-view is meant to exist.</para>
///
/// <para><b>Only the journal is moved aside, never the database.</b> That is a deliberate
/// departure from the corruption path this borrows its naming from, which quarantines the
/// whole set and rebuilds empty. Here the database is the <i>truth</i> and the journal is the
/// liar — moving the database would discard the very history the guard exists to save.</para>
/// </summary>
public static class StaleJournal
{
    public const string WalSuffix = "-wal";
    public const string ShmSuffix = "-shm";

    /// <summary>
    /// How much older than its database a journal may be and still be believed.
    ///
    /// <para><b>Argued from the gap, not picked.</b> The upper bound on a legitimate lag is a
    /// write cadence: O-view commits on every poll, so while it is running the journal is
    /// never more than a minute behind, and a journal that outlives a clean shutdown does not
    /// exist — SQLite deletes it. One that survives a crash is contemporaneous with its
    /// database, not older. So on the app's own behaviour, minutes would do.</para>
    ///
    /// <para>The margin above that is for everything the app does not control: filesystem
    /// timestamp granularity, a clock stepped by NTP, and any tool that rewrites a file's
    /// modification time. Six hours is several hundred times the write cadence and a twentieth
    /// of the five-day gap actually observed, so it sits clear of both ends of the argument.
    /// The issue that filed this offered "a day" as the unambiguous case; this is tighter,
    /// because of what the two mistakes cost.</para>
    ///
    /// <para><b>The asymmetry is the point.</b> Acting wrongly costs a re-ingest — the store
    /// is a rebuildable cache (ADR-0006, rule 7) and the journal is quarantined rather than
    /// deleted, so the frames are still there. Failing to act costs days of history that
    /// cannot be rebuilt at all, silently, with every diagnostic saying the store is
    /// healthy.</para>
    /// </summary>
    public static readonly TimeSpan MaxLag = TimeSpan.FromHours(6);

    /// <summary>
    /// Checks the journal beside <paramref name="dbPath"/> and quarantines it if it cannot
    /// belong there. Call <b>before</b> opening the database — once SQLite has recovered from
    /// an orphan, the damage is done and this can no longer see it.
    ///
    /// <para>Never throws. This runs on the path that must always leave a usable store behind
    /// (rule 7 / issue #16), so every failure downgrades to a named verdict rather than
    /// stopping a launch.</para>
    /// </summary>
    public static StaleJournalCheck Guard(string dbPath, TimeSpan? maxLag = null)
    {
        var wal = dbPath + WalSuffix;

        try
        {
            // A journal with no database is not an orphan overriding anything — there is
            // nothing for it to roll back. Left alone.
            if (!File.Exists(wal) || !File.Exists(dbPath))
            {
                return StaleJournalCheck.None;
            }

            if (!TryReadWhileExclusive(dbPath, wal, out var lag, out var walBytes))
            {
                return new StaleJournalCheck(StaleJournalVerdict.InUse, null, SizeOf(wal));
            }

            if (lag <= (maxLag ?? MaxLag))
            {
                return new StaleJournalCheck(StaleJournalVerdict.Current, lag, walBytes);
            }

            // Both handles are closed by now — TryReadWhileExclusive owns and releases them,
            // and a move cannot run while they are open.
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            return MoveAside(dbPath, stamp) is { } failure
                ? new StaleJournalCheck(StaleJournalVerdict.QuarantineFailed, lag, walBytes, stamp, failure)
                : new StaleJournalCheck(StaleJournalVerdict.Quarantined, lag, walBytes, stamp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new StaleJournalCheck(
                StaleJournalVerdict.InUse, null, 0, Failure: $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// How far the journal beside <paramref name="dbPath"/> trails its database, for a reader
    /// that only wants to report the fact. No probe and no action — the number alone, which is
    /// what the store section of the diagnostics bundle carries.
    /// </summary>
    public static TimeSpan? Lag(string dbPath)
    {
        try
        {
            var wal = dbPath + WalSuffix;
            return File.Exists(wal) && File.Exists(dbPath)
                ? File.GetLastWriteTimeUtc(dbPath) - File.GetLastWriteTimeUtc(wal)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads both timestamps while holding both files exclusively, so no lazily-updated
    /// directory entry can be in flight behind them. False when either handle cannot be
    /// taken — which is a statement about the probe, not about the journal.
    /// </summary>
    private static bool TryReadWhileExclusive(string dbPath, string wal, out TimeSpan lag, out long walBytes)
    {
        lag = default;
        walBytes = 0;

        try
        {
            using var dbHandle = File.Open(dbPath, FileMode.Open, FileAccess.Read, FileShare.None);
            using var walHandle = File.Open(wal, FileMode.Open, FileAccess.Read, FileShare.None);

            walBytes = walHandle.Length;
            lag = File.GetLastWriteTimeUtc(dbPath) - File.GetLastWriteTimeUtc(wal);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Moves the journal and its shared-memory index aside under the quarantine convention,
    /// returning null on success or a description of what stopped it.
    ///
    /// <para>The pair moves together: <c>-shm</c> is an index over the <c>-wal</c>, and
    /// leaving one without the other is a state SQLite should never be handed.</para>
    ///
    /// <para><b>Unlike the corruption path, a failure here is not shrugged off.</b> There it
    /// can fall back to deleting, because the file being moved is already malformed. This one
    /// may hold committed frames, so it is never deleted — if it cannot be moved it stays
    /// where it is and the verdict says so, which at least means the wrong number arrives with
    /// an explanation beside it.</para>
    /// </summary>
    private static string? MoveAside(string dbPath, string stamp)
    {
        string? failure = null;

        foreach (var suffix in new[] { WalSuffix, ShmSuffix })
        {
            var file = dbPath + suffix;
            if (!File.Exists(file))
            {
                continue;
            }

            try
            {
                File.Move(file, $"{file}{CorruptBackups.Marker}{stamp}", overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failure ??= $"{Path.GetFileName(file)}: {ex.GetType().Name}";
            }
        }

        // Bounded like every other quarantine (issue #160). A generation left by this guard
        // holds a -wal and -shm with no .db beside them, which is how a reader tells the two
        // kinds of event apart in a listing.
        CorruptBackups.Prune(dbPath);

        return failure;
    }

    private static long SizeOf(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
