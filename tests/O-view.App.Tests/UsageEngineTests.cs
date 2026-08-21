using OView.Core.Models;

namespace OView.App.Tests;

/// <summary>
/// The refresh cycle and the notification rules — logic that shipped for months inside
/// <c>App.xaml.cs</c> with no coverage at all, because it could not be reached without a
/// WPF dispatcher and a desktop session.
/// </summary>
public class UsageEngineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    /// <summary>How long a dispatcher-driven test waits for a thread-pool read to land.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static (UsageEngine Engine, FakeProvider Provider, FakeTimerFactory Timers, ListLog Log)
        // A transform, not an Action. UsageEngineOptions is a record with init-only members,
        // so the previous `Action<UsageEngineOptions>` could not set anything — it compiled,
        // ran, and silently changed nothing. Nothing had used it, which is why that went
        // unnoticed; `o => o with { ... }` is the shape that actually works.
        Build(TempDir dir, Func<UsageEngineOptions, UsageEngineOptions>? tweak = null)
    {
        var provider = new FakeProvider();
        var log = new ListLog();
        var options = new UsageEngineOptions
        {
            Clock = new FakeClock(T0),
            Log = log,
            Provider = provider,
            RollupDbPath = dir.File("usage.db"),
            WeeklyResetLogPath = dir.File("weekly-resets.json"),
            SettingsPath = dir.File("settings.json"),
        };
        options = tweak?.Invoke(options) ?? options;
        return (new UsageEngine(options), provider, new FakeTimerFactory(), log);
    }

    [Fact]
    public void RefreshPublishesTheSnapshotFromTheProvider()
    {
        using var dir = new TempDir();
        var (engine, provider, timers, _) = Build(dir);
        using var _e = engine;

        var seen = new List<UsageSnapshot>();
        engine.SnapshotUpdated += seen.Add;
        provider.SetSession(42);

        engine.Start(timers);

        Assert.Single(seen);
        Assert.Equal(42, seen[0].SessionPercent);
        Assert.Equal(42, engine.Latest.SessionPercent);
    }

    [Fact]
    public void PollTimerDrivesFurtherRefreshes()
    {
        using var dir = new TempDir();
        var (engine, provider, timers, _) = Build(dir);
        using var _e = engine;

        provider.SetSession(10);
        engine.Start(timers);
        Assert.Equal(1, provider.Calls);

        timers.Poll.Tick();
        timers.Poll.Tick();

        Assert.Equal(3, provider.Calls);
    }

    /// <summary>
    /// The contract in <see cref="ThresholdWatcher"/>, asserted end to end through the
    /// engine: one notification per crossing, not one per poll. Level-triggered logic here
    /// would notify every 60 s for as long as usage stayed high.
    /// </summary>
    [Fact]
    public void ThresholdNotifiesOncePerCrossingNotOncePerPoll()
    {
        using var dir = new TempDir();
        var (engine, provider, timers, _) = Build(dir);
        using var _e = engine;

        var notifications = new List<AppNotification>();
        engine.NotificationRequested += notifications.Add;

        provider.SetSession(10);
        engine.Start(timers);
        Assert.Empty(notifications);

        provider.SetSession(95);          // crosses
        timers.Poll.Tick();
        Assert.Single(notifications);

        provider.SetSession(96);          // still above — must stay silent
        timers.Poll.Tick();
        timers.Poll.Tick();
        Assert.Single(notifications);

        provider.SetSession(5);           // window reset re-arms
        timers.Poll.Tick();
        Assert.Single(notifications);

        provider.SetSession(90);          // crosses again
        timers.Poll.Tick();
        Assert.Equal(2, notifications.Count);
    }

    // ── the plan-history cadence, decoupled from ingestion (issue #163) ───────────

    /// <summary>
    /// Writes a real plan-history file, because the fast path deliberately reads
    /// <c>PlanHistoryProvider</c> directly rather than the composite — that is the whole point
    /// of the split, so faking the composite would test the wrong seam.
    /// </summary>
    private static string WritePlanHistory(TempDir dir, params (DateTimeOffset At, int Fh, int Sd)[] samples)
    {
        var rows = samples.Select(s =>
            $"{{\"t\":{s.At.ToUnixTimeMilliseconds()},\"org\":\"org-a\",\"u\":{{\"fh\":{s.Fh},\"sd\":{s.Sd}}}}}");
        var path = dir.File("plan-usage-history.json");
        File.WriteAllText(path, $"{{\"version\":2,\"samples\":[{string.Join(',', rows)}]}}");
        return path;
    }

    /// <summary>
    /// The point of the split: a plan-history tick publishes new percentages without touching
    /// the transcripts. Measured on a real machine, the two reads cost 3.3 ms and 92 MB
    /// respectively, and they used to share one timer.
    /// </summary>
    [Fact]
    public void APlanTick_PublishesFreshPercentages_WithoutAFullPoll()
    {
        using var dir = new TempDir();
        var path = WritePlanHistory(dir, (T0.AddMinutes(-2), 40, 10));
        var (engine, provider, timers, _) = Build(dir, o => o with { PlanHistoryPath = path });
        using var _e = engine;

        provider.SetSession(40);
        engine.Start(timers);
        var callsAfterStart = provider.Calls;

        WritePlanHistory(dir, (T0.AddMinutes(-2), 40, 10), (T0.AddMinutes(-1), 55, 11));
        timers.PlanPoll.Tick();

        Assert.Equal(55, engine.Latest.SessionPercent);
        Assert.Equal(callsAfterStart, provider.Calls);   // the composite was not re-read
    }

    /// <summary>
    /// Crossing the threshold is noticed on the fast cadence rather than waiting for the full
    /// poll — a real gain, not a side effect, since the notification only needs the percentage.
    /// </summary>
    [Fact]
    public void APlanTick_CanRaiseTheThresholdNotification()
    {
        using var dir = new TempDir();
        var path = WritePlanHistory(dir, (T0.AddMinutes(-2), 10, 5));
        var (engine, provider, timers, _) = Build(dir, o => o with { PlanHistoryPath = path });
        using var _e = engine;

        var notifications = new List<AppNotification>();
        engine.NotificationRequested += notifications.Add;

        provider.SetSession(10);
        engine.Start(timers);
        Assert.Empty(notifications);

        WritePlanHistory(dir, (T0.AddMinutes(-2), 10, 5), (T0.AddMinutes(-1), 95, 6));
        timers.PlanPoll.Tick();

        Assert.Single(notifications);
    }

    /// <summary>
    /// A snapshot with nothing authoritative in it is dropped rather than published. The fast
    /// path skips the composite, so it cannot see the JSONL estimate that would otherwise win —
    /// publishing its <c>None</c> would blank a panel the full poll had correctly filled.
    /// </summary>
    [Fact]
    public void APlanTickWithNoData_LeavesTheExistingSnapshotAlone()
    {
        using var dir = new TempDir();
        var (engine, provider, timers, _) = Build(dir, o => o with { PlanHistoryPath = dir.File("absent.json") });
        using var _e = engine;

        provider.SetSession(37, DataSource.Estimate);
        engine.Start(timers);
        Assert.Equal(37, engine.Latest.SessionPercent);

        timers.PlanPoll.Tick();   // the file does not exist

        Assert.Equal(37, engine.Latest.SessionPercent);
        Assert.Equal(DataSource.Estimate, engine.Latest.Source);
    }

    /// <summary>
    /// The ordering guard has to work in both directions, and this is the direction that was
    /// missing. The <b>full</b> poll is the slow one, so it is the one that loses the race: it
    /// starts reading, spends seconds ingesting, and posts a snapshot it took before a plan
    /// tick that has already published. Guarded only on the fast path, the session percentage
    /// went 77 → 30 — the jump backwards the guard exists to prevent.
    /// </summary>
    [Fact]
    public void ALateFullPoll_DoesNotOverwriteANewerPlanReading()
    {
        using var dir = new TempDir();
        var path = WritePlanHistory(dir, (T0.AddMinutes(-3), 30, 10));
        var (engine, provider, timers, _) = Build(dir, o => o with { PlanHistoryPath = path });
        using var _e = engine;
        using var ingesting = new ManualResetEventSlim(false);

        var dispatcher = new FakeDispatcher();
        provider.SetSession(30);
        engine.Start(timers, dispatcher);
        Assert.True(dispatcher.PumpWithin(Patience));

        // A full poll starts reading and wedges inside the ingest.
        provider.BlockUntil = ingesting;
        timers.Poll.Tick();
        SpinWait.SpinUntil(() => provider.Calls == 2, Patience);

        // While it is stuck, Desktop writes a newer sample and the plan tick publishes it.
        WritePlanHistory(dir, (T0.AddMinutes(-3), 30, 10), (T0.AddMinutes(-1), 77, 25));
        timers.PlanPoll.Tick();
        Assert.True(dispatcher.PumpWithin(Patience));
        Assert.Equal(77, engine.Latest.SessionPercent);

        // The full poll now completes, carrying the reading it took BEFORE that tick.
        ingesting.Set();
        Assert.True(dispatcher.PumpWithin(Patience), "the full poll never published");

        Assert.Equal(77, engine.Latest.SessionPercent);
    }

    /// <summary>
    /// Being superseded must not cost the full poll the two decisions only it makes. Nothing
    /// else re-times the cadence, so an overtaken poll that skipped it would leave a warming-up
    /// engine stuck at 3 s with nothing left to move it.
    /// </summary>
    [Fact]
    public void AnOvertakenFullPoll_StillRetimesTheCadence()
    {
        using var dir = new TempDir();
        var path = WritePlanHistory(dir, (T0.AddMinutes(-3), 30, 10));
        var (engine, provider, timers, _) = Build(dir, o => o with { PlanHistoryPath = path });
        using var _e = engine;
        using var ingesting = new ManualResetEventSlim(false);

        var dispatcher = new FakeDispatcher();
        provider.Next = UsageSnapshot.None;
        engine.Start(timers, dispatcher);
        Assert.True(dispatcher.PumpWithin(Patience));
        Assert.Equal(TimeSpan.FromSeconds(3), timers.Poll.Interval);   // warming up

        provider.BlockUntil = ingesting;
        provider.SetSession(30);
        timers.Poll.Tick();
        SpinWait.SpinUntil(() => provider.Calls == 2, Patience);

        WritePlanHistory(dir, (T0.AddMinutes(-3), 30, 10), (T0.AddMinutes(-1), 77, 25));
        timers.PlanPoll.Tick();
        Assert.True(dispatcher.PumpWithin(Patience));

        ingesting.Set();
        Assert.True(dispatcher.PumpWithin(Patience));

        Assert.Equal(77, engine.Latest.SessionPercent);              // its snapshot was dropped
        Assert.Equal(TimeSpan.FromSeconds(60), timers.Poll.Interval); // its cadence decision was not
    }

    /// <summary>
    /// The guard compares two authoritative readings, and the incoming side matters as much as
    /// the current one. A JSONL estimate carries a capture time too — the newest transcript
    /// activity — so comparing it against a live plan sample would block the fallback the
    /// composite resolved to, and block it <i>permanently</i>: <c>Latest</c> stays Live, so
    /// every later estimate is measured against the same frozen sample and dropped in turn.
    /// The panel would sit on a reading that had stopped being true.
    /// </summary>
    [Fact]
    public void AnEstimateIsNotHeldBackByTheLiveReadingItSucceeds()
    {
        using var dir = new TempDir();
        var path = WritePlanHistory(dir, (T0.AddMinutes(-1), 64, 22));
        var (engine, provider, timers, _) = Build(dir, o => o with { PlanHistoryPath = path });
        using var _e = engine;

        provider.SetSession(64);
        engine.Start(timers);
        timers.PlanPoll.Tick();
        Assert.Equal(T0.AddMinutes(-1), engine.Latest.CapturedAtUtc);

        // Plan history stops resolving; the composite falls back to the local estimate, whose
        // capture time is older than the live sample now displayed.
        provider.SetSession(37, DataSource.Estimate);
        timers.Poll.Tick();

        Assert.Equal(DataSource.Estimate, engine.Latest.Source);
        Assert.Equal(37, engine.Latest.SessionPercent);
    }

    /// <summary>
    /// Clamped for the same reason the warm-up interval is, and by the same diagnostic:
    /// <c>--interval-ms</c> drives <c>PollInterval</c> alone, so a sub-20 s value would leave
    /// the cheap read running behind the full poll it exists to run ahead of.
    /// </summary>
    [Fact]
    public void ThePlanIntervalNeverExceedsThePollInterval()
    {
        using var dir = new TempDir();
        var path = WritePlanHistory(dir, (T0.AddMinutes(-1), 40, 10));
        var (engine, _, timers, _) = Build(dir, o => o with
        {
            PlanHistoryPath = path,
            PollInterval = TimeSpan.FromSeconds(2),
        });
        using var _e = engine;

        engine.Start(timers);

        Assert.Equal(TimeSpan.FromSeconds(2), timers.PlanPoll.Interval);
    }

    /// <summary>
    /// A caller that injects a whole provider chain and names no plan-history file gets no
    /// fast path. The engine still builds a provider — the off-plan arithmetic needs one — but
    /// it defaults to the <b>real machine's</b> plan-usage-history.json and is not part of the
    /// injected chain, so publishing from it would put the developer's own Claude Desktop usage
    /// over the caller's resolution.
    ///
    /// <para>Worth being straight about what this asserts where: on a machine with Claude
    /// Desktop data it is a real assertion and fails without the guard, which is how it was
    /// checked. On a CI runner there is no such file and it passes trivially. That asymmetry is
    /// the defect exactly — a suite whose result depends on whose machine ran it.</para>
    /// </summary>
    [Fact]
    public void AnInjectedProviderNamingNoPlanHistory_NeverPublishesFromTheRealMachinesFile()
    {
        using var dir = new TempDir();
        var (engine, provider, timers, _) = Build(dir);   // no PlanHistoryPath, no PlanHistory
        using var _e = engine;

        provider.SetSession(37, DataSource.Estimate);
        engine.Start(timers);

        timers.PlanPoll.Tick();

        Assert.Equal(DataSource.Estimate, engine.Latest.Source);
        Assert.Equal(37, engine.Latest.SessionPercent);
    }

    // ── automatic updates (ADR-0009 as amended, issue #140) ───────────────────────

    [Fact]
    public void UpdateAutomatically_IsOffUntilTheUserTurnsItOn()
    {
        using var dir = new TempDir();
        var (engine, _, _, _) = Build(dir);
        using var _e = engine;

        Assert.False(engine.Settings.UpdateAutomatically);

        Assert.True(engine.SetUpdateAutomatically(true));
        Assert.True(engine.Settings.UpdateAutomatically);
        Assert.True(TraySettings.Load(dir.File("settings.json")).UpdateAutomatically);

        Assert.False(engine.SetUpdateAutomatically(false));
        Assert.False(TraySettings.Load(dir.File("settings.json")).UpdateAutomatically);
    }

    /// <summary>
    /// Turning one preference on must not disturb the others. They share one record and one
    /// file, so a `with` that dropped a member would silently reset it — and the threshold
    /// resetting to 70 because someone enabled auto-update is the kind of thing nobody
    /// notices until a notification arrives early.
    /// </summary>
    [Fact]
    public void UpdateAutomatically_LeavesTheOtherSettingsAlone()
    {
        using var dir = new TempDir();
        var (engine, _, _, _) = Build(dir);
        using var _e = engine;

        engine.SetThresholdPercent(90);
        engine.SetNotifyOnThreshold(false);
        engine.SetUpdateAutomatically(true);

        var reloaded = TraySettings.Load(dir.File("settings.json"));
        Assert.Equal(90, reloaded.ThresholdPercent);
        Assert.False(reloaded.NotifyOnThreshold);
        Assert.True(reloaded.UpdateAutomatically);
    }

    /// <summary>
    /// Turning notifications off and on again must not swallow the next crossing.
    ///
    /// <para>The gate is <c>Settings.NotifyOnThreshold &amp;&amp; _watcher.ShouldNotify(...)</c>,
    /// and <c>&amp;&amp;</c> short-circuits — so while notifications are off the watcher is
    /// never consulted and its edge state freezes at whatever it held when they were switched
    /// off. Notify once at 95%, switch off, let the window reset, switch back on, climb past
    /// the threshold again: the watcher still believes it is above, and a genuine crossing is
    /// never reported.</para>
    /// </summary>
    [Fact]
    public void ReEnablingNotificationsDoesNotSwallowTheNextCrossing()
    {
        using var dir = new TempDir();
        var (engine, provider, timers, _) = Build(dir);
        using var _e = engine;

        var notifications = new List<AppNotification>();
        engine.NotificationRequested += notifications.Add;

        provider.SetSession(95);          // crosses 70 — one notification
        engine.Start(timers);
        Assert.Single(notifications);

        engine.SetNotifyOnThreshold(false);

        provider.SetSession(5);           // the window resets while notifications are off
        timers.Poll.Tick();

        engine.SetNotifyOnThreshold(true);

        provider.SetSession(80);          // a genuine new crossing
        timers.Poll.Tick();

        Assert.Equal(2, notifications.Count);
    }

    /// <summary>
    /// The same defect from the other direction: switching notifications on while usage is
    /// <i>already</i> past the threshold should report it, not stay silent until the next reset.
    /// </summary>
    [Fact]
    public void EnablingNotificationsWhileAlreadyAboveTheThresholdReportsIt()
    {
        using var dir = new TempDir();
        var (engine, provider, timers, _) = Build(dir);
        using var _e = engine;

        var notifications = new List<AppNotification>();
        engine.NotificationRequested += notifications.Add;
        engine.SetNotifyOnThreshold(false);

        provider.SetSession(85);
        engine.Start(timers);
        Assert.Empty(notifications);      // off, so silent

        engine.SetNotifyOnThreshold(true);
        timers.Poll.Tick();

        Assert.Single(notifications);
    }

    // ── the user-settable threshold (issue #141) ──────────────────────────────────

    [Fact]
    public void ThresholdPercentIsAppliedAndPersisted()
    {
        using var dir = new TempDir();
        var (engine, _, _, _) = Build(dir);
        using var _e = engine;

        Assert.Equal(UsageLevels.CriticalPercent, engine.Settings.ThresholdPercent);   // the default
        Assert.Equal(90, engine.SetThresholdPercent(90));
        Assert.Equal(90, engine.Settings.ThresholdPercent);

        // Reloaded from disk rather than trusted in memory: the menu passes state on every
        // open, so a threshold that did not reach the file silently reverts on restart.
        Assert.Equal(90, TraySettings.Load(dir.File("settings.json")).ThresholdPercent);
    }

    /// <summary>
    /// The notification actually fires at the new threshold, not the old one — the whole
    /// point, and the part a setter that only wrote settings would miss.
    /// </summary>
    [Fact]
    public void NotificationFollowsTheNewThreshold()
    {
        using var dir = new TempDir();
        var (engine, provider, timers, _) = Build(dir);
        using var _e = engine;

        var notifications = new List<AppNotification>();
        engine.NotificationRequested += notifications.Add;
        engine.SetThresholdPercent(90);

        provider.SetSession(75);          // above the 70 default, below the chosen 90
        engine.Start(timers);
        Assert.Empty(notifications);

        provider.SetSession(91);
        timers.Poll.Tick();
        Assert.Single(notifications);
    }

    /// <summary>
    /// Lowering the threshold under usage that is already past it notifies on the next poll.
    ///
    /// <para>This is why <see cref="UsageEngine.SetThresholdPercent"/> rebuilds the watcher
    /// rather than re-reading a field. The watcher is edge-triggered, so a stale "we are not
    /// above" flag decided against the old threshold would keep it silent until a window
    /// reset — leaving a user who just asked to be warned at 70%, while sitting at 75%,
    /// hearing nothing for hours.</para>
    /// </summary>
    [Fact]
    public void LoweringTheThresholdBelowCurrentUsageNotifiesOnTheNextPoll()
    {
        using var dir = new TempDir();
        var (engine, provider, timers, _) = Build(dir);
        using var _e = engine;

        var notifications = new List<AppNotification>();
        engine.NotificationRequested += notifications.Add;
        engine.SetThresholdPercent(80);

        provider.SetSession(75);
        engine.Start(timers);
        Assert.Empty(notifications);

        engine.SetThresholdPercent(70);   // 75 is now over the line
        timers.Poll.Tick();

        Assert.Single(notifications);
    }

    /// <summary>And the converse: raising it past current usage must not fire.</summary>
    [Fact]
    public void RaisingTheThresholdAboveCurrentUsageStaysSilent()
    {
        using var dir = new TempDir();
        var (engine, provider, timers, _) = Build(dir);
        using var _e = engine;

        var notifications = new List<AppNotification>();
        engine.NotificationRequested += notifications.Add;

        provider.SetSession(75);          // over the 70 default
        engine.Start(timers);
        Assert.Single(notifications);

        engine.SetThresholdPercent(90);
        timers.Poll.Tick();

        Assert.Single(notifications);     // 75 < 90 — no second notification
    }

    /// <summary>
    /// Clamped to the range <see cref="TraySettings.Load"/> accepts, so a caller cannot
    /// install a threshold the settings file would reject and silently reset to the default
    /// on the next launch.
    /// </summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(101, 100)]
    [InlineData(1000, 100)]
    public void ThresholdPercentIsClampedToWhatSettingsWillLoadBack(int requested, int expected)
    {
        using var dir = new TempDir();
        var (engine, _, _, _) = Build(dir);
        using var _e = engine;

        Assert.Equal(expected, engine.SetThresholdPercent(requested));
        Assert.Equal(expected, TraySettings.Load(dir.File("settings.json")).ThresholdPercent);
    }

    [Fact]
    public void ThresholdIsSilentWhenNotificationsAreOff()
    {
        using var dir = new TempDir();
        var (engine, provider, timers, _) = Build(dir);
        using var _e = engine;

        var notifications = new List<AppNotification>();
        engine.NotificationRequested += notifications.Add;
        engine.SetNotifyOnThreshold(false);

        provider.SetSession(99);
        engine.Start(timers);

        Assert.Empty(notifications);
    }

    /// <summary>
    /// A monitoring tool must not die on a bad poll. The previous snapshot survives and the
    /// loop keeps running (the historical behaviour of <c>TrayController.Refresh</c>).
    /// </summary>
    [Fact]
    public void RefreshFailureKeepsThePreviousSnapshotAndKeepsPolling()
    {
        using var dir = new TempDir();
        var (engine, provider, timers, log) = Build(dir);
        using var _e = engine;

        provider.SetSession(33);
        engine.Start(timers);
        Assert.Equal(33, engine.Latest.SessionPercent);

        provider.ThrowOnNext = new InvalidOperationException("provider exploded");
        timers.Poll.Tick();                       // must not throw

        Assert.Equal(33, engine.Latest.SessionPercent);
        Assert.Contains(log.Lines, l => l.StartsWith("refresh FAILED", StringComparison.Ordinal));

        provider.SetSession(44);
        timers.Poll.Tick();
        Assert.Equal(44, engine.Latest.SessionPercent);
    }

    /// <summary>
    /// Warm-up cadence (<see cref="Core.Providers.PollingCadence"/>): poll fast while waiting for
    /// authoritative data early in the run, settle to the normal interval once it arrives.
    /// </summary>
    [Fact]
    public void CadenceStartsFastWithoutDataAndSettlesOnceLive()
    {
        using var dir = new TempDir();
        var (engine, provider, timers, _) = Build(dir);
        using var _e = engine;

        provider.Next = UsageSnapshot.None;
        engine.Start(timers);
        Assert.Equal(TimeSpan.FromSeconds(3), timers.Poll.Interval);

        provider.SetSession(20);
        timers.Poll.Tick();
        Assert.Equal(TimeSpan.FromSeconds(60), timers.Poll.Interval);
    }

    [Fact]
    public void WarmupIntervalNeverExceedsThePollInterval()
    {
        using var dir = new TempDir();
        var provider = new FakeProvider();
        using var engine = new UsageEngine(new UsageEngineOptions
        {
            Clock = new FakeClock(T0),
            Provider = provider,
            // A sub-3 s diagnostic --interval-ms would otherwise make "warming up" slower
            // than steady state.
            PollInterval = TimeSpan.FromSeconds(1),
            RollupDbPath = dir.File("usage.db"),
            WeeklyResetLogPath = dir.File("weekly-resets.json"),
            SettingsPath = dir.File("settings.json"),
        });
        var timers = new FakeTimerFactory();

        provider.Next = UsageSnapshot.None;
        engine.Start(timers);

        Assert.Equal(TimeSpan.FromSeconds(1), timers.Poll.Interval);
    }

    /// <summary>Update schedule (ADR-0009): one check shortly after launch, then recurring.</summary>
    [Fact]
    public void UpdateCheckFiresOnceOnStartupThenOnTheRecurringTimer()
    {
        using var dir = new TempDir();
        var (engine, _, timers, _) = Build(dir);
        using var _e = engine;

        var checks = 0;
        engine.UpdateCheckDue += () => checks++;
        engine.Start(timers);

        Assert.Equal(0, checks);                  // not during startup — it must not race the first refresh

        timers.FirstUpdateCheck.Tick();
        Assert.Equal(1, checks);

        timers.FirstUpdateCheck.Tick();           // one-shot: stopped itself
        Assert.Equal(1, checks);

        timers.RecurringUpdateCheck.Tick();
        timers.RecurringUpdateCheck.Tick();
        Assert.Equal(3, checks);
    }

    [Fact]
    public void DisposeStopsEveryTimer()
    {
        using var dir = new TempDir();
        var (engine, _, timers, _) = Build(dir);

        engine.Start(timers);
        engine.Dispose();

        Assert.All(timers.Created, t => Assert.False(t.IsRunning));
        Assert.All(timers.Created, t => Assert.True(t.Disposed));
    }
}
