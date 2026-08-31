using OView.Core.Pricing;
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
        new(id, DateTimeOffset.Parse(date + "T12:00:00Z"), model,
            new TokenSplit(10, 0, 100, 0, 200, output), UsageModifiers.Standard);

    /// <summary>Every zone is named explicitly — none of these read the machine's (issue #211).</summary>
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static readonly TimeZoneInfo PlusTwo =
        TimeZoneInfo.CreateCustomTimeZone("test-plus-2", TimeSpan.FromHours(2), "UTC+2", "UTC+2");

    private static DateTimeOffset At(string timestamp) => DateTimeOffset.Parse(timestamp);

    [Fact]
    public void Rollups_GroupByDateAndModel()
    {
        _store.Ingest([
            Record("r1", "2026-07-20", "claude-opus-4-8", 100),
            Record("r2", "2026-07-20", "claude-opus-4-8", 50),
            Record("r3", "2026-07-20", "claude-sonnet-5", 30),
            Record("r4", "2026-07-21", "claude-opus-4-8", 70),
        ]);

        var rollups = _store.GetDailyRollups(At("2026-07-20T00:00:00Z"), At("2026-07-22T00:00:00Z"), Utc);

        Assert.Equal(3, rollups.Count);
        var day1Opus = rollups.Single(r => r.Date == new DateOnly(2026, 7, 20) && r.Model == "claude-opus-4-8");
        Assert.Equal(150, day1Opus.Tokens.Output);
        Assert.Equal(2, day1Opus.RequestCount);
    }

    [Fact]
    public void Range_IsHalfOpenAndExcludesOutside()
    {
        _store.Ingest([
            Record("r1", "2026-07-19", "m", 1),
            Record("r2", "2026-07-20", "m", 2),
            Record("r3", "2026-07-21", "m", 4),
        ]);

        var rollups = _store.GetDailyRollups(At("2026-07-20T00:00:00Z"), At("2026-07-21T00:00:00Z"), Utc);

        Assert.Equal(2, rollups.Sum(r => r.Tokens.Output));
    }

    /// <summary>
    /// The point of the whole change (issue #211): the bucket a request lands in follows the
    /// caller's timezone, not the <c>utc_date</c> it was stored under. Same rows, same store,
    /// two zones, two answers — and both are right for whoever is reading.
    /// </summary>
    [Fact]
    public void Rollups_BucketByTheCallersDay_NotTheStoredUtcDate()
    {
        // 23:00Z on the 20th is 01:00 on the 21st at UTC+2.
        _store.Ingest([new TranscriptRecord("late", At("2026-07-20T23:00:00Z"), "m", new TokenSplit(0, 0, 0, 0, 0, 5), UsageModifiers.Standard)]);

        var inUtc = _store.GetDailyRollups(At("2026-07-01T00:00:00Z"), At("2026-08-01T00:00:00Z"), Utc);
        var inPlusTwo = _store.GetDailyRollups(At("2026-07-01T00:00:00Z"), At("2026-08-01T00:00:00Z"), PlusTwo);

        Assert.Equal(new DateOnly(2026, 7, 20), Assert.Single(inUtc).Date);
        Assert.Equal(new DateOnly(2026, 7, 21), Assert.Single(inPlusTwo).Date);
    }

    /// <summary>
    /// Rows are grouped after the conversion, not before it: two UTC days that fall inside one
    /// local day are one bucket, summed, not two rows that happen to share a date.
    /// </summary>
    [Fact]
    public void Rollups_MergeAcrossAUtcBoundaryInsideOneLocalDay()
    {
        // Both of these are 2026-07-21 at UTC-5, either side of midnight UTC.
        var minusFive = TimeZoneInfo.CreateCustomTimeZone(
            "test-minus-5", TimeSpan.FromHours(-5), "UTC-5", "UTC-5");

        _store.Ingest([
            new TranscriptRecord("evening", At("2026-07-22T02:00:00Z"), "m", new TokenSplit(0, 0, 0, 0, 0, 7), UsageModifiers.Standard),
            new TranscriptRecord("morning", At("2026-07-21T14:00:00Z"), "m", new TokenSplit(0, 0, 0, 0, 0, 3), UsageModifiers.Standard),
        ]);

        var rollups = _store.GetDailyRollups(At("2026-07-01T00:00:00Z"), At("2026-08-01T00:00:00Z"), minusFive);

        var day = Assert.Single(rollups);
        Assert.Equal(new DateOnly(2026, 7, 21), day.Date);
        Assert.Equal(10, day.Tokens.Output);
        Assert.Equal(2, day.RequestCount);
    }

    // CoverageCount_ReportsDistinctRecordedDays went with CountRecordedDays (issue #142). It
    // asserted the behaviour that turned out to be the bug — that a day with no usage does not
    // count as recorded — so it could not be repaired, only removed. Coverage is now derived
    // from the day series and covered by PanelStatisticsTests, next to the PreInstall boundary
    // it has to agree with.

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
            Assert.Single(store.GetDailyRollups(At("2026-07-20T00:00:00Z"), At("2026-07-21T00:00:00Z"), Utc));
        }

        // The corrupt original was preserved for post-mortem, not silently destroyed.
        Assert.NotEmpty(Directory.GetFiles(_dir, "corrupt.db.corrupt-*"));
    }

    // ── earliest request in an interval (issue #185) ────────────────────────────────

    private static TranscriptRecord At(string id, string timestamp) =>
        new(id, DateTimeOffset.Parse(timestamp), "claude-opus-5",
            new TokenSplit(10, 0, 100, 0, 200, 50), UsageModifiers.Standard);

    /// <summary>
    /// Used to tighten the five-hour window's start bracket: the earliest request in the
    /// interval is the moment the window is first known to have been running.
    /// </summary>
    [Fact]
    public void EarliestRequestBetween_ReturnsTheFirstRequestInTheInterval()
    {
        _store.Ingest([
            At("a", "2026-08-23T18:50:00Z"),
            At("b", "2026-08-23T19:02:00Z"),
            At("c", "2026-08-23T19:05:00Z"),
        ]);

        var earliest = _store.EarliestRequestBetween(
            DateTimeOffset.Parse("2026-08-23T18:52:00Z"),
            DateTimeOffset.Parse("2026-08-23T19:07:00Z"));

        Assert.Equal(DateTimeOffset.Parse("2026-08-23T19:02:00Z"), earliest);
    }

    /// <summary>
    /// Exclusive at the lower end: a request exactly at the previous sample belongs to the
    /// window that was ending, and admitting it would widen the bracket rather than narrow it.
    /// </summary>
    [Fact]
    public void EarliestRequestBetween_ExcludesTheLowerBoundAndIncludesTheUpper()
    {
        _store.Ingest([At("lower", "2026-08-23T18:52:00Z"), At("upper", "2026-08-23T19:07:00Z")]);

        var found = _store.EarliestRequestBetween(
            DateTimeOffset.Parse("2026-08-23T18:52:00Z"),
            DateTimeOffset.Parse("2026-08-23T19:07:00Z"));

        Assert.Equal(DateTimeOffset.Parse("2026-08-23T19:07:00Z"), found);
    }

    /// <summary>No activity is the normal case for a chat-only user, not an error.</summary>
    [Fact]
    public void EarliestRequestBetween_IsNullWhenNothingFallsInside()
    {
        _store.Ingest([At("a", "2026-08-23T12:00:00Z")]);

        Assert.Null(_store.EarliestRequestBetween(
            DateTimeOffset.Parse("2026-08-23T18:52:00Z"),
            DateTimeOffset.Parse("2026-08-23T19:07:00Z")));
    }

    [Fact]
    public void EarliestRequestBetween_IsNullOnAnEmptyStore()
    {
        Assert.Null(_store.EarliestRequestBetween(
            DateTimeOffset.Parse("2026-08-23T00:00:00Z"),
            DateTimeOffset.Parse("2026-08-24T00:00:00Z")));
    }
}
