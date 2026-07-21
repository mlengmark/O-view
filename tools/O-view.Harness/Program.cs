using System.Text.Json;
using OView.Core.Models;
using OView.Core.Providers.PlanHistory;

// Temporary Phase 1 harness (docs/build-plan.md): prints a snapshot from the real
// plan-usage-history.json. Deleted when the tray shell exists.

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
    // Account info is cosmetic here; the provider works without it.
}

var provider = new PlanHistoryProvider(orgUuid: orgUuid);
var snapshot = provider.GetSnapshot(utcNow);

Console.WriteLine($"O-view harness — {utcNow:yyyy-MM-dd HH:mm:ss}Z");
Console.WriteLine($"account   : {displayName ?? "(unknown)"} · tier {tier ?? "(unknown)"}");
Console.WriteLine($"file      : {PlanHistoryFile.DefaultPath}");
Console.WriteLine($"source    : {snapshot.Source}");

if (snapshot.Source == DataSource.None)
{
    Console.WriteLine("no usage data available — is Claude Desktop installed?");
    return;
}

Console.WriteLine($"session   : {snapshot.SessionPercent}% of 5h window");
Console.WriteLine($"weekly    : {snapshot.WeeklyPercent}% of 7d window");
Console.WriteLine(snapshot.SessionResetAtUtc is { } reset
    ? $"next reset: {reset:HH:mm:ss}Z (in {reset - utcNow:h\\h\\ m\\m})"
    : "next reset: unknown (no reset observed yet)");
Console.WriteLine($"sampled   : {snapshot.CapturedAtUtc:HH:mm:ss}Z ({utcNow - snapshot.CapturedAtUtc!.Value:m\\m\\ s\\s} ago)");
