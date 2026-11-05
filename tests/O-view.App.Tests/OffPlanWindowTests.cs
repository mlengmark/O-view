using OView.Core.Models;
using OView.Core.Pricing;
using OView.Core.Providers.CachedUsage;
using OView.Core.Providers.Jsonl;
using OView.Core.Storage;
using OView.TestSupport;

namespace OView.App.Tests;

/// <summary>
/// The engine end of issue #268: what the off-plan comparison sees during the first quarter of
/// an hour of a five-hour window, when plan history holds exactly one sample of it.
///
/// <para>The reported panel, reconstructed from the machine that produced it. Claude Desktop
/// samples every 15 minutes, the divergence window is anchored on the first sample of the new
/// window, and the user had run 56.6K output tokens since, so the series was <c>[5]</c>, the
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
        Assert.Null(stats.EstOffPlanValue.Value);
    }

    /// <summary>
    /// The half that makes the reading right rather than merely quiet. Claude Code's cached
    /// figure was six minutes newer than Desktop's and O-view was already displaying it in the
    /// gauge above the banner; folded into the series it supplies the second point, and the
    /// window reads as what it was, 19 points of ordinary plan usage.
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
    /// volume, but Claude Code agrees the meter has not moved, two points, both flat, and the
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
    /// the rise, leaving one sample inside it.
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
    /// 56.6K output tokens inside the window, past <c>DefaultMinOutputTokens</c>, which is
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

    /// <summary>
    /// Claude Code's cached block, read a minute ago, newer than Desktop's sample. Its reset is
    /// five hours after the window's first sample, which is where Claude Code and plan history
    /// agree the window opened, so the reported start places the window where Desktop did.
    /// </summary>
    private static CachedUtilization Block(int fiveHourPercent) =>
        new(T0.AddMinutes(-1), null, new UtilizationBar(fiveHourPercent, NewestSample.AddHours(5)), null);

    // ── the window starts at Claude Code's reported reset (issue #275) ──────────────

    /// <summary>
    /// 2026-09-07 14:58Z, fourteen minutes after Desktop resumed. Claude Code reports a reset at
    /// 19:40Z, so the window began at 14:40Z; the drop Desktop last saw was 09-05 13:16Z.
    /// </summary>
    private static readonly DateTimeOffset Sept7 = new(2026, 9, 7, 14, 58, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Sept7Reset = new(2026, 9, 7, 19, 40, 0, TimeSpan.Zero);

    /// <summary>
    /// Heavy work that morning, before the window the reset describes, and a little after it.
    /// Against the window plan history alone could place, five hours back, the 60,922 tokens
    /// from 11:58Z read as part of the window; placed by the reset they are not.
    ///
    /// <para>Claude Code's last cached figure here is a zero twenty-four minutes old, which is
    /// discarded as a reading while its reset is still the best evidence there is, the two
    /// have to be read apart.</para>
    /// </summary>
    [Fact]
    public void AnAgedZeroIsDroppedAsAReadingButItsResetStillPlacesTheWindow()
    {
        using var dir = new TempDir();
        var stats = BuildSept7Statistics(dir, Block274(0, fetched: Sept7.AddMinutes(-24), Sept7Reset));

        Assert.Equal(2_000, stats.Divergence?.OutputTokensInWindow);
        Assert.Equal(DivergenceState.InsufficientActivity, stats.Divergence?.State);
        Assert.False(stats.IsOffPlan);
    }

    /// <summary>A reset already past is ignored, so the window is the one plan history gives.</summary>
    [Fact]
    public void AReportedResetInThePastIsNotUsed()
    {
        using var dir = new TempDir();
        var stats = BuildSept7Statistics(dir, Block274(0, fetched: Sept7.AddMinutes(-24), Sept7.AddMinutes(-1)));

        Assert.Equal(62_922, stats.Divergence?.OutputTokensInWindow);
    }

    /// <summary>
    /// With no plan history at all the reported start is still the window's, but the series is
    /// empty, so a single reported reading cannot be turned into a verdict.
    /// </summary>
    [Fact]
    public void WithNoPlanHistory_TheReportedStartPlacesTheWindowButNoVerdictFollows()
    {
        using var dir = new TempDir();
        var stats = BuildSept7Statistics(
            dir, Block274(40, fetched: Sept7.AddMinutes(-2), Sept7Reset), withPlanHistory: false);

        Assert.Equal(2_000, stats.Divergence?.OutputTokensInWindow);
        Assert.False(stats.IsOffPlan);
    }

    private static PanelStatistics BuildSept7Statistics(
        TempDir dir, CachedUtilization block, bool withPlanHistory = true)
    {
        var dbPath = dir.File("usage.db");
        using (var store = new RollupStore(dbPath))
        {
            store.Ingest([
                new TranscriptRecord(
                    "req-morning", Sept7.AddHours(-3), "claude-opus-5",
                    new TokenSplit(0, 0, 0, 0, 0, 60_922), UsageModifiers.Standard),
                new TranscriptRecord(
                    "req-after", Sept7.AddMinutes(-10), "claude-opus-5",
                    new TokenSplit(0, 0, 0, 0, 0, 2_000), UsageModifiers.Standard),
            ]);
        }

        var path = dir.File("plan-usage-history.json");
        var samples = withPlanHistory
            ? new[]
            {
                (At: new DateTimeOffset(2026, 9, 5, 12, 31, 0, TimeSpan.Zero), Fh: 25),
                (At: new DateTimeOffset(2026, 9, 5, 13, 16, 0, TimeSpan.Zero), Fh: 0),
                (At: new DateTimeOffset(2026, 9, 5, 17, 31, 0, TimeSpan.Zero), Fh: 0),
                (At: new DateTimeOffset(2026, 9, 7, 14, 44, 0, TimeSpan.Zero), Fh: 0),
            }
            : [];
        var json = string.Join(",", samples.Select(s =>
            $"{{\"t\":{s.At.ToUnixTimeMilliseconds()},\"org\":\"org-1\",\"u\":{{\"fh\":{s.Fh},\"sd\":8}}}}"));
        File.WriteAllText(path, $"{{\"version\":2,\"samples\":[{json}]}}");

        using var engine = new UsageEngine(new UsageEngineOptions
        {
            Clock = new FakeClock(Sept7),
            Provider = new FakeProvider(),
            RollupDbPath = dbPath,
            WeeklyResetAnchorPath = dir.File("weekly-resets.json"),
            SettingsPath = dir.File("settings.json"),
            PlanHistoryPath = path,
            CachedUtilization = new CachedUtilizationProvider(() => block),
        });
        return engine.BuildStatistics();
    }

    private static CachedUtilization Block274(int fiveHourPercent, DateTimeOffset fetched, DateTimeOffset resetsAt) =>
        new(fetched, null, new UtilizationBar(fiveHourPercent, resetsAt), null);

    // ── the log says when the two sources disagree (issue #275) ─────────────────────

    /// <summary>
    /// A window plan history located between 12:00 and 12:15. Reset 17:10 puts Claude Code's
    /// start at 12:10, inside it; reset 16:50 puts it at 11:50, before a 50% reading.
    /// </summary>
    private static readonly DateTimeOffset AgreeingReset = new(2026, 9, 1, 17, 10, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset DisagreeingReset = new(2026, 9, 1, 16, 50, 0, TimeSpan.Zero);

    private static UsageEngine EngineWithALocatedWindow(TempDir dir, Func<DateTimeOffset> reset, IAppLog log)
    {
        var path = dir.File("plan-usage-history.json");
        var samples = new[]
        {
            (At: new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero), Fh: 50),
            (At: new DateTimeOffset(2026, 9, 1, 12, 15, 0, TimeSpan.Zero), Fh: 3),
            (At: NewestSample, Fh: 20),
        };
        File.WriteAllText(path, $"{{\"version\":2,\"samples\":[{string.Join(",", samples.Select(s =>
            $"{{\"t\":{s.At.ToUnixTimeMilliseconds()},\"org\":\"org-1\",\"u\":{{\"fh\":{s.Fh},\"sd\":8}}}}"))}]}}");

        return new UsageEngine(new UsageEngineOptions
        {
            Clock = new FakeClock(T0),
            Provider = new FakeProvider(),
            RollupDbPath = dir.File("usage.db"),
            WeeklyResetAnchorPath = dir.File("weekly-resets.json"),
            SettingsPath = dir.File("settings.json"),
            PlanHistoryPath = path,
            CachedUtilization = new CachedUtilizationProvider(() => Block274(20, T0.AddMinutes(-1), reset())),
            Log = log,
        });
    }

    private static string[] Disagreements(ListLog log) =>
        [.. log.Lines.Where(l => l.Contains("session window sources", StringComparison.Ordinal))];

    [Fact]
    public void ADisagreement_IsLoggedOnceNotOncePerPoll()
    {
        using var dir = new TempDir();
        var log = new ListLog();
        using var engine = EngineWithALocatedWindow(dir, () => DisagreeingReset, log);

        engine.BuildStatistics();
        engine.BuildStatistics();
        engine.BuildStatistics();

        var lines = Disagreements(log);
        var line = Assert.Single(lines);
        Assert.Contains("disagree", line, StringComparison.Ordinal);
        Assert.Contains("2026-09-01 11:50:00Z", line, StringComparison.Ordinal);
        Assert.Contains("2026-09-01 12:00:00Z and 2026-09-01 12:15:00Z", line, StringComparison.Ordinal);
    }

    [Fact]
    public void AgreementReturning_IsLoggedOnceMore()
    {
        using var dir = new TempDir();
        var log = new ListLog();
        var reset = DisagreeingReset;
        using var engine = EngineWithALocatedWindow(dir, () => reset, log);

        engine.BuildStatistics();
        reset = AgreeingReset;
        engine.BuildStatistics();
        engine.BuildStatistics();

        var lines = Disagreements(log);
        Assert.Equal(2, lines.Length);
        Assert.Contains("agree again", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void AgreeingSources_LogNothing()
    {
        using var dir = new TempDir();
        var log = new ListLog();
        using var engine = EngineWithALocatedWindow(dir, () => AgreeingReset, log);

        engine.BuildStatistics();
        engine.BuildStatistics();

        Assert.Empty(Disagreements(log));
    }

    /// <summary>
    /// The morning issue #274 was reported on: plan history locates no window, only a fallback,
    /// and the reported start differs from it entirely. That is not a disagreement.
    /// </summary>
    [Fact]
    public void AFallbackWindow_IsNeverALoggedDisagreement()
    {
        using var dir = new TempDir();
        var log = new ListLog();
        var db = dir.File("usage.db");
        using (new RollupStore(db)) { }
        var path = dir.File("plan-usage-history.json");
        File.WriteAllText(path, $"{{\"version\":2,\"samples\":[{{\"t\":{Sept7.AddMinutes(-14).ToUnixTimeMilliseconds()},\"org\":\"org-1\",\"u\":{{\"fh\":0,\"sd\":8}}}}]}}");

        using var engine = new UsageEngine(new UsageEngineOptions
        {
            Clock = new FakeClock(Sept7),
            Provider = new FakeProvider(),
            RollupDbPath = db,
            WeeklyResetAnchorPath = dir.File("weekly-resets.json"),
            SettingsPath = dir.File("settings.json"),
            PlanHistoryPath = path,
            CachedUtilization = new CachedUtilizationProvider(() => Block274(0, Sept7.AddMinutes(-24), Sept7Reset)),
            Log = log,
        });

        engine.BuildStatistics();

        Assert.Empty(Disagreements(log));
    }
}
