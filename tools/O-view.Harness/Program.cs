using System.Globalization;
using System.Text.Json;
using OView.Core.Models;
using OView.Core.Pricing;
using OView.Core.Providers;
using OView.Core.Providers.Jsonl;
using OView.Core.Providers.PlanHistory;
using OView.Core.Storage;

// Temporary harness (docs/build-plan.md): exercises the Phase 1+2 pipeline against
// real local data. Deleted when the tray shell exists.

var utcNow = DateTimeOffset.UtcNow;

// Account context from ~/.claude.json (CLAUDE.md rule 8: tier is organizationType;
// seatTier is empty and would render blank). All fields optional.
string? orgUuid = null, displayName = null, tier = null;
try
{
    var claudeJson = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
    using var doc = JsonDocument.Parse(File.ReadAllText(claudeJson));
    if (doc.RootElement.TryGetProperty("oauthAccount", out var account))
    {
        orgUuid = account.TryGetProperty("organizationUuid", out var o) ? o.GetString() : null;
        displayName = account.TryGetProperty("displayName", out var d) ? d.GetString() : null;
        tier = account.TryGetProperty("organizationType", out var t) ? t.GetString() : null;
    }
}
catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
{
    // Account info is cosmetic here; the providers work without it.
}

using var store = new RollupStore();
var composite = new CompositeUsageProvider(
    new PlanHistoryProvider(orgUuid: orgUuid),
    new JsonlUsageProvider(store));

var snapshot = composite.GetSnapshot(utcNow);

Console.WriteLine($"O-view harness — {utcNow:yyyy-MM-dd HH:mm:ss}Z");
Console.WriteLine($"account   : {displayName ?? "(unknown)"} · tier {tier ?? "(unknown)"}");
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
