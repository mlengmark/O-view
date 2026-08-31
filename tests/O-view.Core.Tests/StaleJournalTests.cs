using OView.Core.Pricing;
using Microsoft.Data.Sqlite;
using OView.Core.Providers.Jsonl;
using OView.Core.Storage;

namespace OView.Core.Tests;

/// <summary>
/// The orphaned-journal guard (issue #213).
///
/// <para>The hazard these pin is not an exception anywhere — it is SQLite recovering from a
/// <c>-wal</c> that does not belong to the database beside it and presenting the result as the
/// truth, with <c>quick_check</c> returning <c>ok</c> throughout. So the assertions are about
/// row counts and about which files exist, never about anything being thrown.</para>
/// </summary>
public class StaleJournalTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("oview-tests-").FullName;

    private string Db => Path.Combine(_dir, "usage.db");
    private string Wal => Db + StaleJournal.WalSuffix;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static TranscriptRecord Record(string id) =>
        new(id, DateTimeOffset.Parse("2026-08-20T10:00:00Z"), "claude-opus-4-8",
            new TokenSplit(1, 0, 0, 0, 0, 1), UsageModifiers.Standard);

    /// <summary>Ingests <paramref name="count"/> rows under <paramref name="prefix"/> and closes.</summary>
    private void Fill(string prefix, int count)
    {
        using var store = new RollupStore(Db);
        store.Ingest(Enumerable.Range(0, count).Select(i => Record($"{prefix}-{i}")));
    }

    private long RowsViaFreshConnection()
    {
        using var connection = new SqliteConnection($"Data Source={Db};Pooling=False");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM ingested_requests;";
        return (long)cmd.ExecuteScalar()!;
    }

    private static void Backdate(string file, TimeSpan by) =>
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow - by);

    // ── the guard itself ────────────────────────────────────────────────────────────

    [Fact]
    public void NoJournal_IsTheOrdinaryCase_AndNothingIsTouched()
    {
        Fill("a", 3);   // a clean close leaves no -wal behind

        var check = StaleJournal.Guard(Db);

        Assert.Equal(StaleJournalVerdict.NoJournal, check.Verdict);
        Assert.True(File.Exists(Db));
    }

    [Fact]
    public void AJournalYoungerThanTheThreshold_IsLeftAlone()
    {
        Fill("a", 3);
        File.WriteAllBytes(Wal, new byte[64]);
        // Newer than the database, which is what a live journal always is.
        Backdate(Db, TimeSpan.FromHours(1));

        var check = StaleJournal.Guard(Db);

        Assert.Equal(StaleJournalVerdict.Current, check.Verdict);
        Assert.True(File.Exists(Wal));
        Assert.True(check.Lag < TimeSpan.Zero);
    }

    /// <summary>
    /// The reported shape: a journal five days older than the database it sits beside. A live
    /// one is seconds old and almost always the newer of the two, so this cannot be a
    /// continuation of anything.
    /// </summary>
    [Fact]
    public void AJournalMuchOlderThanItsDatabase_IsQuarantined()
    {
        Fill("a", 3);
        File.WriteAllBytes(Wal, new byte[2048]);
        Backdate(Wal, TimeSpan.FromDays(5));

        var check = StaleJournal.Guard(Db);

        Assert.Equal(StaleJournalVerdict.Quarantined, check.Verdict);
        Assert.False(File.Exists(Wal));
        Assert.Equal(2048, check.WalBytes);
        Assert.True(check.Lag > TimeSpan.FromDays(4));
    }

    /// <summary>
    /// <b>Quarantined, never deleted.</b> The journal may hold committed frames, so the guard
    /// acting wrongly has to be recoverable — and it lands under the same convention the
    /// corruption path uses, so the retention that bounds one bounds the other (issue #160).
    /// </summary>
    [Fact]
    public void TheQuarantinedJournalIsKeptAndDiscoverable()
    {
        Fill("a", 3);
        File.WriteAllBytes(Wal, new byte[2048]);
        Backdate(Wal, TimeSpan.FromDays(5));

        var check = StaleJournal.Guard(Db);

        var generation = Assert.Single(CorruptBackups.Find(Db));
        Assert.Equal(check.Stamp, generation.Stamp);
        Assert.All(generation.Files, f => Assert.True(File.Exists(f)));
        Assert.Equal(2048, generation.Bytes);
    }

    /// <summary>
    /// <b>The database is never moved.</b> This is the deliberate departure from the
    /// corruption path, which quarantines the whole set and rebuilds empty: here the database
    /// is the truth and the journal is the liar, so moving the database would discard the
    /// history the guard exists to save.
    /// </summary>
    [Fact]
    public void TheDatabaseItselfIsNeverQuarantined()
    {
        Fill("a", 7);
        var before = RowsViaFreshConnection();
        File.WriteAllBytes(Wal, new byte[2048]);
        Backdate(Wal, TimeSpan.FromDays(5));

        StaleJournal.Guard(Db);

        Assert.True(File.Exists(Db));
        Assert.Equal(before, RowsViaFreshConnection());
        Assert.DoesNotContain(
            CorruptBackups.Find(Db).SelectMany(g => g.Files),
            f => Path.GetFileName(f).StartsWith("usage.db.", StringComparison.Ordinal));
    }

    /// <summary>
    /// The shared-memory index goes with its journal. It is an index over the <c>-wal</c>, and
    /// one without the other is a state SQLite should never be handed.
    /// </summary>
    [Fact]
    public void TheShmGoesWithTheJournal()
    {
        Fill("a", 3);
        File.WriteAllBytes(Wal, new byte[2048]);
        File.WriteAllBytes(Db + StaleJournal.ShmSuffix, new byte[512]);
        Backdate(Wal, TimeSpan.FromDays(5));

        StaleJournal.Guard(Db);

        Assert.False(File.Exists(Wal));
        Assert.False(File.Exists(Db + StaleJournal.ShmSuffix));
        Assert.Equal(2, Assert.Single(CorruptBackups.Find(Db)).Files.Count);
    }

    /// <summary>
    /// <b>The trap in the obvious implementation.</b> Windows does not update a file's
    /// directory entry while a handle is open, so a journal being written right now can carry
    /// a last-write time from minutes ago — and quarantining a live journal is the data loss
    /// this guard exists to prevent, caused by the guard.
    ///
    /// <para>So the timestamps are only read while both files are held exclusively. When they
    /// cannot be, the verdict says nothing was established rather than reporting the journal
    /// as fine — an unmade check must not read as a clean one.</para>
    /// </summary>
    [Fact]
    public void AStoreHeldOpenElsewhere_IsNotTouchedAndNotDeclaredHealthy()
    {
        Fill("a", 3);
        File.WriteAllBytes(Wal, new byte[2048]);
        Backdate(Wal, TimeSpan.FromDays(5));   // would be quarantined, were it checkable

        using (File.Open(Wal, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var check = StaleJournal.Guard(Db);

            Assert.Equal(StaleJournalVerdict.InUse, check.Verdict);
            Assert.Null(check.Lag);
            Assert.True(File.Exists(Wal));
            Assert.Empty(CorruptBackups.Find(Db));
        }
    }

    [Fact]
    public void AJournalWithNoDatabase_IsNotAnOrphanToActOn()
    {
        File.WriteAllBytes(Wal, new byte[2048]);
        Backdate(Wal, TimeSpan.FromDays(5));

        var check = StaleJournal.Guard(Db);

        Assert.Equal(StaleJournalVerdict.NoJournal, check.Verdict);
        Assert.True(File.Exists(Wal));
    }

    /// <summary>The threshold is a parameter so the argument for its value can be re-run, not asserted once.</summary>
    [Fact]
    public void TheThresholdIsWhatDecides()
    {
        Fill("a", 3);
        File.WriteAllBytes(Wal, new byte[64]);
        Backdate(Wal, TimeSpan.FromHours(3));

        Assert.Equal(
            StaleJournalVerdict.Current,
            StaleJournal.Guard(Db, TimeSpan.FromHours(6)).Verdict);

        Assert.Equal(
            StaleJournalVerdict.Quarantined,
            StaleJournal.Guard(Db, TimeSpan.FromHours(1)).Verdict);
    }

    // ── through the store, which is where it has to hold ────────────────────────────

    /// <summary>
    /// End to end: a stale journal beside a good database, opened through
    /// <see cref="RollupStore"/>. Every row survives and the store says what it did.
    /// </summary>
    [Fact]
    public void TheStoreOpensOnItsOwnContent_AndReportsTheQuarantine()
    {
        Fill("kept", 12);
        var expected = RowsViaFreshConnection();

        File.WriteAllBytes(Wal, new byte[2048]);
        Backdate(Wal, TimeSpan.FromDays(5));

        using var store = new RollupStore(Db);

        Assert.Equal(StaleJournalVerdict.Quarantined, store.JournalGuard.Verdict);
        Assert.True(store.JournalGuard.IsNoteworthy);
        Assert.Equal(expected, store.Inspect().LedgerRows);
        Assert.Equal(12, store.Inspect().LedgerRows);
    }

    /// <summary>
    /// The guard runs <b>before</b> the connection, and that ordering is the whole defence:
    /// SQLite folds an orphan in as it opens the file, so a check made afterwards is asking
    /// about a store that has already been rolled back.
    ///
    /// <para>Asserted through the outcome rather than by inspecting call order — the journal
    /// is gone by the time the store is usable, which can only be true if the guard ran
    /// first.</para>
    /// </summary>
    [Fact]
    public void TheGuardRunsBeforeTheDatabaseIsOpened()
    {
        Fill("a", 4);
        File.WriteAllBytes(Wal, new byte[2048]);
        Backdate(Wal, TimeSpan.FromDays(5));

        using var store = new RollupStore(Db);

        Assert.False(File.Exists(Wal + CorruptBackups.Marker));
        Assert.Single(CorruptBackups.Find(Db));
        Assert.Equal(4, store.Inspect().LedgerRows);
    }

    /// <summary>
    /// A store that opens normally must not look like one that was repaired. The guard's
    /// ordinary verdict is silence — nothing quarantined, nothing logged, nothing in the
    /// bundle to chase.
    /// </summary>
    [Fact]
    public void AnOrdinaryOpenIsNotNoteworthy()
    {
        Fill("a", 3);

        using var store = new RollupStore(Db);

        Assert.Equal(StaleJournalVerdict.NoJournal, store.JournalGuard.Verdict);
        Assert.False(store.JournalGuard.IsNoteworthy);
        Assert.Empty(CorruptBackups.Find(Db));
    }

    // ── the hazard itself, reproduced ───────────────────────────────────────────────

    /// <summary>
    /// Captures a real journal mid-life: the actual <c>-wal</c> SQLite wrote for an earlier
    /// state of this database, frames, checksums and all.
    ///
    /// <para>Every other test here fabricates a journal, which is enough to exercise the
    /// guard's mechanics but proves nothing about the hazard — SQLite ignores a file with a
    /// bad header rather than recovering from it. This produces one it will genuinely
    /// replay.</para>
    /// </summary>
    private byte[] CaptureLiveJournal(int rows)
    {
        using var store = new RollupStore(Db);
        store.Ingest(Enumerable.Range(0, rows).Select(i => Record($"old-{i}")));

        // Read while the store still holds it: this is the journal as it stood at this
        // moment, which is precisely what an orphan is.
        using var handle = File.Open(Wal, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var bytes = new byte[handle.Length];
        handle.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>
    /// <b>The bug, demonstrated.</b> A genuine journal from an earlier state of this database,
    /// put back beside it after the database has moved on, and read through a plain SQLite
    /// connection with no guard in the way.
    ///
    /// <para>SQLite recovers from it and treats its frames as the newest version of the pages
    /// they cover, so the store presents itself as it stood when that file was written — the
    /// later rows simply are not there. Nothing reports a problem: <c>quick_check</c> returns
    /// <c>ok</c>, which is what made this six releases of misdiagnosis.</para>
    ///
    /// <para>This test asserts the <i>wrong</i> behaviour on purpose. It is the evidence that
    /// the guard beside it is defending against something real rather than against a file of
    /// zeroes, and it fails loudly if a future SQLite ever stops doing this — at which point
    /// the guard's rationale would need rewriting rather than quietly standing on a premise
    /// that had expired.</para>
    /// </summary>
    [Fact]
    public void WithoutTheGuard_AGenuineOrphanRollsTheStoreBack_AndIntegrityStillReportsOk()
    {
        var orphan = CaptureLiveJournal(rows: 5);

        Fill("new", 20);                       // the database moves on and closes cleanly
        Assert.Equal(25, RowsViaFreshConnection());

        File.WriteAllBytes(Wal, orphan);       // the journal comes back
        Backdate(Wal, TimeSpan.FromDays(5));

        using var connection = new SqliteConnection($"Data Source={Db};Pooling=False");
        connection.Open();

        using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA quick_check;";
        Assert.Equal("ok", check.ExecuteScalar()!.ToString());

        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM ingested_requests;";
        Assert.Equal(5L, (long)count.ExecuteScalar()!);
    }

    /// <summary>
    /// The same orphan, through <see cref="RollupStore"/>. Every row survives, and the twenty
    /// that the connection above could not see are the rows this issue is about — on the
    /// machine it was measured on, 1,845 of them, unrebuildable for transcripts Claude Code
    /// had since deleted.
    /// </summary>
    [Fact]
    public void WithTheGuard_AGenuineOrphanCostsNothing()
    {
        var orphan = CaptureLiveJournal(rows: 5);

        Fill("new", 20);
        File.WriteAllBytes(Wal, orphan);
        Backdate(Wal, TimeSpan.FromDays(5));

        using var store = new RollupStore(Db);

        Assert.Equal(StaleJournalVerdict.Quarantined, store.JournalGuard.Verdict);
        Assert.Equal(25, store.Inspect().LedgerRows);
    }

    /// <summary>
    /// And the recovery stays available: the frames the guard set aside are still on disk, so
    /// a guard that acted wrongly is an inconvenience rather than the loss it prevents.
    /// </summary>
    [Fact]
    public void TheSetAsideJournalIsStillTheFileSqliteWrote()
    {
        var orphan = CaptureLiveJournal(rows: 5);

        Fill("new", 20);
        File.WriteAllBytes(Wal, orphan);
        Backdate(Wal, TimeSpan.FromDays(5));

        using var store = new RollupStore(Db);

        var quarantined = Assert.Single(
            CorruptBackups.Find(Db).SelectMany(g => g.Files),
            f => f.Contains(StaleJournal.WalSuffix, StringComparison.Ordinal));

        Assert.Equal(orphan, File.ReadAllBytes(quarantined));
    }

    // ── the report ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// The number that was missing while this went undiagnosed for six releases. It states its
    /// direction, because only one of the two directions is a problem and "6.2h" alone does
    /// not say which.
    /// </summary>
    [Fact]
    public void TheReportCarriesTheJournalsAgeAndItsDirection()
    {
        Fill("a", 3);
        File.WriteAllBytes(Wal, new byte[2048]);
        Backdate(Wal, TimeSpan.FromDays(5));

        var report = RollupStoreReport.Inspect(Db);

        Assert.NotNull(report.JournalLag);
        Assert.True(report.JournalLag > TimeSpan.FromDays(4));
        Assert.Contains("OLDER than the database", report.ToClipboardText(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A reader that ran no guard says so. Reporting "nothing was wrong" for a check nobody
    /// made is the failure mode this whole issue is an instance of.
    /// </summary>
    [Fact]
    public void AReaderThatRanNoGuardSaysSo()
    {
        Fill("a", 3);

        var text = RollupStoreReport.Inspect(Db).ToClipboardText();

        Assert.Contains("guard not run by this reader", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLiveInstanceReportsWhatItsOwnGuardDid()
    {
        Fill("a", 3);
        File.WriteAllBytes(Wal, new byte[2048]);
        Backdate(Wal, TimeSpan.FromDays(5));

        using var store = new RollupStore(Db);

        Assert.Contains("QUARANTINED", store.Inspect().ToClipboardText(), StringComparison.Ordinal);
    }
}
