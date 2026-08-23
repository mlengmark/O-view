using System.Text.Json;
using OView.Core.Providers;

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
    /// <summary>File name Claude Code writes its own state to, beside the config directory.</summary>
    public const string FileName = ".claude.json";

    /// <summary>
    /// Where to look, in order. Two entries because the documentation is genuinely ambiguous
    /// here: it says <c>CLAUDE_CONFIG_DIR</c> relocates "every <c>~/.claude</c> path", and
    /// separately describes <c>~/.claude.json</c> as a sibling of that directory rather than
    /// a member of it — so a relocated setup could plausibly put it in either place.
    ///
    /// <para>Checking both costs one file-existence test and cannot be wrong. Picking one and
    /// guessing would blank the account badge for whichever half of those users guessed
    /// differently, with nothing on screen to say why (rule 8's failure mode).</para>
    /// </summary>
    public static IReadOnlyList<string> Candidates() =>
    [
        Path.Combine(ClaudeConfigDir.Path, FileName),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), FileName),
    ];

    /// <summary>
    /// The first candidate that exists, or the profile-relative path so a "not found" still
    /// names somewhere real for diagnostics to report.
    /// </summary>
    public static string DefaultPath =>
        Candidates().FirstOrDefault(File.Exists) ?? Candidates()[^1];

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
