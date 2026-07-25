using System.Text.Json;

namespace OView.Core.Providers.PlanHistory;

/// <summary>
/// Reads %APPDATA%\Claude\plan-usage-history.json — a file owned by Claude Desktop.
/// Strictly read-only (CLAUDE.md rule 3), opened with FileShare.ReadWrite because
/// Desktop appends while we read. The format is undocumented and has already changed
/// once (version: 2), so every field is treated as optional and malformed samples are
/// skipped rather than fatal (docs/findings/plan-usage-history.md).
/// </summary>
public static class PlanHistoryFile
{
    /// <summary>
    /// Where the file actually is on this machine — the canonical
    /// <c>%APPDATA%\Claude</c> location when it exists, otherwise a packaged Claude
    /// Desktop's redirected store (<see cref="PlanHistoryLocator"/>). Falls back to the
    /// canonical path when nothing is found, so error messages name the expected location.
    /// </summary>
    public static string DefaultPath =>
        PlanHistoryLocator.Locate() ?? PlanHistoryLocator.CanonicalPath;

    /// <summary>
    /// Parse the file into validated samples ordered by time. Returns an empty list on
    /// any failure — missing file, unreadable file, malformed JSON. Never throws.
    /// </summary>
    public static IReadOnlyList<PlanHistorySample> Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(stream);

            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("samples", out var samples) ||
                samples.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var result = new List<PlanHistorySample>(samples.GetArrayLength());
            foreach (var element in samples.EnumerateArray())
            {
                if (TryParseSample(element) is { } sample)
                {
                    result.Add(sample);
                }
            }

            result.Sort((a, b) => a.AtUtc.CompareTo(b.AtUtc));
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return [];
        }
    }

    private static PlanHistorySample? TryParseSample(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        // Note: TryGetInt32/64 THROW on non-Number kinds (e.g. a string) rather than
        // returning false — the ValueKind check first is load-bearing.
        if (!element.TryGetProperty("t", out var t) ||
            t.ValueKind != JsonValueKind.Number || !t.TryGetInt64(out var epochMs)) return null;
        if (!element.TryGetProperty("org", out var org) || org.ValueKind != JsonValueKind.String) return null;
        if (!element.TryGetProperty("u", out var u) || u.ValueKind != JsonValueKind.Object) return null;
        if (!u.TryGetProperty("fh", out var fh) ||
            fh.ValueKind != JsonValueKind.Number || !fh.TryGetInt32(out var fiveHour)) return null;
        if (!u.TryGetProperty("sd", out var sd) ||
            sd.ValueKind != JsonValueKind.Number || !sd.TryGetInt32(out var sevenDay)) return null;

        var orgUuid = org.GetString();
        if (string.IsNullOrEmpty(orgUuid)) return null;
        if (fiveHour is < 0 or > 100 || sevenDay is < 0 or > 100) return null;

        DateTimeOffset atUtc;
        try
        {
            atUtc = DateTimeOffset.FromUnixTimeMilliseconds(epochMs);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }

        return new PlanHistorySample(atUtc, orgUuid, fiveHour, sevenDay);
    }
}
