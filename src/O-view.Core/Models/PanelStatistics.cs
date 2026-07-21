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

        return new PanelStatistics(
            todayRollups.Sum(r => r.TotalTokens),
            EstimateTotal(todayRollups),
            rollups.Sum(r => r.TotalTokens),
            EstimateTotal(rollups),
            store.CountRecordedDays(windowStart, today),
            windowDays,
            series,
            creditRollups.Sum(r => r.TotalTokens),
            EstimateTotal(creditRollups));
    }

    /// <summary>Null when any contributing model is unpriced — never a partial sum.</summary>
    private static decimal? EstimateTotal(IReadOnlyList<DailyRollup> rollups)
    {
        decimal total = 0;
        foreach (var r in rollups)
        {
            if (CostEstimator.EstimateUsd(r.Model, r.InputTokens, r.CacheCreationTokens, r.CacheReadTokens, r.OutputTokens) is not { } usd)
            {
                return null;
            }
            total += usd;
        }
        return total;
    }
}
