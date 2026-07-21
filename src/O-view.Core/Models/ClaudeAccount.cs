using System.Text.Json;

namespace OView.Core.Models;

/// <summary>
/// Account info from ~/.claude.json → oauthAccount — no token, no network.
/// Tier comes from organizationType; seatTier and userRateLimitTier are empty
/// strings on real accounts and would silently render blank (CLAUDE.md rule 8).
/// </summary>
public sealed record ClaudeAccount(
    string? DisplayName,
    string? Email,
    string? Tier,
    string? OrganizationUuid)
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude.json");

    /// <summary>Read account info; null on any failure. Never throws.</summary>
    public static ClaudeAccount? TryRead(string? path = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path ?? DefaultPath));
            if (!doc.RootElement.TryGetProperty("oauthAccount", out var account) ||
                account.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return new ClaudeAccount(
                ReadString(account, "displayName"),
                ReadString(account, "emailAddress"),
                ReadString(account, "organizationType"),
                ReadString(account, "organizationUuid"));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String &&
        v.GetString() is { Length: > 0 } s
            ? s
            : null;
}
