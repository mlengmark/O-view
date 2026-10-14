using OView.Core.Models;
using OView.Core.Providers.CachedUsage;
using OView.Core.Providers.PlanHistory;

namespace OView.App.Tests;

/// <summary>
/// Proves the two interfaces between a skin and the shell (OVIAA-64, docs/architecture.md)
/// each stand on their own: a fake skin against <see cref="IShellToSkin"/>, and a fake shell
/// against <see cref="ISkinToShell"/>, neither touching WPF, Avalonia or a real
/// <see cref="UsageEngine"/>. No skin is wired to either interface yet — this only names the
/// shape both already rely on through direct calls and engine events.
/// </summary>
public class ShellInterfaceTests
{
    /// <summary>A skin that only records what the shell pushed at it.</summary>
    private sealed class FakeSkin : IShellToSkin
    {
        public List<UsageSnapshot> Snapshots { get; } = [];
        public List<AppNotification> Notifications { get; } = [];
        public int UpdateChecksDue { get; private set; }
        public int RateChecksDue { get; private set; }

        public void ShowSnapshot(UsageSnapshot snapshot) => Snapshots.Add(snapshot);
        public void ShowNotification(AppNotification notification) => Notifications.Add(notification);
        public void UpdateCheckDue() => UpdateChecksDue++;
        public void RateCheckDue() => RateChecksDue++;
    }

    /// <summary>A shell that answers every query itself — no rollup store, no clock, no disk.</summary>
    private sealed class FakeShell : ISkinToShell
    {
        public UsageSnapshot Latest { get; init; } = UsageSnapshot.None;
        public TraySettings Settings { get; init; } = new();
        public CachedUtilization? CachedUsage { get; init; }
        public bool CanRefreshUsageCache { get; init; }
        public string? UsageRefreshBlocked { get; init; }
        public int UsageRefreshAttempts { get; init; }
        public DateTimeOffset? WeeklyResetConflict { get; init; }

        public int RefreshCalls { get; private set; }
        public DateTimeOffset? NoticedConflict { get; private set; }

        public PanelStatistics BuildStatistics() => new(0, null, 0, null, 0, 31, [], 0, null);
        public void Refresh() => RefreshCalls++;
        public void RefreshUsageCache(bool force = false) { }
        public bool ResumeUsageRefresh() => true;
        public bool SetNotifyOnThreshold(bool enable) => enable;
        public bool SetUpdateAutomatically(bool enable) => enable;
        public int SetThresholdPercent(int percent) => percent;
        public ManualWeeklyReset? SetWeeklyReset(ManualWeeklyReset? reset) => reset;
        public void MarkWeeklyResetConflictNoticed(DateTimeOffset conflict) => NoticedConflict = conflict;
        public bool IsWeeklyResetConflictUnseen(DateTimeOffset conflict) => NoticedConflict != conflict;
    }

    [Fact]
    public void FakeSkinImplementsIShellToSkin()
    {
        IShellToSkin skin = new FakeSkin();
        var notification = new AppNotification("Title", "Message");

        skin.ShowSnapshot(UsageSnapshot.None);
        skin.ShowNotification(notification);
        skin.UpdateCheckDue();
        skin.RateCheckDue();

        var fake = (FakeSkin)skin;
        Assert.Single(fake.Snapshots);
        Assert.Single(fake.Notifications);
        Assert.Equal(1, fake.UpdateChecksDue);
        Assert.Equal(1, fake.RateChecksDue);
    }

    [Fact]
    public void FakeShellImplementsISkinToShell()
    {
        var shell = new FakeShell();
        ISkinToShell seam = shell;

        seam.Refresh();
        seam.RefreshUsageCache(force: true);

        Assert.Equal(1, shell.RefreshCalls);
        Assert.True(seam.ResumeUsageRefresh());
        Assert.True(seam.SetNotifyOnThreshold(true));
        Assert.True(seam.SetUpdateAutomatically(true));
        Assert.Equal(80, seam.SetThresholdPercent(80));

        var reset = new ManualWeeklyReset(DayOfWeek.Monday, new TimeOnly(9, 0));
        Assert.Equal(reset, seam.SetWeeklyReset(reset));

        var conflict = DateTimeOffset.UnixEpoch;
        Assert.True(seam.IsWeeklyResetConflictUnseen(conflict));
        seam.MarkWeeklyResetConflictNoticed(conflict);
        Assert.False(seam.IsWeeklyResetConflictUnseen(conflict));

        Assert.NotNull(seam.BuildStatistics());
        Assert.Equal(UsageSnapshot.None, seam.Latest);
        Assert.Equal(new TraySettings(), seam.Settings);
    }

    /// <summary>The engine itself is a valid <see cref="ISkinToShell"/> — no change to any
    /// member's behaviour, just a name a skin could depend on instead.</summary>
    [Fact]
    public void UsageEngineIsAnISkinToShell()
    {
        using var dir = new TempDir();
        var options = new UsageEngineOptions
        {
            Clock = new FakeClock(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero)),
            Provider = new FakeProvider(),
            RollupDbPath = dir.File("usage.db"),
            WeeklyResetAnchorPath = dir.File("weekly-resets.json"),
            SettingsPath = dir.File("settings.json"),
        };
        using var engine = new UsageEngine(options);

        ISkinToShell seam = engine;

        Assert.Equal(engine.Latest, seam.Latest);
        Assert.Equal(engine.Settings, seam.Settings);
        Assert.Equal(engine.CanRefreshUsageCache, seam.CanRefreshUsageCache);
        Assert.Equal(engine.BuildStatistics().WindowDays, seam.BuildStatistics().WindowDays);
    }
}
