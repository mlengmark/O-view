using System.Globalization;
using OView.Core.Models;
using OView.Core.Providers.CachedUsage;
using OView.Core.Providers.Jsonl;
using OView.Core.Providers.PlanHistory;
using OView.Presentation.Panel;

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
    BoostNotices? Boost = null,
    PanelViewChoice View = default);

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

    /// <summary>
    /// The work area of the display the panel was reported running off: a 1024×768 desktop,
    /// less <c>WorkAreaPlacement.DefaultMarginPx</c> at each end. Both heads render their short
    /// sample against this figure, so the two pictures are comparable.
    /// </summary>
    public const double ShortWorkAreaDip = 712;

    /// <summary>
    /// The state rendered a second time for that display: the tallest one, which is what the
    /// short sample wants. Named rather than measured, because a name chosen at run time would
    /// rename the golden image the day some other state grows past it; the Linux head's
    /// <c>PanelShortScreenTests</c> checks it against the others instead.
    /// </summary>
    public const string ShortState = "offplan-on";

    /// <summary>
    /// The icon's sample states, shared by both heads' <c>--samples</c> hooks: the two meters as
    /// a source reported them, with no reset times, which the icon does not draw.
    /// </summary>
    /// <param name="capturedAtUtc">When the reading was taken; the icon does not draw that either.</param>
    public static IReadOnlyList<(string Name, UsageSnapshot Snapshot)> IconStates(DateTimeOffset capturedAtUtc) =>
    [
        // A measured zero: the one state with no arc at all, where the pupil fades to the
        // track. Nothing rendered it once, which is how the bright-green-pupil-on-an-empty-ring
        // icon shipped (issue #139).
        ("live-00", IconState(DataSource.Live, 0, 0, capturedAtUtc)),
        ("live-06", IconState(DataSource.Live, 6, 1, capturedAtUtc)),
        ("live-47", IconState(DataSource.Live, 47, 20, capturedAtUtc)),
        ("live-58", IconState(DataSource.Live, 58, 30, capturedAtUtc)),
        ("live-72", IconState(DataSource.Live, 72, 40, capturedAtUtc)),
        ("live-91", IconState(DataSource.Live, 91, 60, capturedAtUtc)),
        ("live-100", IconState(DataSource.Live, 100, 80, capturedAtUtc)),
        ("estimate", IconState(DataSource.Estimate, null, null, capturedAtUtc)),
        ("none", UsageSnapshot.None),
    ];

    private static UsageSnapshot IconState(DataSource source, int? session, int? weekly, DateTimeOffset capturedAtUtc) =>
        new(
            source,
            UsageSnapshot.Percent(session),
            UsageSnapshot.Percent(weekly),
            UsageSnapshot.Instant(null),
            UsageSnapshot.Instant(capturedAtUtc),
            UsageSnapshot.Instant(null));

    private static readonly DateOnly Today = DateOnly.FromDateTime(Now.UtcDateTime);

    private static readonly ClaudeAccount Account = new("Sample User", "sample@example.com", "claude_pro", "sample-org");

    // The weekly reset a real machine observed: 06:28:57Z, a quarter of the way into its
    // day column rather than on the edge, which is where gridline placement goes wrong.
    private static readonly DateTimeOffset NextWeeklyReset = new(2026, 8, 4, 6, 28, 57, TimeSpan.Zero);

    private static readonly UsageSnapshot Live = new(
        DataSource.Live,
        UsageSnapshot.Percent(25),
        UsageSnapshot.Percent(3),
        UsageSnapshot.Instant(Now.AddHours(3)),
        UsageSnapshot.Instant(Now),
        UsageSnapshot.Instant(NextWeeklyReset),
        TimeSpan.FromDays(7));

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
        TokensToday: 128_600,
        Tokens31Days: 6_771_000,
        RecordedDays: 10, WindowDays: 31,
        DailySeries: Series,
        CreditTokens31Days: 395_000)
    {
        EstTodayValue = Priced(9.36m),
        Est31DaysValue = Priced(492.52m),
        EstCredit31DaysValue = Priced(92.75m),
        EstTodayOutputValue = Priced(3.22m),
        Est31DaysOutputValue = Priced(169.28m),
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
            EstOffPlanValue = Priced(92.75m),
        };
        var offPlanSnapshot = Live with { SessionPercentValue = UsageSnapshot.Percent(100) };
        var extraUsageOn = new CachedUtilization(Now, null, null, null)
        {
            ExtraUsage = new ExtraUsageStatus(ExtraUsageState.Enabled),
        };
        var overall = new PanelViewChoice(PanelMeasure.Overall);

        return
        [
            new("typical",
                "Live meters, partial history (10 of 31 days), both transcript sources",
                Live, Stats, Account, BothSources),

            new("weekly-reset-unknown",
                "No weekly reset reported yet: Monday-midnight gridlines and the row naming the fix (ADR-0014)",
                Live with { WeeklyResetAtUtcValue = UsageSnapshot.Instant(null), WeeklyResetPeriod = null },
                Stats, Account, BothSources),

            new("no-local-transcripts",
                "Plan meters move but nothing is recorded locally: the token-scope note (issue #58)",
                Live, NoLocalUsage(), Account, TranscriptScopeReport.Inspect(ProjectsRoot, [CoworkRoot])),

            new("cli-only",
                "Claude Code only: no plan-history file, tiles filled from transcripts (issue #170)",
                new UsageSnapshot(
                    DataSource.Estimate,
                    UsageSnapshot.Percent(null),
                    UsageSnapshot.Percent(null),
                    UsageSnapshot.Instant(null),
                    UsageSnapshot.Instant(Now),
                    UsageSnapshot.Instant(null)),
                Stats, Account, CliOnlyScope(), CliOnlyPlan()),

            new("high-usage",
                "Both meters in the red band",
                Live with { SessionPercentValue = UsageSnapshot.Percent(91), WeeklyPercentValue = UsageSnapshot.Percent(80) }, Stats, Account, BothSources),

            new("unpriced-model",
                "A model with no published rate: a labelled partial, never a blank tile",
                Live, Stats with { UnpricedModels = ["claude-brandnew-9"] }, Account, BothSources),

            new("boosted",
                "A live promo at its widest: three countdown units beside \"100% used\" (issue #254)",
                Live with { WeeklyPercentValue = UsageSnapshot.Percent(100) }, Stats, Account, BothSources, Boost: Promo()),

            new("model-limit",
                "A model with a weekly limit of its own: an amber bar under the account's green Weekly, and no off-plan section",
                Live, Stats, Account, BothSources,
                CachedUsage: FableLimit()),

            new("model-limit-spend",
                "A model limit with spend reported: the amount, labelled reported, and no estimate",
                Live, Stats, Account, BothSources,
                CachedUsage: FableLimit() with
                {
                    ExtraUsage = new ExtraUsageStatus(ExtraUsageState.Enabled),
                    Spend = new SpendRead(SpendReadState.Read, new ReportedSpend(12.34m, "USD", Enabled: true)),
                }),

            new("model-limit-spend-unknown",
                "A model limit with extra usage on and no amount to read: unknown, never $0",
                Live, Stats, Account, BothSources,
                CachedUsage: FableLimit() with
                {
                    ExtraUsage = new ExtraUsageStatus(ExtraUsageState.Enabled),
                }),

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
                CachedUsage: extraUsageOn),

            // The redesign's views. Both heads still lay out today's panel, so these differ
            // from their neighbours in the data they carry, which the view models read.
            new("typical-overall",
                "The Overall view of the totals face: all billed tokens, the kind table, Est. value",
                Live, Stats, Account, BothSources, View: overall),

            new("typical-models",
                "Seven models in the store, with the local block turned to its model face",
                Live, WithModels(Stats), Account, BothSources,
                View: new PanelViewChoice(Face: PanelFace.Models)),

            new("typical-models-overall",
                "The model face counting everything billed",
                Live, WithModels(Stats), Account, BothSources,
                View: new PanelViewChoice(PanelMeasure.Overall, PanelFace.Models)),

            new("offplan-on-overall",
                "Off-plan with extra usage on, the Overall view: Est. spend wording and the off-plan row",
                offPlanSnapshot, offPlanStats, Account, BothSources,
                CachedUsage: extraUsageOn, View: overall),

            new("offplan-on-models",
                "Off-plan with seven models and the banner shown: the tallest the redesigned panel gets",
                offPlanSnapshot, WithModels(offPlanStats), Account, BothSources,
                CachedUsage: extraUsageOn,
                View: new PanelViewChoice(PanelMeasure.Overall, PanelFace.Models)),

            new("offplan-on-hidden",
                "The same off-plan account after the reader hid the banner",
                offPlanSnapshot, offPlanStats, Account, BothSources,
                CachedUsage: extraUsageOn,
                View: new PanelViewChoice(BannerHidden: true)),

            new("longest-figures",
                "The widest figures the panel is asked for, and a long model id, for the no-clipping check",
                Live, LongestFigures(), Account, BothSources, View: overall),

            new("no-data",
                "Nothing readable: the no-usage-data banner, which states what was checked",
                UsageSnapshot.None, EmptyStats(), null, TranscriptScopeReport.Inspect(ProjectsRoot, [CoworkRoot]),
                CliOnlyPlan()),
        ];
    }

    private static CachedUtilization FableLimit() =>
        new(Now, null, null, null)
        {
            ModelLimits = new ModelLimitRead(
                [new ModelLimit("Fable", new UtilizationBar(58, NextWeeklyReset))],
                RowsSeen: 3,
                RowsSkipped: 2),
        };

    private static List<DayUsage> BuildSeries(int recordedDays)
    {
        var series = new List<DayUsage>();
        for (var offset = 30; offset >= 0; offset--)
        {
            var preInstall = offset >= recordedDays;
            // Output tokens since issue #253, so the scale is ~1% of the totals.
            long tokens = preInstall ? 0 : (long)(60_000 + 89_000 * Math.Abs(Math.Sin(offset * 0.9)));
            // Everything billed runs about a hundred times the output, the proportion the
            // compositions above are built from.
            series.Add(new DayUsage(Today.AddDays(-offset), tokens, preInstall, tokens * 101));
        }
        return series;
    }

    /// <summary>
    /// A sample Est. figure: estimated where there is one, unavailable where the state is one
    /// in which nothing could be priced, the same split <c>PanelStatistics</c> itself applies,
    /// so a sample cannot describe a pairing the real builder would never produce.
    /// </summary>
    private static EstimatedUsd Priced(decimal? usd) =>
        new(usd, usd is null ? UsageValueStatus.Unavailable : UsageValueStatus.Estimated);

    private static PanelStatistics NoLocalUsage() => Stats with
    {
        TokensToday = 0, EstTodayValue = Priced(0m),
        Tokens31Days = 0, Est31DaysValue = Priced(0m),
        RecordedDays = 0,
        DailySeries = [.. Series.Select(d => d with { OutputTokens = 0, BilledTokens = 0, PreInstall = true })],
        CreditTokens31Days = 0, EstCredit31DaysValue = Priced(0m),
        CompositionToday = TokenComposition.Empty,
        Composition31Days = TokenComposition.Empty,
    };

    private static string[] SevenModelIds =>
    [
        "claude-opus-5", "claude-sonnet-5", "claude-haiku-5", "claude-fable-5",
        "claude-opus-4-1", "claude-sonnet-4-5", "claude-haiku-4-5",
    ];

    /// <summary>
    /// A store with seven models, the case the model face is for: three get their own colour
    /// and the other four are listed as rows. Per-model data lives on the states that need it
    /// and nowhere else, so every other state reads as it always did.
    /// </summary>
    private static PanelStatistics WithModels(PanelStatistics stats, string[]? ids = null)
    {
        var models31 = SliceModels(ids ?? SevenModelIds, stats.Tokens31Days, stats.Composition31Days.Total,
            stats.Est31DaysValue.Value, stats.Est31DaysOutputValue.Value, [3_100, 1_700, 900, 500, 300, 200, 71]);
        var modelsToday = SliceModels(ids ?? SevenModelIds, stats.TokensToday, stats.CompositionToday.Total,
            stats.EstTodayValue.Value, stats.EstTodayOutputValue.Value, [58, 31, 17, 9, 6, 4, 3.6m]);

        return stats with
        {
            ModelsToday = modelsToday,
            Models31Days = models31,
            ModelColourOrder = ModelBreakdown.ColourOrder(models31),
        };
    }

    /// <summary>
    /// One window's models, the totals divided by <paramref name="weights"/> so the rows add up
    /// to the figures above them: the first model takes the rounding remainder of the output
    /// and billed counts.
    /// </summary>
    private static List<ModelSlice> SliceModels(
        string[] ids, long output, long billed, decimal? usd, decimal? outputUsd, decimal[] weights)
    {
        var sum = weights.Sum();
        var tokens = weights.Select(w => (long)Math.Round(output * w / sum)).ToArray();
        var all = weights.Select(w => (long)Math.Round(billed * w / sum)).ToArray();
        tokens[0] += output - tokens.Sum();
        all[0] += billed - all.Sum();

        return [.. ids.Select((id, i) => new ModelSlice(id, ModelDisplayName.For(id), tokens[i])
        {
            TokensCount = TokenCount.Measured(tokens[i]),
            AllTokensCount = TokenCount.Measured(all[i]),
            EstUsdValue = Priced(usd is { } u ? Math.Round(u * weights[i] / sum, 2) : null),
            EstOutputUsdValue = Priced(outputUsd is { } o ? Math.Round(o * weights[i] / sum, 2) : null),
        })];
    }

    /// <summary>
    /// Figures at the width the panel has to hold: a 10-digit billed total, a four-figure
    /// value, and a model id as long as the catalogue's longest. Everything else is the
    /// typical state, so a clipped figure here is the figure's doing.
    /// </summary>
    private static PanelStatistics LongestFigures() => WithModels(
        Stats with
        {
            TokensToday = 987_600,
            Tokens31Days = 52_900_000,
            EstTodayValue = Priced(71.83m),
            Est31DaysValue = Priced(3513.67m),
            EstTodayOutputValue = Priced(24.69m),
            Est31DaysOutputValue = Priced(1322.50m),
            CompositionToday = new TokenComposition(
                Input: 11_000, CacheCreation: 9_800_000, CacheRead: 88_000_000, Output: 987_600),
            Composition31Days = new TokenComposition(
                Input: 500_000, CacheCreation: 480_000_000, CacheRead: 4_200_000_000, Output: 52_900_000),
        },
        [.. SevenModelIds.Skip(1).Prepend("claude-sonnet-4-5-20250929-extended-context-preview")]);

    private static PanelStatistics EmptyStats() => NoLocalUsage() with
    {
        EstTodayValue = Priced(null), Est31DaysValue = Priced(null), EstCredit31DaysValue = Priced(null),
    };

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
