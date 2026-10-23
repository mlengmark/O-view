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
    /// Read account info from the first candidate that carries an <c>oauthAccount</c>; null
    /// when no candidate does. Never throws.
    /// </summary>
    public static ClaudeAccount? TryRead(string? path = null) =>
        TryReadAny(path is null ? Candidates() : [path]);

    /// <summary>
    /// The selection rule over an explicit candidate list. Separate from <see cref="TryRead"/>
    /// so it can be tested without a real profile: the production candidates are two absolute
    /// paths on the machine running the test, and a test that depended on those would pass here
    /// and fail on a CI runner with no Claude install — or, worse, pass by reading the
    /// developer's own account.
    /// </summary>
    public static ClaudeAccount? TryReadAny(IReadOnlyList<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (TryReadFrom(candidate) is { } account)
            {
                return account;
            }
        }

        return null;
    }

    /// <summary>
    /// One file. Null when it is missing, unreadable, malformed, or carries no
    /// <c>oauthAccount</c> — all four are "this file cannot answer", and the caller's next
    /// candidate can.
    /// </summary>
    private static ClaudeAccount? TryReadFrom(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("oauthAccount", out var account) ||
                account.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return new ClaudeAccount(
                JsonFields.NonEmptyString(account, "displayName"),
                JsonFields.NonEmptyString(account, "emailAddress"),
                JsonFields.NonEmptyString(account, "organizationType"),
                JsonFields.NonEmptyString(account, "organizationUuid"));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

}
