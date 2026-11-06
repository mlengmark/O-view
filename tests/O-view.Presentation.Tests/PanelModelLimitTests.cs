using OView.Core.Models;
using OView.Core.Providers.CachedUsage;
using OView.Presentation.Panel;
using OView.Presentation.Text;

namespace OView.Presentation.Tests;

/// <summary>
/// A model's own weekly limit as its own bar, built once for both heads. What these pin is the
/// part neither head may decide: that the row follows the cached block rather than the snapshot
/// that won, and that a reading which no longer describes its window says unknown without the
/// row going away.
/// </summary>
public class PanelModelLimitTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Reset = new(2026, 8, 24, 21, 0, 0, TimeSpan.Zero);

    private static CachedUtilization Cached(TimeSpan age, params ModelLimit[] limits) =>
        new(Now - age, null, null, null)
        {
            ModelLimits = new ModelLimitRead(limits, limits.Length, 0),
        };

    private static CachedUtilization Cached(params ModelLimit[] limits) =>
        Cached(TimeSpan.FromMinutes(3), limits);

    private static ModelLimit Fable(int percent, DateTimeOffset? resets) =>
        new("Fable", new UtilizationBar(percent, resets));

    private static UsageSnapshot Snapshot(DataSource source, int? session, int? weekly) =>
        new(
            source,
            UsageSnapshot.Percent(session),
            UsageSnapshot.Percent(weekly),
            UsageSnapshot.Instant(null),
            UsageSnapshot.Instant(Now),
            UsageSnapshot.Instant(null));

    // ── one bar per row ─────────────────────────────────────────────────────────────

    [Fact]
    public void ARowGivesABarWithItsLabelFigureBandFillAndResetLine()
    {
        var meter = Assert.Single(PanelSections.ModelLimitMeters(Cached(Fable(58, Reset)), Now, Utc));

        Assert.Equal("Weekly · Fable", meter.Label);
        Assert.Equal(58, meter.Percent);
        Assert.Equal("58% used", meter.Value);
        Assert.Equal(UsageLevel.Warning, meter.Band);
        Assert.Equal(58, meter.FillPercent);
        Assert.Equal(PanelText.WeeklyReset(Reset, Now, Utc), meter.ResetLine);
        Assert.Equal(meter.ResetLine, meter.ResetLineWithAction);
        Assert.Null(meter.ResetHint);
        Assert.Null(meter.Chip);
    }

    /// <summary>A model's reset is reported, like the weekly one, so its line has no <c>~</c>.</summary>
    [Fact]
    public void TheResetLineIsNeverMarkedApproximate()
    {
        var meter = Assert.Single(PanelSections.ModelLimitMeters(Cached(Fable(58, Reset)), Now, Utc));

        Assert.DoesNotContain("~", meter.ResetLine);
    }

    [Fact]
    public void EachRowGetsItsOwnBarInTheOrderTheFileListsThem()
    {
        var earlier = Reset.AddDays(-2);
        var cached = Cached(
            new ModelLimit("Fable", new UtilizationBar(10, Reset)),
            new ModelLimit("Saga", new UtilizationBar(91, earlier)));

        var meters = PanelSections.ModelLimitMeters(cached, Now, Utc);

        Assert.Equal(["Weekly · Fable", "Weekly · Saga"], meters.Select(m => m.Label));
        Assert.Equal([UsageLevel.Normal, UsageLevel.Critical], meters.Select(m => m.Band!.Value));
        Assert.Equal(PanelText.WeeklyReset(earlier, Now, Utc), meters[1].ResetLine);
    }

    /// <summary>The row Claude Code really writes, read through the real parser.</summary>
    [Fact]
    public void TheRowAsClaudeCodeCachesItReachesTheBar()
    {
        var fetched = Now.AddMinutes(-3).ToUnixTimeMilliseconds();
        var cached = CachedUtilization.Parse($$$$"""
            {"cachedUsageUtilization": {"fetchedAtMs": {{{{fetched}}}}, "utilization": {"limits": [
              {"kind": "weekly_all", "group": "weekly", "percent": 13,
               "resets_at": "2026-08-24T21:00:00+00:00", "scope": null},
              {"kind": "weekly_scoped", "group": "weekly", "percent": 10,
               "resets_at": "2026-08-24T21:00:00+00:00",
               "scope": {"model": {"id": null, "display_name": "Fable"}, "surface": null}}
            ]}}}
            """);

        var meter = Assert.Single(PanelSections.ModelLimitMeters(cached, Now, Utc));

        Assert.Equal("Weekly · Fable", meter.Label);
        Assert.Equal("10% used", meter.Value);
        Assert.Equal(PanelText.WeeklyReset(Reset, Now, Utc), meter.ResetLine);
    }

    /// <summary>
    /// Both heads add a row per entry and nothing else, so an empty list is the whole of "no
    /// bar and no space", and what keeps every existing golden unchanged.
    /// </summary>
    [Fact]
    public void NoRowGivesNoBar()
    {
        Assert.Empty(PanelSections.ModelLimitMeters(Cached(), Now, Utc));
        Assert.Empty(PanelSections.ModelLimitMeters(null, Now, Utc));
    }

    // ── a reading that no longer describes its window ───────────────────────────────

    [Fact]
    public void APassedResetReadsUnknownWithNoFillAndNoLine()
    {
        var meter = Assert.Single(PanelSections.ModelLimitMeters(Cached(Fable(58, Now.AddHours(-1))), Now, Utc));

        Assert.Equal("Weekly · Fable", meter.Label);
        AssertUnknown(meter);
        Assert.Null(meter.ResetLine);
        Assert.Null(meter.ResetLineWithAction);
    }

    /// <summary>The instant of the reset is already the next window.</summary>
    [Fact]
    public void AResetFallingExactlyNowCountsAsPassed()
    {
        var meter = Assert.Single(PanelSections.ModelLimitMeters(Cached(Fable(58, Now)), Now, Utc));

        AssertUnknown(meter);
        Assert.Null(meter.ResetLine);
    }

    /// <summary>
    /// An aged zero is a lower bound that looks like a measurement, so it reads unknown, but its
    /// reset is still ahead and still true, so the line stays.
    /// </summary>
    [Fact]
    public void AnAgedZeroReadsUnknownWithNoFillAndKeepsItsLine()
    {
        var meter = Assert.Single(
            PanelSections.ModelLimitMeters(Cached(TimeSpan.FromHours(1), Fable(0, Reset)), Now, Utc));

        AssertUnknown(meter);
        Assert.Equal(PanelText.WeeklyReset(Reset, Now, Utc), meter.ResetLine);
    }

    /// <summary>The contrast that shows the rule above is about age: a fresh zero is a reading.</summary>
    [Fact]
    public void AFreshZeroIsAReading()
    {
        var meter = Assert.Single(PanelSections.ModelLimitMeters(Cached(Fable(0, Reset)), Now, Utc));

        Assert.Equal("0% used", meter.Value);
        Assert.Equal(0, meter.FillPercent);
    }

    /// <summary>An aged non-zero is still a lower bound worth showing, as on the two meters.</summary>
    [Fact]
    public void AnAgedNonZeroKeepsItsFigure()
    {
        var meter = Assert.Single(
            PanelSections.ModelLimitMeters(Cached(TimeSpan.FromHours(6), Fable(58, Reset)), Now, Utc));

        Assert.Equal("58% used", meter.Value);
    }

    /// <summary>A row may carry no reset at all: there is then nothing to state, and no line.</summary>
    [Fact]
    public void ARowWithNoResetHasAFigureAndNoLine()
    {
        var meter = Assert.Single(PanelSections.ModelLimitMeters(Cached(Fable(58, null)), Now, Utc));

        Assert.Equal("58% used", meter.Value);
        Assert.Null(meter.ResetLine);
    }

    /// <summary>A figure outside the range is stated as read and drawn within the track.</summary>
    [Fact]
    public void AFigureOverOneHundredFillsTheTrackAndNoMore()
    {
        var meter = Assert.Single(PanelSections.ModelLimitMeters(Cached(Fable(104, Reset)), Now, Utc));

        Assert.Equal("104% used", meter.Value);
        Assert.Equal(100, meter.FillPercent);
    }

    private static void AssertUnknown(PanelMeterView meter)
    {
        Assert.Null(meter.Percent);
        Assert.Equal(PanelBanner.UnknownGauge, meter.Value);
        Assert.Null(meter.Band);
        Assert.Null(meter.FillPercent);
    }

    // ── whichever snapshot won ──────────────────────────────────────────────────────

    public static TheoryData<string, UsageSnapshot, bool> Winners => new()
    {
        { "Claude Code, live", Snapshot(DataSource.Live, 25, 3), true },
        { "Claude Desktop, no resets and no model rows", Snapshot(DataSource.Live, 40, 12), true },
        { "a stale reading", Snapshot(DataSource.Stale, 40, 12), true },
        { "a local estimate", Snapshot(DataSource.Estimate, null, null), false },
        { "no data at all", UsageSnapshot.None, false },
    };

    /// <summary>
    /// The same block beside every kind of winner. Each case also reads the weekly meter, which
    /// does follow the winner, so the snapshots here are ones that really change a meter: on the
    /// estimate and no-data rows Weekly reads unknown while the model's bar keeps its figure.
    /// </summary>
    [Theory]
    [MemberData(nameof(Winners))]
    public void TheSameBlockGivesTheSameBarWhicheverSnapshotWon(
        string winner, UsageSnapshot snapshot, bool weeklyHasAFigure)
    {
        var cached = Cached(Fable(58, Reset));
        var expected = new PanelMeterView(
            "Weekly · Fable", 58, "58% used", UsageLevel.Warning, 58,
            PanelText.WeeklyReset(Reset, Now, Utc), PanelText.WeeklyReset(Reset, Now, Utc), null, null);

        var weekly = PanelSections.WeeklyMeter(snapshot, null, null, null, Now, Utc);
        var meter = Assert.Single(PanelSections.ModelLimitMeters(cached, Now, Utc));

        Assert.True(weeklyHasAFigure == weekly.Percent is not null, winner);
        Assert.Equal(expected, meter);
    }

    /// <summary>
    /// What makes the test above hold for every snapshot rather than the five it lists: the
    /// builder is not handed one, nor the banner that is derived from one. A later parameter
    /// that let a snapshot in would bring back the bar that blinks.
    /// </summary>
    [Fact]
    public void TheBarIsBuiltFromTheCachedBlockAloneAndIsNeverHandedASnapshot()
    {
        var parameters = typeof(PanelSections).GetMethod(nameof(PanelSections.ModelLimitMeters))!
            .GetParameters()
            .Select(p => p.ParameterType);

        Assert.Equal([typeof(CachedUtilization), typeof(DateTimeOffset), typeof(TimeZoneInfo)], parameters);
    }
}
