using OView.Core.Models;

namespace OView.App.Tests;

/// <summary>
/// The smoke test: the real engine, with its real provider chain and store, run once against
/// a committed Claude home (<c>tests/fixtures/claude-home</c>). Unit tests prove the pieces;
/// this proves they are wired together — that real-shaped files on disk come out as the
/// numbers the panel shows. An app whose providers were never connected passes every unit
/// test and fails this one.
///
/// <para>Every location is the fixture's (<see cref="ClaudeDataLocations"/>), never this
/// machine's, so the result is the same on a developer's desktop and on a CI runner.</para>
/// </summary>
[Trait("Category", "Smoke")]
public class SmokeTests
{
    private static readonly DateTimeOffset At = new(2026, 7, 28, 10, 47, 0, TimeSpan.Zero);

    private static string FixtureHome()
    {
        // From the test binary up to the repository root, which holds the solution file.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "O-view.slnx")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "tests", "fixtures", "claude-home");
    }

    private static (UsageSnapshot Snapshot, PanelStatistics Stats) RunOnce()
    {
        using var temp = new TempDir();
        using var engine = new UsageEngine(new UsageEngineOptions
        {
            Clock = new FakeClock(At),
            DisplayZone = TimeZoneInfo.Utc,
            ClaudeData = ClaudeDataLocations.UnderHome(FixtureHome()),
            RollupDbPath = temp.File("usage.db"),
            WeeklyResetAnchorPath = temp.File("weekly-reset.json"),
            SettingsPath = temp.File("settings.json"),
        });

        engine.Refresh();
        return (engine.Latest, engine.BuildStatistics());
    }

    [Fact]
    public void PlanHistoryOnDisk_ReachesTheMeters()
    {
        var (snapshot, _) = RunOnce();

        Assert.Equal(DataSource.Live, snapshot.Source);
        Assert.Equal(25, snapshot.SessionPercent);
        Assert.Equal(3, snapshot.WeeklyPercent);
    }

    [Fact]
    public void TranscriptsOnDisk_ReachTheTokenTiles_EachRequestCountedOnce()
    {
        var (_, stats) = RunOnce();

        // 1,500 + 2,500 today: the request written twice counts once, the synthetic record
        // counts not at all.
        Assert.Equal(4_000, stats.TokensToday);
        // Plus 4,000 yesterday.
        Assert.Equal(8_000, stats.Tokens31Days);
    }
}
