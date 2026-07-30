using Microsoft.Data.Sqlite;
using OView.Core.Models;
using OView.Core.Storage;

namespace OView.App.Tests;

/// <summary>
/// What the engine does on the way up and the way down: settings persistence, and the
/// pre-ADR-0011 weekly-reset migration that runs on every single launch.
/// </summary>
public class EngineLifecycleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    private static UsageEngine NewEngine(TempDir dir) => new(new UsageEngineOptions
    {
        Clock = new FakeClock(T0),
        Provider = new FakeProvider(),
        RollupDbPath = dir.File("usage.db"),
        WeeklyResetLogPath = dir.File("weekly-resets.json"),
        SettingsPath = dir.File("settings.json"),
    });

    [Fact]
    public void SettingsRoundTripAcrossRestarts()
    {
        using var dir = new TempDir();

        using (var engine = NewEngine(dir))
        {
            Assert.True(engine.Settings.NotifyOnThreshold);   // the default
            Assert.False(engine.SetNotifyOnThreshold(false));
        }

        using (var reopened = NewEngine(dir))
        {
            Assert.False(reopened.Settings.NotifyOnThreshold);
            Assert.True(reopened.SetNotifyOnThreshold(true));
        }

        using var again = NewEngine(dir);
        Assert.True(again.Settings.NotifyOnThreshold);
    }

    /// <summary>
    /// A missing or unreadable settings file must yield defaults rather than throwing — a
    /// lost preference must never stop the app starting.
    /// </summary>
    [Fact]
    public void CorruptSettingsFallBackToDefaults()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("settings.json"), "{ this is not json");

        using var engine = NewEngine(dir);

        Assert.True(engine.Settings.NotifyOnThreshold);
        Assert.Equal(UsageLevels.CriticalPercent, engine.Settings.ThresholdPercent);
    }

    /// <summary>
    /// The migration runs on every launch (ADR-0011), so it has to be idempotent — otherwise
    /// each restart would re-import the same resets and the log would grow without bound,
    /// eventually evicting real observations past <c>MaxObservations</c>.
    /// </summary>
    [Fact]
    public void LegacyWeeklyResetMigrationIsIdempotentAcrossRuns()
    {
        using var dir = new TempDir();
        var dbPath = dir.File("usage.db");
        var logPath = dir.File("weekly-resets.json");

        // Create the schema, then seed the pre-ADR-0011 table the way an upgraded install
        // would already have it. Nothing writes this table any more, so it is seeded directly.
        using (var store = new RollupStore(dbPath))
        {
            _ = store.GetLegacyWeeklyResets();
        }

        using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
            connection.Open();
            foreach (var at in new[] { "2026-07-01T03:00:00+00:00", "2026-07-08T03:14:00+00:00" })
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "INSERT INTO weekly_resets (reset_at) VALUES ($at)";
                cmd.Parameters.AddWithValue("$at", at);
                cmd.ExecuteNonQuery();
            }
        }

        using (var first = NewEngine(dir)) { }
        var afterFirst = new WeeklyResetLog(logPath).GetObservations().Count;

        using (var second = NewEngine(dir)) { }
        using (var third = NewEngine(dir)) { }
        var afterThird = new WeeklyResetLog(logPath).GetObservations().Count;

        Assert.Equal(2, afterFirst);        // both legacy rows carried across
        Assert.Equal(afterFirst, afterThird);   // and not re-imported on later launches
    }

    /// <summary>
    /// A brand-new install has no rollup DB and no legacy table content. Construction must
    /// still succeed — this is the common case, not an edge one.
    /// </summary>
    [Fact]
    public void StartsCleanlyWithNoExistingState()
    {
        using var dir = new TempDir();

        using var engine = NewEngine(dir);
        var timers = new FakeTimerFactory();
        engine.Start(timers);

        Assert.Equal(UsageSnapshot.None, engine.Latest);
        Assert.True(timers.Poll.IsRunning);
    }

    /// <summary>
    /// <see cref="UsageEngine.BuildStatistics"/> must return something usable on an empty
    /// store rather than throwing — the panel opens on a fresh install too.
    /// </summary>
    [Fact]
    public void BuildStatisticsWorksOnAnEmptyStore()
    {
        using var dir = new TempDir();
        using var engine = NewEngine(dir);

        var stats = engine.BuildStatistics();

        Assert.NotNull(stats);
        Assert.False(stats.IsOffPlan);
    }
}
