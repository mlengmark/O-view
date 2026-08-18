using OView.App.Diagnostics;

namespace OView.App.Tests;

/// <summary>
/// The bundle is pasted into public GitHub issues, so what these assert is not a formatting
/// preference — it is what does and does not become permanently searchable when a user asks
/// for help with a tray icon.
///
/// <para>Two properties are in tension throughout: nothing identifying may survive, and the
/// path shape must, because the roots are printed precisely so a wrong
/// <c>SpecialFolder</c> resolution is visible.</para>
/// </summary>
public class RedactTests
{
    private static readonly string[] Ada = ["ada"];

    // ── the account name ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(@"C:\Users\ada\AppData\Roaming", @"C:\Users\<user>\AppData\Roaming")]
    [InlineData(@"C:\Users\ada", @"C:\Users\<user>")]
    [InlineData("/home/ada/.config/O-view", "/home/<user>/.config/O-view")]
    [InlineData("/home/ada", "/home/<user>")]
    public void TheAccountNameIsReplacedButTheShapeSurvives(string input, string expected)
    {
        // The drive, the profile container and everything below the name are what make a
        // misresolved root visible. Collapsing the whole path to ~ would hide exactly that.
        Assert.Equal(expected, Redact.Bundle(input, Ada));
    }

    [Fact]
    public void EveryOccurrenceGoes_NotJustTheFirst()
    {
        var text = "  config root   : C:\\Users\\ada\\AppData\n  home          : C:\\Users\\ada\n";

        var redacted = Redact.Bundle(text, Ada);

        Assert.DoesNotContain("ada", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, redacted.Split(Redact.UserPlaceholder).Length - 1);
    }

    [Fact]
    public void CasingDoesNotLetOneThrough()
    {
        // Windows paths are case-insensitive and a bundle may spell the profile either way.
        // Over-redaction is the safe failure; a missed name is public and permanent.
        Assert.Equal(@"C:\Users\<user>\x", Redact.Bundle(@"C:\Users\ADA\x", Ada));
    }

    [Theory]
    [InlineData(@"C:\tools\maxsize\bin")]        // contains a shorter name as a substring
    [InlineData(@"C:\Users\adaptive\config")]    // starts with the name
    [InlineData("the adamant plan")]             // ordinary prose
    public void ASegmentThatMerelyContainsTheNameIsLeftAlone(string input)
    {
        // A substring replace would mangle paths that mention nobody, and a mangled path is
        // a misleading diagnostic — worse than a verbose one.
        Assert.Equal(input, Redact.Bundle(input, ["ada", "max"]));
    }

    [Fact]
    public void ASingleCharacterAccountNameIsNotMatched()
    {
        // Matching one character as a path segment would eat drive letters and any
        // single-letter directory, for no privacy gain worth having.
        Assert.Equal(@"C:\Users\a\x", Redact.Bundle(@"C:\Users\a\x", ["a"]));
    }

    [Fact]
    public void BothSpellingsOfTheAccountAreRemoved()
    {
        // A login of ada.lovelace can own a profile folder called ada; redacting one and
        // not the other still publishes a name.
        var text = @"C:\Users\ada\x and /home/ada.lovelace/y";

        var redacted = Redact.Bundle(text, ["ada", "ada.lovelace"]);

        Assert.DoesNotContain("lovelace", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"\ada\", redacted, StringComparison.OrdinalIgnoreCase);
    }

    // ── organization UUIDs ─────────────────────────────────────────────────────────

    [Fact]
    public void AUuidKeepsEightCharactersAndLosesTheRest()
    {
        var text = "  account file  : read ok (org 3f2504e0-4f89-11d3-9a0c-0305e82c3301, tier claude_pro)";

        var redacted = Redact.Bundle(text, Ada);

        // Enough to compare against another org id at a glance; not the identifier.
        Assert.Contains("org 3f2504e0…", redacted);
        Assert.DoesNotContain("0305e82c3301", redacted);
    }

    [Fact]
    public void TheWholeOrgListIsCovered_NotJustTheAccountLine()
    {
        // PlanHistoryReport prints every org found in the file. That list was the larger
        // exposure of the two, and it reaches the bundle through a different class.
        var text = "  orgs in file  : 3f2504e0-4f89-11d3-9a0c-0305e82c3301, "
                   + "9c858901-8a57-4791-81fe-4c455b099bc9";

        var redacted = Redact.Bundle(text, Ada);

        Assert.Contains("3f2504e0…", redacted);
        Assert.Contains("9c858901…", redacted);
        Assert.DoesNotContain("4c455b099bc9", redacted);
    }

    [Fact]
    public void TwoDifferentOrgsStillReadAsDifferent()
    {
        // The diagnostic question is "does the account's org match the file's org". A
        // redaction that made every org look alike would answer it wrongly.
        var text = "a 3f2504e0-4f89-11d3-9a0c-0305e82c3301 b 9c858901-8a57-4791-81fe-4c455b099bc9";

        var redacted = Redact.Bundle(text, Ada);

        Assert.Contains("3f2504e0…", redacted);
        Assert.Contains("9c858901…", redacted);
    }

    [Fact]
    public void ALongerHexRunIsNotMistakenForAUuid()
    {
        // A SHA-256 has no dashes, but guarding the boundaries is what keeps a future
        // checksum line in the bundle from being chewed up.
        const string sha = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

        Assert.Contains(sha, Redact.Bundle($"  installer : {sha}", Ada));
    }

    // ── the whole bundle ───────────────────────────────────────────────────────────

    [Fact]
    public void ARealisticBundleLosesTheNameAndTheOrgAndKeepsEverythingElse()
    {
        var text = string.Join('\n',
            "O-view diagnostics",
            @"  path          : C:\Users\ada\AppData\Roaming\Claude\plan-usage-history.json",
            "  orgs in file  : 3f2504e0-4f89-11d3-9a0c-0305e82c3301",
            @"  home          : C:\Users\ada",
            @"  process       : C:\Users\ada\AppData\Local\Programs\O-view\O-view.Tray.exe",
            "  account file  : read ok (org 3f2504e0-4f89-11d3-9a0c-0305e82c3301, tier claude_pro)",
            "  transcripts   : 12 file(s), 3,481,220 bytes total");

        var redacted = Redact.Bundle(text, Ada);

        Assert.DoesNotContain("ada", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("4f89-11d3", redacted);

        // Still a usable report: the structure that reveals a misresolved root, the file
        // name, the tier and the counts all survive.
        Assert.Contains(@"C:\Users\<user>\AppData\Roaming\Claude\plan-usage-history.json", redacted);
        Assert.Contains(@"C:\Users\<user>\AppData\Local\Programs\O-view\O-view.Tray.exe", redacted);
        Assert.Contains("tier claude_pro", redacted);
        Assert.Contains("12 file(s), 3,481,220 bytes total", redacted);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void EmptyInputIsReturnedUnchanged(string? text)
    {
        Assert.Equal(text, Redact.Bundle(text!, Ada));
    }

    [Fact]
    public void ThisMachinesAccountNameIsDiscoverable()
    {
        // The default overload is what production uses; if it found nothing to redact, the
        // whole guard would be inert while every test above still passed.
        var names = Redact.AccountNames();

        Assert.NotEmpty(names);
        Assert.All(names, n => Assert.False(string.IsNullOrWhiteSpace(n)));
    }
}
