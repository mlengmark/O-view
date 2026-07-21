using System.Globalization;
using OView.Core.Models;
using OView.Core.Pricing;
using OView.Core.Providers;
using OView.Core.Providers.Jsonl;
using OView.Core.Providers.PlanHistory;
using OView.Core.Storage;

// Temporary harness (docs/build-plan.md): exercises the Phase 1+2 pipeline against
// real local data. Deleted when the tray shell exists.

var utcNow = DateTimeOffset.UtcNow;
var account = ClaudeAccount.TryRead();

using var store = new RollupStore();
var composite = new CompositeUsageProvider(
    new PlanHistoryProvider(orgUuid: account?.OrganizationUuid),
    new JsonlUsageProvider(store));

var snapshot = composite.GetSnapshot(utcNow);

Console.WriteLine($"O-view harness — {utcNow:yyyy-MM-dd HH:mm:ss}Z");
Console.WriteLine($"account   : {account?.DisplayName ?? "(unknown)"} · tier {account?.Tier ?? "(unknown)"}");
Console.WriteLine($"tooltip   : {TooltipFormatter.Format(snapshot)}");
Console.WriteLine($"source    : {snapshot.Source}");

if (snapshot.Source == DataSource.None)
{
    Console.WriteLine("no usage data available");
    return;
}

Console.WriteLine(snapshot.SessionPercent is { } fh ? $"session   : {fh}% of 5h window" : "session   : unknown");
Console.WriteLine(snapshot.WeeklyPercent is { } sd ? $"weekly    : {sd}% of 7d window" : "weekly    : unknown");
Console.WriteLine(snapshot.SessionResetAtUtc is { } reset
    ? $"next reset: {reset:HH:mm:ss}Z (in {reset - utcNow:h\\h\\ m\\m})"
    : "next reset: unknown (no reset observed yet)");
Console.WriteLine($"sampled   : {snapshot.CapturedAtUtc:HH:mm:ss}Z");

// Rollup-store figures for the stats tiles (ui-spec.md).
var today = DateOnly.FromDateTime(utcNow.UtcDateTime);
var windowStart = today.AddDays(-30);

PrintTile("today", store.GetDailyRollups(today, today));
var trailing = store.GetDailyRollups(windowStart, today);
PrintTile("31 days", trailing);
Console.WriteLine($"coverage  : {store.CountRecordedDays(windowStart, today)} of 31 days recorded");

static void PrintTile(string label, IReadOnlyList<DailyRollup> rollups)
{
    var tokens = rollups.Sum(r => r.TotalTokens);
    decimal estimated = 0;
    var priceable = true;
    foreach (var r in rollups)
    {
        if (CostEstimator.EstimateUsd(r.Model, r.InputTokens, r.CacheCreationTokens, r.CacheReadTokens, r.OutputTokens) is { } usd)
        {
            estimated += usd;
        }
        else
        {
            priceable = false;
        }
    }

    var value = priceable
        ? "$" + estimated.ToString("0.00", CultureInfo.InvariantCulture)
        : rollups.Count == 0 ? "$0.00" : "unknown (unpriced model)";
    Console.WriteLine($"{label,-10}: {tokens:N0} tokens · est. value {value}");
}
