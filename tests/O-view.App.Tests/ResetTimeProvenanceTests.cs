using OView.Core.Models;
using OView.Core.Providers.CachedUsage;
using OView.Core.Providers.PlanHistory;

namespace OView.App.Tests;

/// <summary>
/// <see cref="WeeklyResetCoordinator.WithWeeklyReset"/> fills the weekly reset from one of two
/// places, and the two carry different claims about where the instant came from: a stored
/// anchor came from Claude Code, a typed entry came from a person (OVIAA-81). Both are treated
/// as exact (no uncertainty field survives on the weekly row at all), so both must claim
/// <see cref="UsageValueStatus.Real"/> — but before this test existed, neither path set the
/// status at all, so a snapshot that started <see cref="UsageValueStatus.Unavailable"/> (the
/// ordinary case: nothing has read a weekly reset yet) kept that flag even once a reset time
/// had been written onto it by this method. <see cref="AnAnchorFillsTheGap_AndClaimsItReal"/>
/// and <see cref="ATypedEntryFillsTheGap_AndClaimsItReal"/> are that regression test; the fix
/// is the <c>WeeklyResetAtUtcStatus</c> set in <see cref="WeeklyResetCoordinator.WithWeeklyReset"/>'s
/// closing <c>with</c> expression.
/// </summary>
public class ResetTimeProvenanceTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Directory.CreateTempSubdirectory("oview-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>
    /// A snapshot with data but no weekly reset yet — <see cref="UsageSnapshot.WeeklyResetAtUtcValue.Value"/>
    /// null, so its status defaults to <see cref="UsageValueStatus.Unavailable"/>. This is what
    /// every snapshot looks like before <see cref="WeeklyResetCoordinator.WithWeeklyReset"/> runs,
    /// and it is the carried-forward value the bug left behind.
    /// </summary>
    private static UsageSnapshot NoWeeklyResetYet { get; } =
        new(
            DataSource.Live,
            UsageSnapshot.Percent(10),
            UsageSnapshot.Percent(20),
            UsageSnapshot.Instant(null),
            UsageSnapshot.Instant(T0),
            UsageSnapshot.Instant(null));

    private WeeklyResetCoordinator Build(
        Func<CachedUtilization?> cachedUtilizationSource, TraySettings settings) =>
        new(
            Path.Combine(_dir, "weekly-reset.json"),
            cachedUtilizationSource,
            new FakeProvider(),
            orgUuid: null,
            log: null,
            getSettings: () => settings,
            saveSettings: _ => { });

    /// <summary>
    /// A weekly reset recovered from the stored anchor came from Claude Code — it is what the
    /// method harvests from <c>cachedUsageUtilization</c> before anything else runs, so it is
    /// read directly off the source rather than transcribed by a person.
    /// </summary>
    [Fact]
    public void AnAnchorFillsTheGap_AndClaimsItReal()
    {
        var reported = T0.AddDays(3);
        var coordinator = Build(
            () => new CachedUtilization(T0, "acct", null, new UtilizationBar(79, reported)),
            new TraySettings());

        var filled = coordinator.WithWeeklyReset(NoWeeklyResetYet, T0);

        Assert.Equal(reported, filled.WeeklyResetAtUtcValue.Value);
        Assert.Equal(UsageValueStatus.Real, filled.WeeklyResetAtUtcValue.Status);
    }

    /// <summary>
    /// A weekly reset built from the typed entry came from a person reading Claude's own
    /// Settings → Usage screen (issue #186) — no anchor has ever been stored, so this only ever
    /// runs when there is nothing from Claude Code to prefer instead.
    /// </summary>
    [Fact]
    public void ATypedEntryFillsTheGap_AndClaimsItReal()
    {
        var entry = new ManualWeeklyReset(DayOfWeek.Monday, new TimeOnly(22, 59));
        var coordinator = Build(
            cachedUtilizationSource: () => null,
            new TraySettings(WeeklyResetDay: entry.DayText, WeeklyResetTime: entry.TimeText));

        var filled = coordinator.WithWeeklyReset(NoWeeklyResetYet, T0);

        Assert.Equal(entry.NextAfter(T0, TimeZoneInfo.Local), filled.WeeklyResetAtUtcValue.Value);
        Assert.Equal(UsageValueStatus.Real, filled.WeeklyResetAtUtcValue.Status);
    }

    /// <summary>
    /// Genuinely nothing to fill from either source: the snapshot comes back untouched, so it
    /// still reads <see cref="UsageValueStatus.Unavailable"/> rather than the fix above making
    /// every weekly row claim <c>Real</c> regardless of whether there is a value behind it.
    /// </summary>
    [Fact]
    public void WithNeitherSource_TheGapStaysUnavailable()
    {
        var coordinator = Build(cachedUtilizationSource: () => null, new TraySettings());

        var filled = coordinator.WithWeeklyReset(NoWeeklyResetYet, T0);

        Assert.Null(filled.WeeklyResetAtUtcValue.Value);
        Assert.Equal(UsageValueStatus.Unavailable, filled.WeeklyResetAtUtcValue.Status);
    }
}
