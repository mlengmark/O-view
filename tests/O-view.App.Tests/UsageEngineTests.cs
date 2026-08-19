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

    private static (UsageEngine Engine, FakeProvider Provider, FakeTimerFactory Timers, ListLog Log)
        Build(TempDir dir, Action<UsageEngineOptions>? tweak = null)
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
        tweak?.Invoke(options);
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
