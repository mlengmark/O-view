using OView.Core.Pricing;
using OView.Core.Storage;

namespace OView.Core.Tests;

/// <summary>
/// Retention for the databases the rollup store quarantines (issue #160).
///
/// <para>The corruption handling itself is not what these are about — that behaved correctly
/// every time it fired, and <see cref="RollupStoreTests.CorruptDatabase_IsBackedUpAndRebuilt"/>
/// owns it. What was wrong is that the files it left behind had no upper bound and no reader:
/// seven generations and ~6 MB on one machine over a month, retained "so the corruption can
/// still be examined" while nothing surfaced them.</para>
///
/// <para>So two things are pinned here, and the second is what makes the first safe: which
/// generations survive, and that a prune can never be the reason a self-heal fails.</para>
/// </summary>
public class CorruptBackupRetentionTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "oview-corrupt-tests", Guid.NewGuid().ToString("N"));

    private string Db => Path.Combine(_dir, "usage.db");

    public CorruptBackupRetentionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    /// <summary>Seeds a complete generation — the DB and both sidecars, all on one stamp.</summary>
    private void SeedGeneration(string stamp, int bytesEach = 64)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            File.WriteAllBytes($"{Db}{suffix}{CorruptBackups.Marker}{stamp}", new byte[bytesEach]);
        }
    }

    private string[] Quarantined() => Directory.GetFiles(_dir, $"*{CorruptBackups.Marker}*");

    // ── what survives ───────────────────────────────────────────────────────────────

    [Fact]
    public void OnlyTheNewestGenerationsSurviveAPrune()
    {
        SeedGeneration("20260601-090000");
        SeedGeneration("20260702-101500");
        SeedGeneration("20260803-113000");
        SeedGeneration("20260804-120000");

        Assert.Equal(2, CorruptBackups.Prune(Db));

        var surviving = CorruptBackups.Find(Db);
        Assert.Equal(["20260804-120000", "20260803-113000"], surviving.Select(g => g.Stamp));
    }

    /// <summary>
    /// The reason grouping exists at all. A database separated from its WAL is not the state
    /// that was quarantined, so a surviving generation has to be complete — dropping a sidecar
    /// while keeping its DB would leave something that looks like evidence and is not.
    /// </summary>
    [Fact]
    public void ASurvivingGenerationKeepsAllThreeOfItsFiles()
    {
        SeedGeneration("20260601-090000");
        SeedGeneration("20260803-113000");
        SeedGeneration("20260804-120000");

        CorruptBackups.Prune(Db);

        foreach (var generation in CorruptBackups.Find(Db))
        {
            Assert.Equal(3, generation.Files.Count);
            Assert.All(generation.Files, f => Assert.True(File.Exists(f)));
            Assert.All(generation.Files, f => Assert.EndsWith(generation.Stamp, f, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// A dropped generation goes whole, for the same reason: leaving the <c>-shm</c> of an
    /// otherwise deleted set behind is residue with none of the value.
    /// </summary>
    [Fact]
    public void ADroppedGenerationLeavesNothingBehind()
    {
        SeedGeneration("20260601-090000");
        SeedGeneration("20260803-113000");
        SeedGeneration("20260804-120000");

        CorruptBackups.Prune(Db);

        Assert.DoesNotContain(Quarantined(), f => f.EndsWith("20260601-090000", StringComparison.Ordinal));
        Assert.Equal(6, Quarantined().Length);   // two complete generations
    }

    /// <summary>
    /// A generation is however many files carried that stamp, which is not always three.
    /// <c>BackUpCorruptFiles</c> moves only the files that exist, and a store checkpointed
    /// before it corrupted has no <c>-wal</c> or <c>-shm</c> to move — three of the seven
    /// generations on the machine this issue was measured from are a lone <c>.db</c>.
    /// Grouping by stamp has to treat those as whole generations rather than as fragments,
    /// or the retention count means something different depending on how the store happened
    /// to die.
    /// </summary>
    [Fact]
    public void AGenerationWithNoSidecarsCountsAsOne()
    {
        File.WriteAllBytes($"{Db}{CorruptBackups.Marker}20260725-104046", new byte[200_704]);
        SeedGeneration("20260803-113000");
        SeedGeneration("20260804-120000");

        Assert.Equal(3, CorruptBackups.Find(Db).Count);
        Assert.Single(CorruptBackups.Find(Db).Single(g => g.Stamp == "20260725-104046").Files);

        Assert.Equal(1, CorruptBackups.Prune(Db));
        Assert.False(File.Exists($"{Db}{CorruptBackups.Marker}20260725-104046"));
    }

    [Fact]
    public void FewerGenerationsThanTheLimitAreAllKept()
    {
        SeedGeneration("20260803-113000");
        SeedGeneration("20260804-120000");

        Assert.Equal(0, CorruptBackups.Prune(Db));
        Assert.Equal(6, Quarantined().Length);
    }

    [Fact]
    public void AnEmptyDirectoryPrunesNothingAndDoesNotThrow()
    {
        Assert.Equal(0, CorruptBackups.Prune(Db));
        Assert.Equal(0, CorruptBackups.Prune(Path.Combine(_dir, "never-existed", "usage.db")));
    }

    /// <summary>
    /// The live database and its working sidecars are not backups and must survive a prune —
    /// they carry no stamp, and confusing the two would delete the store the app is using.
    /// </summary>
    [Fact]
    public void TheLiveDatabaseIsNeverTouched()
    {
        File.WriteAllBytes(Db, new byte[128]);
        File.WriteAllBytes(Db + "-wal", new byte[128]);
        SeedGeneration("20260601-090000");
        SeedGeneration("20260702-101500");
        SeedGeneration("20260803-113000");

        CorruptBackups.Prune(Db);

        Assert.True(File.Exists(Db));
        Assert.True(File.Exists(Db + "-wal"));
        Assert.DoesNotContain(CorruptBackups.Find(Db), g => g.Stamp == "20260601-090000");
    }

    // ── never fatal ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// A file that will not delete is skipped, the rest of the sweep continues, and nothing
    /// propagates to the caller. This is the guarantee rule 7 / issue #16 already made of the
    /// rebuild path — housekeeping is not allowed to turn a self-heal back into the fatal
    /// state it exists to escape.
    /// </summary>
    [Fact]
    public void AFileThatCannotBeDeletedDoesNotStopThePrune()
    {
        SeedGeneration("20260601-090000");
        SeedGeneration("20260702-101500");
        SeedGeneration("20260803-113000");
        SeedGeneration("20260804-120000");

        var wedged = $"{Db}-wal{CorruptBackups.Marker}20260601-090000";

        var removed = CorruptBackups.Prune(Db, delete: path =>
        {
            if (path == wedged)
            {
                throw new IOException("The process cannot access the file because it is being used by another process.");
            }
            File.Delete(path);
        });

        // The 20260702 generation went whole; 20260601 is reported as not fully removed.
        Assert.Equal(1, removed);
        Assert.True(File.Exists(wedged));
        Assert.False(File.Exists($"{Db}{CorruptBackups.Marker}20260601-090000"));
        Assert.Empty(Directory.GetFiles(_dir, $"*{CorruptBackups.Marker}20260702-101500"));
    }

    /// <summary>
    /// The same guarantee against a real lock rather than a stand-in, and across the whole
    /// rebuild rather than the prune alone: whatever the prune could not remove, a usable
    /// empty database still results.
    ///
    /// <para>Only Windows holds an open file against deletion. On Linux the unlink succeeds,
    /// which is not a failure of anything — so the assertion that has to hold on both is that
    /// the store came back, and the file surviving is asserted only where the OS makes it
    /// survive.</para>
    /// </summary>
    [Fact]
    public void ARebuildStillYieldsAUsableStoreWhenAQuarantinedFileIsLocked()
    {
        SeedGeneration("20260601-090000");
        SeedGeneration("20260702-101500");
        SeedGeneration("20260803-113000");

        var held = $"{Db}{CorruptBackups.Marker}20260601-090000";
        var corrupt = WriteMalformedDatabase();

        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
        using (var store = new RollupStore(corrupt))
        {
            store.Ingest([new Providers.Jsonl.TranscriptRecord(
                "r1", DateTimeOffset.Parse("2026-08-20T10:00:00Z"), "claude-opus-5",
                new TokenSplit(1, 0, 0, 0, 0, 1), UsageModifiers.Standard)]);
            Assert.Single(store.GetDailyRollups(
                DateTimeOffset.Parse("2026-08-20T00:00:00Z"),
                DateTimeOffset.Parse("2026-08-21T00:00:00Z"),
                TimeZoneInfo.Utc));

            Assert.Equal(OperatingSystem.IsWindows(), File.Exists(held));
        }
    }

    /// <summary>
    /// The prune is wired into the quarantine itself, so it runs at the one moment the
    /// directory is known to have grown. Without that it would need a caller nobody would
    /// remember to add.
    /// </summary>
    [Fact]
    public void QuarantiningANewCorruptionPrunesTheOldOnes()
    {
        SeedGeneration("20260601-090000");
        SeedGeneration("20260702-101500");
        SeedGeneration("20260803-113000");

        var corrupt = WriteMalformedDatabase();
        using (new RollupStore(corrupt)) { }

        var generations = CorruptBackups.Find(corrupt);
        Assert.Equal(CorruptBackups.KeepGenerations, generations.Count);

        // The one just quarantined is the newest, and the oldest seeded ones are gone.
        Assert.DoesNotContain(generations, g => g.Stamp == "20260601-090000");
        Assert.DoesNotContain(generations, g => g.Stamp == "20260702-101500");
    }

    // ── the report the bundle prints ────────────────────────────────────────────────

    [Fact]
    public void InspectReportsCountNewestStampAndSize()
    {
        SeedGeneration("20260803-113000", bytesEach: 1024 * 1024);
        SeedGeneration("20260804-120000", bytesEach: 1024 * 1024);

        var report = CorruptBackups.Inspect(Db);

        Assert.Equal(2, report.Generations);
        Assert.Equal("20260804-120000", report.NewestStamp);
        Assert.Equal(6 * 1024 * 1024, report.Bytes);
    }

    [Fact]
    public void InspectOnACleanMachineReportsNothingRatherThanFailing()
    {
        var report = CorruptBackups.Inspect(Db);

        Assert.Equal(0, report.Generations);
        Assert.Null(report.NewestStamp);
        Assert.Equal(0, report.Bytes);
    }

    /// <summary>
    /// Valid SQLite magic and page size followed by garbage — a malformed file rather than
    /// merely "not a database", which is what real on-disk corruption looks like. Same shape
    /// <see cref="RollupStoreTests"/> uses.
    /// </summary>
    private string WriteMalformedDatabase()
    {
        var bytes = new byte[4096];
        System.Text.Encoding.ASCII.GetBytes("SQLite format 3\0").CopyTo(bytes, 0);
        bytes[16] = 0x10; bytes[17] = 0x00;
        for (var i = 100; i < bytes.Length; i++) bytes[i] = 0xEE;
        File.WriteAllBytes(Db, bytes);
        return Db;
    }
}
