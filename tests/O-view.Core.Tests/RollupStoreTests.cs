using OView.Core.Providers.Jsonl;
using OView.Core.Storage;

namespace OView.Core.Tests;

public class RollupStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("oview-tests-").FullName;
    private readonly RollupStore _store;

    public RollupStoreTests()
    {
        _store = new RollupStore(Path.Combine(_dir, "usage.db"));
    }

    public void Dispose()
    {
        _store.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private static TranscriptRecord Record(string id, string date, string model, long output) =>
        new(id, DateTimeOffset.Parse(date + "T12:00:00Z"), model, 10, 100, 200, output);

    [Fact]
    public void Rollups_GroupByUtcDateAndModel()
    {
        _store.Ingest([
            Record("r1", "2026-07-20", "claude-opus-4-8", 100),
            Record("r2", "2026-07-20", "claude-opus-4-8", 50),
            Record("r3", "2026-07-20", "claude-sonnet-5", 30),
            Record("r4", "2026-07-21", "claude-opus-4-8", 70),
        ]);

        var rollups = _store.GetDailyRollups(new DateOnly(2026, 7, 20), new DateOnly(2026, 7, 21));

        Assert.Equal(3, rollups.Count);
        var day1Opus = rollups.Single(r => r.DateUtc == new DateOnly(2026, 7, 20) && r.Model == "claude-opus-4-8");
        Assert.Equal(150, day1Opus.OutputTokens);
        Assert.Equal(2, day1Opus.RequestCount);
    }

    [Fact]
    public void DateRange_IsInclusiveAndExcludesOutside()
    {
        _store.Ingest([
            Record("r1", "2026-07-19", "m", 1),
            Record("r2", "2026-07-20", "m", 2),
            Record("r3", "2026-07-21", "m", 4),
        ]);

        var rollups = _store.GetDailyRollups(new DateOnly(2026, 7, 20), new DateOnly(2026, 7, 20));

        Assert.Equal(2, rollups.Sum(r => r.OutputTokens));
    }

    [Fact]
    public void CoverageCount_ReportsDistinctRecordedDays()
    {
        _store.Ingest([
            Record("r1", "2026-07-18", "m", 1),
            Record("r2", "2026-07-20", "m", 2),
            Record("r3", "2026-07-20", "m", 3),
        ]);

        // 3 of 31 days would mislead; only 2 days actually carry data.
        Assert.Equal(2, _store.CountRecordedDays(new DateOnly(2026, 6, 20), new DateOnly(2026, 7, 21)));
    }

    [Fact]
    public void EmptyStore_LatestActivityIsNull()
    {
        Assert.Null(_store.LatestActivityUtc());
    }

    [Fact]
    public void LatestActivity_ReturnsNewestTimestamp()
    {
        _store.Ingest([
            Record("r1", "2026-07-19", "m", 1),
            Record("r2", "2026-07-21", "m", 2),
        ]);

        Assert.Equal(DateTimeOffset.Parse("2026-07-21T12:00:00Z"), _store.LatestActivityUtc());
    }

    [Fact]
    public void CorruptDatabase_IsBackedUpAndRebuilt()
    {
        // issue #16: a malformed usage.db threw SQLITE_CORRUPT on every query and blanked
        // the whole usage display. A rebuildable cache must self-heal, not stay fatal.
        var path = Path.Combine(_dir, "corrupt.db");
        var bytes = new byte[4096];
        // Valid SQLite magic + page size 4096, then garbage — a malformed file (not merely
        // "not a database"), which is what real on-disk corruption looks like.
        System.Text.Encoding.ASCII.GetBytes("SQLite format 3\0").CopyTo(bytes, 0);
        bytes[16] = 0x10; bytes[17] = 0x00;
        for (var i = 100; i < bytes.Length; i++) bytes[i] = 0xEE;
        File.WriteAllBytes(path, bytes);

        using (var store = new RollupStore(path))
        {
            // A rebuilt, empty, healthy store — usable, with no leftover rows.
            store.Ingest([Record("r1", "2026-07-20", "claude-opus-4-8", 100)]);
            Assert.Single(store.GetDailyRollups(new DateOnly(2026, 7, 20), new DateOnly(2026, 7, 20)));
        }

        // The corrupt original was preserved for post-mortem, not silently destroyed.
        Assert.NotEmpty(Directory.GetFiles(_dir, "corrupt.db.corrupt-*"));
    }
}
