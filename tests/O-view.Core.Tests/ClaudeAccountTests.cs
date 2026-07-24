using OView.Core.Models;

namespace OView.Core.Tests;

public class ClaudeAccountTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("oview-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Tier_ComesFromOrganizationType_NotSeatTier()
    {
        // Real-account shape (CLAUDE.md rule 8): seatTier and userRateLimitTier are
        // empty strings — the obvious-looking fields silently render a blank badge.
        var path = Path.Combine(_dir, ".claude.json");
        File.WriteAllText(path, """
            {"oauthAccount":{"displayName":"Maximilian","emailAddress":"m@example.com",
             "organizationType":"claude_pro","seatTier":"","userRateLimitTier":"",
             "organizationUuid":"00000000-0000-0000-0000-000000000000"}}
            """);

        var account = ClaudeAccount.TryRead(path);

        Assert.NotNull(account);
        Assert.Equal("claude_pro", account.Tier);
        Assert.Equal("Maximilian", account.DisplayName);
        Assert.Equal("m@example.com", account.Email);
        Assert.Equal("00000000-0000-0000-0000-000000000000", account.OrganizationUuid);
    }

    [Fact]
    public void MissingFileOrShape_ReturnsNull_NeverThrows()
    {
        Assert.Null(ClaudeAccount.TryRead(Path.Combine(_dir, "absent.json")));

        var bad = Path.Combine(_dir, "bad.json");
        File.WriteAllText(bad, "{\"noAccount\":true}");
        Assert.Null(ClaudeAccount.TryRead(bad));
    }

    [Fact]
    public void EmptyStrings_ReadAsNull_NotBlankBadges()
    {
        var path = Path.Combine(_dir, ".claude.json");
        File.WriteAllText(path, """
            {"oauthAccount":{"displayName":"","organizationType":""}}
            """);

        var account = ClaudeAccount.TryRead(path);

        Assert.NotNull(account);
        Assert.Null(account.DisplayName);
        Assert.Null(account.Tier);
    }
}
