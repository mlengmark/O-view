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
    IReadOnlyList<DayUsage> DailySeries)
{
    public bool HasPartialHistory => RecordedDays < WindowDays;

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

        return new PanelStatistics(
            todayRollups.Sum(r => r.TotalTokens),
            EstimateTotal(todayRollups),
            rollups.Sum(r => r.TotalTokens),
            EstimateTotal(rollups),
            store.CountRecordedDays(windowStart, today),
            windowDays,
            series);
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
