using OView.App;
using OView.Core.Models;
using OView.Core.Providers;
using OView.Core.Providers.CachedUsage;
using OView.Core.Providers.PlanHistory;

namespace OView.App.Tests;

/// <summary>
/// Reset times that Claude <b>reported</b> beat reset times O-view <b>derived</b>.
///
/// <para>Every other reset time in the app is inferred from a drop in a sampled series, so its
/// precision is bounded by the gap between samples — about half an interval for the five-hour
/// window, and roughly ten hours for the weekly one, whose resets land overnight while Desktop
/// is closed (ADR-0011). Claude Code caches the exact instants in <c>~/.claude.json</c>, and
/// once those are on disk the derived figures are strictly worse answers to the same
/// question.</para>
///
/// <para>So this fold <b>overrides</b>, where the entered weekly reset only fills a gap — and
/// it runs after it, so it wins over that too.</para>
/// </summary>
public class ReportedResetTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);

    private sealed class FixedProvider(UsageSnapshot snapshot) : IUsageProvider
    {
        public UsageSnapshot GetSnapshot(DateTimeOffset utcNow) => snapshot;
    }

    private sealed class ThrowingProvider : IUsageProvider
    {
        public UsageSnapshot GetSnapshot(DateTimeOffset utcNow) => throw new IOException("locked");
    }

    /// <summary>A Desktop machine: percentages and derived, bracketed reset times.</summary>
    private static UsageSnapshot Derived { get; } = new(
        DataSource.Live, 40, 60,
        SessionResetAtUtc: T0.AddHours(2),
        CapturedAtUtc: T0,
        WeeklyResetAtUtc: T0.AddDays(3),
        WeeklyResetUncertainty: TimeSpan.FromHours(10),
        WeeklyResetPeriod: TimeSpan.FromDays(7),
        SessionResetUncertainty: TimeSpan.FromMinutes(8));

    private static IUsageProvider Cache(
        DateTimeOffset? sessionResets, DateTimeOffset? weeklyResets, DateTimeOffset? fetchedAt = null) =>
        new CachedUtilizationProvider(() => new CachedUtilization(
            fetchedAt ?? T0,
            "acct",
            sessionResets is null ? null : new UtilizationBar(91, sessionResets),
            weeklyResets is null ? null : new UtilizationBar(79, weeklyResets)));

    private static UsageEngine Build(
        TempDir dir, UsageSnapshot snapshot, IUsageProvider? cache, ManualWeeklyReset? entry = null)
    {
        if (entry is not null)
        {
            new TraySettings(WeeklyResetDay: entry.DayText, WeeklyResetTime: entry.TimeText)
                .Save(dir.File("settings.json"));
        }

        return new UsageEngine(new UsageEngineOptions
        {
            Clock = new FakeClock(T0),
            Provider = new FixedProvider(snapshot),
            CachedUtilization = cache ?? new CachedUtilizationProvider(() => null),
            RollupDbPath = dir.File("usage.db"),
            WeeklyResetLogPath = dir.File("weekly-resets.json"),
            SettingsPath = dir.File("settings.json"),
        });
    }

    /// <summary>
    /// The headline: an exact instant replaces the bracketed guess, and takes the bracket with
    /// it so the display stops marking it approximate.
    /// </summary>
    [Fact]
    public void AReportedResetReplacesTheDerivedOneAndItsUncertainty()
    {
        using var dir = new TempDir();
        var session = T0.AddHours(3).AddMinutes(17);
        var weekly = T0.AddDays(2).AddHours(11);
        using var engine = Build(dir, Derived, Cache(session, weekly));

        engine.Refresh();

        Assert.Equal(session, engine.Latest.SessionResetAtUtc);
        Assert.Equal(weekly, engine.Latest.WeeklyResetAtUtc);
        Assert.Equal(TimeSpan.Zero, engine.Latest.SessionResetUncertainty);
        Assert.Equal(TimeSpan.Zero, engine.Latest.WeeklyResetUncertainty);
    }

    /// <summary>
    /// The entry exists because the derived weekly reset is imprecise (issue #186). A reported
    /// instant answers that question outright, so it supersedes what the user typed — including
    /// when they typed it wrong, which is the case that matters.
    /// </summary>
    [Fact]
    public void AReportedResetSupersedesTheUsersEnteredOne()
    {
        using var dir = new TempDir();
        var weekly = T0.AddDays(2).AddHours(11);
        using var engine = Build(
            dir,
            Derived with { WeeklyResetAtUtc = null, WeeklyResetUncertainty = null },
            Cache(sessionResets: null, weeklyResets: weekly),
            entry: new ManualWeeklyReset(DayOfWeek.Monday, new TimeOnly(22, 59)));

        engine.Refresh();

        Assert.Equal(weekly, engine.Latest.WeeklyResetAtUtc);
        Assert.Equal(TimeSpan.Zero, engine.Latest.WeeklyResetUncertainty);
    }

    /// <summary>
    /// With no cache — a machine that has never run Claude Code — everything behaves exactly as
    /// it did before this existed.
    /// </summary>
    [Fact]
    public void WithNoCachedFiguresTheDerivedResetsAreUntouched()
    {
        using var dir = new TempDir();
        using var engine = Build(dir, Derived, cache: null);

        engine.Refresh();

        Assert.Equal(Derived.SessionResetAtUtc, engine.Latest.SessionResetAtUtc);
        Assert.Equal(Derived.WeeklyResetAtUtc, engine.Latest.WeeklyResetAtUtc);
        Assert.Equal(TimeSpan.FromHours(10), engine.Latest.WeeklyResetUncertainty);
    }

    /// <summary>
    /// A refinement must never remove the figure it was meant to refine. A cached window that
    /// has already rolled past its boundary reports nothing, and nothing must stay nothing —
    /// not null written over a good derived value.
    /// </summary>
    [Fact]
    public void ARolledOverCacheLeavesTheDerivedResetsInPlace()
    {
        using var dir = new TempDir();
        using var engine = Build(
            dir, Derived, Cache(T0.AddHours(-2), T0.AddDays(-1), fetchedAt: T0.AddHours(-8)));

        engine.Refresh();

        Assert.Equal(Derived.SessionResetAtUtc, engine.Latest.SessionResetAtUtc);
        Assert.Equal(Derived.WeeklyResetAtUtc, engine.Latest.WeeklyResetAtUtc);
    }

    /// <summary>Each window is folded on its own; one being absent must not drop the other.</summary>
    [Fact]
    public void OnlyTheWindowTheCacheKnowsAboutIsReplaced()
    {
        using var dir = new TempDir();
        var weekly = T0.AddDays(2);
        using var engine = Build(dir, Derived, Cache(sessionResets: null, weeklyResets: weekly));

        engine.Refresh();

        Assert.Equal(weekly, engine.Latest.WeeklyResetAtUtc);
        Assert.Equal(Derived.SessionResetAtUtc, engine.Latest.SessionResetAtUtc);
        Assert.Equal(TimeSpan.FromMinutes(8), engine.Latest.SessionResetUncertainty);
    }

    /// <summary>
    /// A panel with no data at all stays blank rather than growing one lonely populated line —
    /// the same rule the entered reset follows.
    /// </summary>
    [Fact]
    public void ASnapshotWithNoDataAtAllStaysEmpty()
    {
        using var dir = new TempDir();
        using var engine = Build(dir, UsageSnapshot.None, Cache(T0.AddHours(3), T0.AddDays(2)));

        engine.Refresh();

        Assert.Equal(DataSource.None, engine.Latest.Source);
        Assert.Null(engine.Latest.SessionResetAtUtc);
        Assert.Null(engine.Latest.WeeklyResetAtUtc);
    }

    /// <summary>
    /// The refinement is allowed to fail. A locked or half-written <c>.claude.json</c> costs the
    /// exact timestamps and nothing else — the poll still succeeds and the panel still fills.
    /// </summary>
    [Fact]
    public void AFailingCacheReadCostsPrecisionRatherThanThePoll()
    {
        using var dir = new TempDir();
        using var engine = Build(dir, Derived, new ThrowingProvider());

        engine.Refresh();

        Assert.Equal(DataSource.Live, engine.Latest.Source);
        Assert.Equal(40, engine.Latest.SessionPercent);
        Assert.Equal(Derived.SessionResetAtUtc, engine.Latest.SessionResetAtUtc);
    }

    /// <summary>
    /// The population this was built for: no Claude Desktop, so no plan history and no
    /// percentages from it — but Claude Code has been running, so both meters and both exact
    /// resets are on disk. Before this, these users saw two empty bars all day.
    /// </summary>
    [Fact]
    public void AMachineWithNoDesktopGetsBothMetersFromTheCache()
    {
        using var dir = new TempDir();
        var cache = Cache(T0.AddHours(3), T0.AddDays(2));
        using var engine = new UsageEngine(new UsageEngineOptions
        {
            Clock = new FakeClock(T0),
            Provider = new CompositeUsageProvider(cache),
            CachedUtilization = cache,
            RollupDbPath = dir.File("usage.db"),
            WeeklyResetLogPath = dir.File("weekly-resets.json"),
            SettingsPath = dir.File("settings.json"),
        });

        engine.Refresh();

        Assert.Equal(DataSource.Live, engine.Latest.Source);
        Assert.Equal(91, engine.Latest.SessionPercent);
        Assert.Equal(79, engine.Latest.WeeklyPercent);
        Assert.Equal(T0.AddHours(3), engine.Latest.SessionResetAtUtc);
        Assert.Equal(T0.AddDays(2), engine.Latest.WeeklyResetAtUtc);
    }
}
