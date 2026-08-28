using OView.App;
using OView.Core.Models;
using OView.Core.Providers.PlanHistory;

namespace OView.App.Tests;

/// <summary>
/// The entered weekly reset (GitHub issue #186) must reach the people it was built for.
///
/// <para><b>It did not.</b> <c>PlanHistoryProvider</c> resolves the entry itself, but returns
/// <c>None</c> before reaching that code when there is no plan-history file at all — and the
/// composite then falls through to the JSONL estimate, whose weekly reset is null. So a user
/// with no Claude Desktop entered their reset time and saw nothing change: precisely the
/// population that cannot derive the value instead, and the only reason the feature exists.
/// Shipped that way in v0.6.16.</para>
/// </summary>
public class EnteredWeeklyResetTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);

    private static readonly ManualWeeklyReset Entry = new(DayOfWeek.Monday, new TimeOnly(22, 59));

    /// <summary>A provider standing in for the composite's answer on a given machine.</summary>
    private sealed class FixedProvider(UsageSnapshot snapshot) : Core.Providers.IUsageProvider
    {
        public UsageSnapshot GetSnapshot(DateTimeOffset utcNow) => snapshot;
    }

    private static UsageEngine Build(TempDir dir, UsageSnapshot snapshot, ManualWeeklyReset? entry)
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
            RollupDbPath = dir.File("usage.db"),
            WeeklyResetAnchorPath = dir.File("weekly-resets.json"),
            SettingsPath = dir.File("settings.json"),
        });
    }

    /// <summary>
    /// The regression. A CLI-only machine has no plan-history file, so the snapshot that wins
    /// is the JSONL estimate — percentages null, weekly reset null. The entered time must
    /// still appear, because it is the one figure that machine can know.
    /// </summary>
    [Fact]
    public void AMachineWithNoPlanHistoryStillGetsTheEnteredReset()
    {
        using var dir = new TempDir();
        var jsonlOnly = new UsageSnapshot(DataSource.Estimate, null, null, null, T0.AddMinutes(-2));
        using var engine = Build(dir, jsonlOnly, Entry);

        engine.Refresh();

        Assert.NotNull(engine.Latest.WeeklyResetAtUtc);
        Assert.Equal(DayOfWeek.Monday,
            TimeZoneInfo.ConvertTime(engine.Latest.WeeklyResetAtUtc!.Value, TimeZoneInfo.Local).DayOfWeek);

        // The percentages stay null — those genuinely cannot be known without Desktop, and
        // inventing one would be the fabrication rule 6 forbids.
        Assert.Null(engine.Latest.WeeklyPercent);
        Assert.Null(engine.Latest.SessionPercent);
    }

    /// <summary>
    /// Only fills a gap. A snapshot that already carries a weekly reset has been through the
    /// provider's entry-versus-evidence rule, which is the one place allowed to choose between
    /// them — overwriting here would reinstate an entry an observation had just disproved.
    /// </summary>
    [Fact]
    public void TheEntryIsUsedEvenWhereADerivedResetWouldOnceHaveWon()
    {
        using var dir = new TempDir();

        // A snapshot still carrying a bracketed weekly reset. Under ADR-0011 the entry only
        // filled a gap, so this value survived and the user's own answer was ignored.
        //
        // No provider produces one any more — PlanHistoryProvider returns null for the weekly
        // window — so this is a snapshot the app cannot actually build, kept here to pin that
        // the entry wins even against the thing that used to outrank it. What made the old
        // rule wrong is that the derived value was an inference and the entry was read off
        // Claude's own screen; deferring to the inference preferred a guess to an answer.
        var stale = new UsageSnapshot(
            DataSource.Live, 40, 60, T0.AddHours(2), T0,
            WeeklyResetAtUtc: T0.AddDays(3),
            WeeklyResetPeriod: TimeSpan.FromDays(7));
        using var engine = Build(dir, stale, Entry);

        engine.Refresh();

        Assert.Equal(Entry.NextAfter(T0, TimeZoneInfo.Local), engine.Latest.WeeklyResetAtUtc);
    }

    /// <summary>With nothing entered, nothing is added.</summary>
    [Fact]
    public void WithNoEntryTheSnapshotIsUntouched()
    {
        using var dir = new TempDir();
        var jsonlOnly = new UsageSnapshot(DataSource.Estimate, null, null, null, T0.AddMinutes(-2));
        using var engine = Build(dir, jsonlOnly, entry: null);

        engine.Refresh();

        Assert.Null(engine.Latest.WeeklyResetAtUtc);
    }

    /// <summary>
    /// A panel with no data at all stays blank rather than growing one lonely populated line
    /// — a weekly reset beside four empty tiles reads as a half-broken app.
    /// </summary>
    [Fact]
    public void ASnapshotWithNoDataAtAllStaysEmpty()
    {
        using var dir = new TempDir();
        using var engine = Build(dir, UsageSnapshot.None, Entry);

        engine.Refresh();

        Assert.Null(engine.Latest.WeeklyResetAtUtc);
        Assert.Equal(DataSource.None, engine.Latest.Source);
    }

    /// <summary>
    /// Both cadences must decorate the snapshot the same way, or the difference shows up as a
    /// blink.
    ///
    /// <para>The regression, shipped in v0.6.24: ADR-0014 moved the weekly reset out of
    /// <c>PlanHistoryProvider</c> and into the engine, but only the full poll applied it. The
    /// plan-history cadence publishes straight from the provider, so every 20 seconds it
    /// replaced a snapshot that had a weekly reset with one that did not, and the 60-second
    /// poll put it back. Measured on a real machine as consecutive tooltips reading
    /// <c>7d: 13%</c> and <c>7d: 13% · resets Mon 22:59</c>.</para>
    ///
    /// <para><c>PublishPlanHistory</c> already guards this shape for the <i>percentages</i>,
    /// which is why the same flicker arrived through the reset instead.</para>
    /// </summary>
    [Fact]
    public void ThePlanOnlyCadencePublishesTheWeeklyResetToo()
    {
        using var dir = new TempDir();

        // A real plan-history file, so the plan-only cadence has something authoritative to
        // publish — it drops anything that is not (see PublishPlanHistory).
        var planPath = dir.File("plan-usage-history.json");
        var sampledAt = T0.AddMinutes(-2).ToUnixTimeMilliseconds();
        File.WriteAllText(planPath,
            $"{{\"version\":2,\"samples\":[{{\"t\":{sampledAt},\"org\":\"org-a\","
            + "\"u\":{\"fh\":23,\"sd\":13}}]}");

        new TraySettings(WeeklyResetDay: Entry.DayText, WeeklyResetTime: Entry.TimeText)
            .Save(dir.File("settings.json"));

        using var engine = new UsageEngine(new UsageEngineOptions
        {
            Clock = new FakeClock(T0),
            PlanHistoryPath = planPath,
            RollupDbPath = dir.File("usage.db"),
            WeeklyResetAnchorPath = dir.File("weekly-reset.json"),
            SettingsPath = dir.File("settings.json"),
        });

        var timers = new FakeTimerFactory();
        engine.Start(timers);

        var afterFullPoll = engine.Latest.WeeklyResetAtUtc;
        Assert.NotNull(afterFullPoll);

        // The tick that used to blank it.
        timers.PlanPoll.Tick();

        Assert.Equal(afterFullPoll, engine.Latest.WeeklyResetAtUtc);
    }
}
