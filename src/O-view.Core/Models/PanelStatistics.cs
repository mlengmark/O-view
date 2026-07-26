using OView.Core.Pricing;
using OView.Core.Storage;

namespace OView.Core.Models;

/// <summary>One graph slot: a UTC day and what is honestly known about it.</summary>
/// <param name="PreInstall">
/// True for days before the first recorded day. These have NO data, not zero data —
/// the UI renders them as an explicit empty region, never zero-height bars
/// (ADR-0006; CLAUDE.md rule 6).
/// </param>
public sealed record DayUsage(DateOnly DateUtc, long TotalTokens, bool PreInstall);

/// <summary>
/// Everything the popup's tiles and graph need, computed from the rollup store.
/// "Est." values price tokens at public API rates — not money charged; the UI must
/// keep the Est. prefix. A null estimate means an unpriced model was involved and
/// the tile shows unknown rather than a partial sum presented as a total.
/// </summary>
public sealed record PanelStatistics(
    long TokensToday,
    decimal? EstTodayUsd,
    long Tokens31Days,
    decimal? Est31DaysUsd,
    int RecordedDays,
    int WindowDays,
    IReadOnlyList<DayUsage> DailySeries,
    long CreditTokens31Days,
    decimal? EstCredit31DaysUsd,
    DivergenceResult? Divergence = null,
    decimal? EstOffPlanUsd = null)
{
    /// <summary>
    /// Model IDs seen in the window that have no published rate, so their tokens are
    /// excluded from the Est. figures. Surfaced in the UI rather than silently dropped —
    /// and, crucially, they no longer void the whole total: a single unrecognised model
    /// (a newly released Claude, say) used to blank both "Est. value" tiles entirely.
    /// </summary>
    public IReadOnlyList<string> UnpricedModels { get; init; } = [];

    /// <summary>
    /// Per-model split behind <see cref="TokensToday"/> and <see cref="EstTodayUsd"/>
    /// (GitHub issue #37). The rollup store already keeps its ledger at (UTC date ×
    /// model) grain, so this costs one extra grouping over rollups already in hand —
    /// no second query, and nothing to fetch when a tile is clicked.
    /// </summary>
    public IReadOnlyList<ModelSlice> ModelsToday { get; init; } = [];

    /// <summary>Per-model split behind the 31-day figures. See <see cref="ModelsToday"/>.</summary>
    public IReadOnlyList<ModelSlice> Models31Days { get; init; } = [];

    /// <summary>
    /// Which models get their own colour, in slot order — one answer for the whole panel
    /// so a model wears the same colour on every tile. Derived from the 31-day window
    /// because it is the superset the other tiles draw from.
    /// </summary>
    public IReadOnlyList<string> ModelColourOrder { get; init; } = [];

    public bool HasPartialHistory => RecordedDays < WindowDays;

    /// <summary>True when work in the current session window is not drawing from the plan.</summary>
    public bool IsOffPlan => Divergence?.IsOffPlan == true;

    /// <summary>True when any credit-billed usage was recorded in the 31-day window.</summary>
    public bool HasCreditUsage => CreditTokens31Days > 0;

    /// <summary>
    /// Adds divergence analysis for the current session window. Kept separate from
    /// <see cref="Build"/> because it needs the plan-meter series, which lives in the
    /// provider layer — Core stays free of file-format knowledge here.
    /// </summary>
    public PanelStatistics WithDivergence(RollupStore store, DateTimeOffset windowStartUtc, IReadOnlyList<int> planPercentsInWindow)
    {
        var windowUsage = store.GetUsageSince(windowStartUtc);
        var outputTokens = windowUsage.Sum(r => r.OutputTokens);
        var result = DivergenceDetector.Evaluate(planPercentsInWindow, outputTokens);

        // Only price the window when it is actually off-plan: otherwise this figure
        // would read as money spent when it is plan usage costing nothing marginal.
        return this with
        {
            Divergence = result,
            EstOffPlanUsd = result.IsOffPlan ? EstimateTotal(windowUsage) : null,
        };
    }

    public static PanelStatistics Build(RollupStore store, DateTimeOffset utcNow, int windowDays = 31)
    {
        var today = DateOnly.FromDateTime(utcNow.UtcDateTime);
        var windowStart = today.AddDays(-(windowDays - 1));

        var rollups = store.GetDailyRollups(windowStart, today);
        var todayRollups = rollups.Where(r => r.DateUtc == today).ToList();

        // First recorded day across ALL history, not just the window — a store older
        // than the window means no day in the window is pre-install.
        var firstRecorded = store.GetDailyRollups(DateOnly.MinValue, today) is { Count: > 0 } all
            ? all.Min(r => r.DateUtc)
            : (DateOnly?)null;

        var byDate = rollups
            .GroupBy(r => r.DateUtc)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.TotalTokens));

        var series = new List<DayUsage>(windowDays);
        for (var day = windowStart; day <= today; day = day.AddDays(1))
        {
            var preInstall = firstRecorded is null || day < firstRecorded;
            series.Add(new DayUsage(day, byDate.GetValueOrDefault(day), preInstall));
        }

        // 31-day credit spend (GitHub issue #3): the estimated API-rate value of usage
        // on models that bill as extra usage rather than drawing from the plan window.
        // A retroactive per-model estimate — see CreditBilledModels for why this is
        // inferred, not read, and what it can miss.
        var creditRollups = rollups.Where(r => CreditBilledModels.IsCreditBilled(r.Model)).ToList();

        var est31 = EstimateTotal(rollups, out var unpriced);

        return new PanelStatistics(
            todayRollups.Sum(r => r.TotalTokens),
            EstimateTotal(todayRollups),
            rollups.Sum(r => r.TotalTokens),
            est31,
            store.CountRecordedDays(windowStart, today),
            windowDays,
            series,
            creditRollups.Sum(r => r.TotalTokens),
            EstimateTotal(creditRollups))
        {
            UnpricedModels = unpriced,
            ModelsToday = SliceByModel(todayRollups),
            Models31Days = SliceByModel(rollups),
            ModelColourOrder = ModelBreakdown.ColourOrder(SliceByModel(rollups)),
        };
    }

    /// <summary>
    /// Collapses rollups to one row per model. The store's grain is already (date ×
    /// model), so this only drops the date — the model dimension was being discarded
    /// at the very last step, not missing.
    ///
    /// A model's estimate is null only when NOTHING of its usage could be priced, which
    /// for a single model means its rate is unknown. That is deliberately the same rule
    /// <see cref="EstimateTotal"/> applies to the whole window: unknown stays unknown and
    /// gets named, rather than being folded in as a zero that would understate the total.
    /// </summary>
    private static IReadOnlyList<ModelSlice> SliceByModel(IReadOnlyList<DailyRollup> rollups) =>
        rollups
            .GroupBy(r => r.Model, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ModelSlice(
                g.Key,
                ModelDisplayName.For(g.Key),
                g.Sum(r => r.TotalTokens),
                EstimateTotal(g.ToList())))
            .OrderByDescending(s => s.Tokens)
            .ToList();

    /// <summary>
    /// Sums the priced rollups. Null only when there was something to price and NOTHING
    /// could be priced — an honest "unknown". Otherwise it returns the priced subtotal and
    /// reports the excluded models via <paramref name="unpriced"/>, so the UI can state the
    /// gap instead of blanking. Previously any single unpriced model returned null, which
    /// meant one unrecognised model id voided both Est. tiles even when 99% of the tokens
    /// were priceable (CLAUDE.md rule 6 is "explain the uncertainty", not "show nothing").
    /// </summary>
    private static decimal? EstimateTotal(IReadOnlyList<DailyRollup> rollups, out IReadOnlyList<string> unpriced)
    {
        decimal total = 0;
        var priced = 0;
        var missing = new List<string>();

        foreach (var r in rollups)
        {
            if (CostEstimator.EstimateUsd(r.Model, r.InputTokens, r.CacheCreationTokens, r.CacheReadTokens, r.OutputTokens) is { } usd)
            {
                total += usd;
                priced++;
            }
            else if (!missing.Contains(r.Model, StringComparer.OrdinalIgnoreCase))
            {
                missing.Add(r.Model);
            }
        }

        unpriced = missing;
        return rollups.Count > 0 && priced == 0 ? null : total;
    }

    /// <summary>Overload for callers that don't surface the excluded-model list.</summary>
    private static decimal? EstimateTotal(IReadOnlyList<DailyRollup> rollups) =>
        EstimateTotal(rollups, out _);
}
