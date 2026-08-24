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

    /// <summary>
    /// Reported 2026-08-24. Claude Code migrated its state into <c>~/.claude/.claude.json</c>
    /// and the file it writes there starts as nine keys of migration bookkeeping — no
    /// <c>oauthAccount</c>. Resolving to the first candidate that EXISTED picked that stub, and
    /// the panel read "account unknown / tier unknown" beside a populated file one directory up.
    /// </summary>
    [Fact]
    public void AMigrationStubDoesNotShadowTheFileThatHasTheAccount()
    {
        var stub = Path.Combine(_dir, "stub.json");
        File.WriteAllText(stub, """
            {"firstStartTime":"2026-08-24T11:03:29.644Z","migrationVersion":13,
             "sonnet1m45MigrationComplete":true,"seenNotifications":{}}
            """);

        var populated = Path.Combine(_dir, "populated.json");
        File.WriteAllText(populated, """
            {"oauthAccount":{"displayName":"Maximilian","emailAddress":"m@example.com",
             "organizationType":"claude_pro"}}
            """);

        var account = ClaudeAccount.TryReadAny([stub, populated]);

        Assert.NotNull(account);
        Assert.Equal("claude_pro", account.Tier);
        Assert.Equal("Maximilian", account.DisplayName);
    }

    /// <summary>
    /// Order still decides between two candidates that BOTH carry an account: the configured
    /// location wins, which is what CLAUDE_CONFIG_DIR is for.
    /// </summary>
    [Fact]
    public void TheFirstCandidateWithAnAccountWins()
    {
        var first = Path.Combine(_dir, "first.json");
        File.WriteAllText(first, """{"oauthAccount":{"displayName":"First"}}""");

        var second = Path.Combine(_dir, "second.json");
        File.WriteAllText(second, """{"oauthAccount":{"displayName":"Second"}}""");

        Assert.Equal("First", ClaudeAccount.TryReadAny([first, second])?.DisplayName);
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
