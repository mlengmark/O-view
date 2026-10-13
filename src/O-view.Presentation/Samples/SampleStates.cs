using System.Globalization;
using OView.Core.Models;
using OView.Core.Providers.CachedUsage;
using OView.Core.Providers.Jsonl;
using OView.Core.Providers.PlanHistory;

namespace OView.Presentation.Samples;

/// <summary>
/// One state of the app, as a panel would be asked to show it. Every head renders every
/// state, so the Windows and Linux renders of the same name can be compared side by side.
/// </summary>
public sealed record SampleState(
    string Name,
    string Shows,
    UsageSnapshot Snapshot,
    PanelStatistics Stats,
    ClaudeAccount? Account,
    TranscriptScopeReport? Scope = null,
    PlanHistoryReport? Plan = null,
    CachedUtilization? CachedUsage = null,
    BoostNotices? Boost = null);

/// <summary>
/// The shared sample states both heads render into golden images (<c>tools/check.ps1</c>).
///
/// <para><b>Deterministic by construction.</b> A golden image is only useful if the same
/// code renders the same pixels on every machine, so nothing here reads the real clock, the
/// real time zone or the real file system: renders use <see cref="Now"/> and
/// <see cref="Zone"/>, and every path is a fixed placeholder that also keeps this machine's
/// profile out of committed images.</para>
///
/// <para>The data is constructed rather than read from a store because the interesting
/// states are the ones no single machine shows: no local transcripts, a CLI-only setup, an
/// exhausted plan meter, a live promo, an unpriced model. A state nothing renders is how the
/// no-data banner spent its life saying the wrong thing (issues #58, #170).</para>
/// </summary>
public static class SampleStates
{
    /// <summary>The instant every sample is rendered at.</summary>
    public static readonly DateTimeOffset Now = new(2026, 7, 28, 10, 47, 0, TimeSpan.Zero);

    /// <summary>The zone every sample is rendered in, so a UTC CI runner and a CET desktop agree.</summary>
    public static readonly TimeZoneInfo Zone = TimeZoneInfo.Utc;

    private static readonly DateOnly Today = DateOnly.FromDateTime(Now.UtcDateTime);

    private static readonly ClaudeAccount Account = new("Sample User", "sample@example.com", "claude_pro", "sample-org");

    // The weekly reset a real machine observed: 06:28:57Z, a quarter of the way into its
    // day column rather than on the edge, which is where gridline placement goes wrong.
    private static readonly DateTimeOffset NextWeeklyReset = new(2026, 8, 4, 6, 28, 57, TimeSpan.Zero);

    private static readonly UsageSnapshot Live = new(DataSource.Live, 25, 3,
        Now.AddHours(3), Now, NextWeeklyReset, TimeSpan.FromDays(7));

    private const string ProjectsRoot = @"C:\Users\<user>\.claude\projects";
    private const string CoworkRoot = @"C:\Users\<user>\AppData\Roaming\Claude\local-agent-mode-sessions";
    private const string PlanFile = @"C:\Users\<user>\AppData\Roaming\Claude\plan-usage-history.json";

    /// <summary>31 days with visible variation, the first 21 before install (10 of 31 recorded).</summary>
    private static readonly IReadOnlyList<DayUsage> Series = BuildSeries(recordedDays: 10);

    /// <summary>
    /// The token tiles headline OUTPUT and the Est. tiles price EVERYTHING (issue #253), so
    /// the two pairs are built from different measures on purpose. The compositions use the
    /// proportions measured on real transcripts (~89% cache reads, issue #169), and their
    /// Output matches the token tiles exactly: a sample that contradicts itself is the one
    /// thing these images exist to catch.
    /// </summary>
    private static readonly PanelStatistics Stats = new(
        TokensToday: 128_600, EstTodayUsd: 9.36m,
        Tokens31Days: 6_771_000, Est31DaysUsd: 492.52m,
        RecordedDays: 10, WindowDays: 31,
        DailySeries: Series,
        CreditTokens31Days: 395_000, EstCredit31DaysUsd: 92.75m)
    {
        CompositionToday = new TokenComposition(
            Input: 1_400, CacheCreation: 1_270_000, CacheRead: 11_300_000, Output: 128_600),
        Composition31Days = new TokenComposition(
            Input: 75_000, CacheCreation: 68_460_000, CacheRead: 609_294_000, Output: 6_771_000),
    };

    private static readonly TranscriptScopeReport BothSources =
        TranscriptScopeReport.Inspect(ProjectsRoot, [CoworkRoot]) with
        {
            Sources =
            [
                new TranscriptSource("Claude Code", [ProjectsRoot], 42, 6_700_000, Now),
                new TranscriptSource("Cowork", [CoworkRoot], 3, 220_000, Now),
            ],
        };

    /// <summary>All the states, in the order the goldens are listed.</summary>
    public static IReadOnlyList<SampleState> All { get; } = Build();

    private static List<SampleState> Build()
    {
        var offPlanStats = Stats with
        {
            Divergence = new DivergenceResult(DivergenceState.PlanLimitReached, 69_091, 0),
            EstOffPlanUsd = 92.75m,
        };
        var offPlanSnapshot = Live with { SessionPercent = 100 };

        return
        [
            new("typical",
                "Live meters, partial history (10 of 31 days), both transcript sources",
                Live, Stats, Account, BothSources),

            new("weekly-reset-unknown",
                "No weekly reset reported yet: Monday-midnight gridlines and the row naming the fix (ADR-0014)",
                new UsageSnapshot(DataSource.Live, 25, 3, Now.AddHours(3), Now), Stats, Account, BothSources),

            new("no-local-transcripts",
                "Plan meters move but nothing is recorded locally: the token-scope note (issue #58)",
                Live, NoLocalUsage(), Account, TranscriptScopeReport.Inspect(ProjectsRoot, [CoworkRoot])),

            new("cli-only",
                "Claude Code only: no plan-history file, tiles filled from transcripts (issue #170)",
                new UsageSnapshot(DataSource.Estimate, null, null, null, Now), Stats, Account,
                CliOnlyScope(), CliOnlyPlan()),

            new("high-usage",
                "Both meters in the red band",
                Live with { SessionPercent = 91, WeeklyPercent = 80 }, Stats, Account, BothSources),

            new("unpriced-model",
                "A model with no published rate: a labelled partial, never a blank tile",
                Live, Stats with { UnpricedModels = ["claude-brandnew-9"] }, Account, BothSources),

            new("boosted",
                "A live promo at its widest: three countdown units beside \"100% used\" (issue #254)",
                Live with { WeeklyPercent = 100 }, Stats, Account, BothSources, Boost: Promo()),

            new("offplan-unknown",
                "Plan window exhausted; extra-usage setting unknown (issue #259)",
                offPlanSnapshot, offPlanStats, Account, BothSources),

            new("offplan-off",
                "Plan window exhausted; extra usage switched off",
                offPlanSnapshot, offPlanStats, Account, BothSources,
                CachedUsage: new CachedUtilization(Now, null, null, null)
                {
                    ExtraUsage = new ExtraUsageStatus(ExtraUsageState.Disabled, UserDisabled: true),
                }),

            new("offplan-on",
                "Plan window exhausted; extra usage switched on",
                offPlanSnapshot, offPlanStats, Account, BothSources,
                CachedUsage: new CachedUtilization(Now, null, null, null)
                {
                    ExtraUsage = new ExtraUsageStatus(ExtraUsageState.Enabled),
                }),

            new("no-data",
                "Nothing readable: the no-usage-data banner, which states what was checked",
                UsageSnapshot.None, EmptyStats(), null, TranscriptScopeReport.Inspect(ProjectsRoot, [CoworkRoot]),
                CliOnlyPlan()),
        ];
    }

    private static List<DayUsage> BuildSeries(int recordedDays)
    {
        var series = new List<DayUsage>();
        for (var offset = 30; offset >= 0; offset--)
        {
            var preInstall = offset >= recordedDays;
            // Output tokens since issue #253, so the scale is ~1% of the totals.
            long tokens = preInstall ? 0 : (long)(60_000 + 89_000 * Math.Abs(Math.Sin(offset * 0.9)));
            series.Add(new DayUsage(Today.AddDays(-offset), tokens, preInstall));
        }
        return series;
    }

    private static PanelStatistics NoLocalUsage() => Stats with
    {
        TokensToday = 0, EstTodayUsd = 0m,
        Tokens31Days = 0, Est31DaysUsd = 0m,
        RecordedDays = 0,
        DailySeries = [.. Series.Select(d => d with { OutputTokens = 0, PreInstall = true })],
        CreditTokens31Days = 0, EstCredit31DaysUsd = 0m,
        CompositionToday = TokenComposition.Empty,
        Composition31Days = TokenComposition.Empty,
    };

    private static PanelStatistics EmptyStats() => NoLocalUsage() with { EstTodayUsd = null, Est31DaysUsd = null, EstCredit31DaysUsd = null };

    private static TranscriptScopeReport CliOnlyScope() =>
        TranscriptScopeReport.Inspect(ProjectsRoot, []) with
        {
            Sources = [new TranscriptSource("Claude Code", [ProjectsRoot], 42, 6_700_000, Now)],
        };

    private static PlanHistoryReport CliOnlyPlan() =>
        new(PlanDataStatus.FileMissing, PlanFile,
            FileExists: false, FileBytes: 0, RawSampleCount: 0, ValidSampleCount: 0,
            Orgs: [], LatestSampleAge: null, Detail: null)
        {
            Searched = [PlanFile],
        };

    /// <summary>
    /// A promo in the shape Claude Code really caches, parsed from a sentence so the render
    /// shows what the extractors produce, ending far enough out for all three countdown units.
    /// </summary>
    private static BoostNotices Promo()
    {
        var ends = Today.AddDays(18);
        var text = string.Create(CultureInfo.InvariantCulture,
            $"+50% weekly limits promo through {ends:MMM d} · clau.de/cc-50-promo");
        return new BoostNotices(Now, [
            new BoostNotice(BoostNotice.WeeklyBar, text, PromoText.Percent(text), PromoText.EndDate(text, Now)),
        ]);
    }
}
