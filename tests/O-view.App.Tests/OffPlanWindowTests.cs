using OView.Core.Models;
using OView.Core.Pricing;
using OView.Core.Providers.CachedUsage;
using OView.Core.Providers.Jsonl;
using OView.Core.Storage;

namespace OView.App.Tests;

/// <summary>
/// The engine end of issue #268: what the off-plan comparison sees during the first quarter of
/// an hour of a five-hour window, when plan history holds exactly one sample of it.
///
/// <para>The reported panel, reconstructed from the machine that produced it. Claude Desktop
/// samples every 15 minutes, the divergence window is anchored on the first sample of the new
/// window, and the user had run 56.6K output tokens since — so the series was <c>[5]</c>, the
/// rise was <c>5 - 5</c>, and the banner announced that a meter which had in fact gone 5% → 24%
/// had not moved.</para>
/// </summary>
public class OffPlanWindowTests
{
    /// <summary>16:13 local on the reporting machine, the instant the panel was captured.</summary>
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 14, 13, 0, TimeSpan.Zero);

    /// <summary>The newest plan-history sample: six minutes old, well inside the Live bound.</summary>
    private static readonly DateTimeOffset NewestSample = T0.AddMinutes(-6);

    [Fact]
    public void AOneSampleWindow_WithNoOtherReading_IsNotCalledOffPlan()
    {
        using var dir = new TempDir();
        var stats = BuildStatistics(dir, reportedPercent: null);

        Assert.Equal(DivergenceState.RiseNotMeasurable, stats.Divergence?.State);
        Assert.False(stats.IsOffPlan);
        Assert.Null(stats.EstOffPlanUsd);
    }

    /// <summary>
    /// The half that makes the reading right rather than merely quiet. Claude Code's cached
    /// figure was six minutes newer than Desktop's and O-view was already displaying it in the
    /// gauge above the banner; folded into the series it supplies the second point, and the
    /// window reads as what it was — 19 points of ordinary plan usage.
    /// </summary>
    [Fact]
    public void TheReportedReading_SuppliesTheSecondPoint()
    {
        using var dir = new TempDir();
        var stats = BuildStatistics(dir, reportedPercent: 24);

        Assert.Equal(DivergenceState.Consistent, stats.Divergence?.State);
        Assert.Equal(19, stats.Divergence?.PlanRisePoints);
        Assert.False(stats.IsOffPlan);
    }

    /// <summary>
    /// The detector must not have been talked out of its job. Same one-sample window, same
    /// volume, but Claude Code agrees the meter has not moved — two points, both flat, and the
    /// verdict stands.
    /// </summary>
    [Fact]
    public void AReportedReadingThatAgreesTheMeterIsFlat_StillDiverges()
    {
        using var dir = new TempDir();
        var stats = BuildStatistics(dir, reportedPercent: 5);

        Assert.Equal(DivergenceState.Diverging, stats.Divergence?.State);
        Assert.True(stats.IsOffPlan);
    }

    /// <summary>
    /// One poll of an engine standing where the reported panel stood: a window opened minutes
    /// ago with one plan-history sample in it, and 56.6K output tokens since.
    /// </summary>
    private static PanelStatistics BuildStatistics(TempDir dir, int? reportedPercent)
    {
        var dbPath = dir.File("usage.db");
        SeedHeavyWindow(dbPath);

        var options = new UsageEngineOptions
        {
            Clock = new FakeClock(T0),
            Provider = new FakeProvider(),
            RollupDbPath = dbPath,
            WeeklyResetAnchorPath = dir.File("weekly-resets.json"),
            SettingsPath = dir.File("settings.json"),
            PlanHistoryPath = WritePlanHistory(dir),

            // Named explicitly in both directions. A test that supplies its own Provider gets
            // an inert one by default (issue #212), so "no reported reading" is a state this
            // fixture has to describe rather than inherit.
            CachedUtilization = new CachedUtilizationProvider(
                () => reportedPercent is { } percent ? Block(percent) : null),
        };

        using var engine = new UsageEngine(options);
        return engine.BuildStatistics();
    }

    /// <summary>
    /// The window that reset while Desktop was closed: a run of zeros, then first use. That
    /// shape is what <c>ResetDetector</c> reads as a boundary, and it puts the window start at
    /// the rise — leaving one sample inside it.
    /// </summary>
    private static string WritePlanHistory(TempDir dir)
    {
        var path = dir.File("plan-usage-history.json");
        var samples = new[]
        {
            (At: NewestSample.AddMinutes(-30), Fh: 0),
            (At: NewestSample.AddMinutes(-15), Fh: 0),
            (At: NewestSample, Fh: 5),
        };

        var json = string.Join(",", samples.Select(s =>
            $"{{\"t\":{s.At.ToUnixTimeMilliseconds()},\"org\":\"org-1\",\"u\":{{\"fh\":{s.Fh},\"sd\":8}}}}"));

        File.WriteAllText(path, $"{{\"version\":2,\"samples\":[{json}]}}");
        return path;
    }

    /// <summary>
    /// 56.6K output tokens inside the window — past <c>DefaultMinOutputTokens</c>, which is
    /// what made the one-sample series worth an opinion at all.
    /// </summary>
    private static void SeedHeavyWindow(string dbPath)
    {
        using var store = new RollupStore(dbPath);
        store.Ingest([
            new TranscriptRecord(
                "req-268", NewestSample.AddMinutes(2), "claude-opus-5",
                new TokenSplit(0, 0, 0, 0, 0, 56_600), UsageModifiers.Standard),
        ]);
    }

    /// <summary>Claude Code's cached block, read a minute ago — newer than Desktop's sample.</summary>
    private static CachedUtilization Block(int fiveHourPercent) =>
        new(T0.AddMinutes(-1), null, new UtilizationBar(fiveHourPercent, T0.AddHours(4)), null);
}
