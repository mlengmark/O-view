using OView.Core.Updates;

namespace OView.Core.Tests;

public class ReleaseVersionTests
{
    [Theory]
    [InlineData("v0.4.2", 0, 4, 2)]
    [InlineData("0.4.2", 0, 4, 2)]
    [InlineData("V1.2.3", 1, 2, 3)]
    [InlineData("0.4.2.0", 0, 4, 2)]      // assembly 4-part version, revision ignored
    [InlineData("1.0", 1, 0, 0)]           // missing patch defaults to 0
    [InlineData("2", 2, 0, 0)]             // major only
    [InlineData("0.4.3-beta.1", 0, 4, 3)]  // pre-release suffix stripped
    [InlineData(" v0.4.2 ", 0, 4, 2)]      // surrounding whitespace
    [InlineData("0.10.0+build.7", 0, 10, 0)]
    public void Parses_valid_versions(string text, int major, int minor, int patch)
    {
        Assert.True(ReleaseVersion.TryParse(text, out var v));
        Assert.Equal(new ReleaseVersion(major, minor, patch), v);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("v")]
    [InlineData("vabc")]
    [InlineData("1.x.0")]
    [InlineData("1.2.3.4.5")]
    [InlineData("-1.0.0")]
    public void Rejects_invalid_versions(string? text)
    {
        Assert.False(ReleaseVersion.TryParse(text, out _));
    }

    [Fact]
    public void Compares_numerically_not_lexicographically()
    {
        Assert.True(Parse("0.10.0") > Parse("0.9.0"));
        Assert.True(Parse("1.0.0") > Parse("0.99.99"));
        Assert.True(Parse("0.4.3") > Parse("0.4.2"));
        // The third vector crossing into two digits, first exercised by v0.4.10.
        // Lexicographically "0.5.10" < "0.5.9", so a string comparison would leave every user
        // on 0.5.9 never offered the update — and silently, since the check would report
        // "up to date" rather than fail.
        Assert.True(Parse("0.5.10") > Parse("0.5.9"));
        Assert.True(Parse("0.5.11") > Parse("0.5.10"));
        Assert.True(Parse("0.4.2") == Parse("0.4.2"));
        Assert.False(Parse("0.4.2") > Parse("0.4.2"));
    }

    /// <summary>
    /// Every vector crosses into two digits independently, and each is compared by its own
    /// <c>int</c>. The third vector has shipped past 9 three times (v0.4.10, v0.5.10, v0.5.11,
    /// then v0.6.10) and the second is covered above; <b>the first had no coverage at all</b>
    /// until this test, purely because the project has never left 0.x.
    ///
    /// <para>Written as a theory over all three positions so the guarantee is "no vector
    /// compares lexicographically" rather than three separate assertions that happen to
    /// exist.</para>
    /// </summary>
    [Theory]
    // first vector
    [InlineData("10.0.0", "9.0.0")]
    [InlineData("11.0.0", "10.0.0")]
    [InlineData("100.0.0", "99.0.0")]
    // second vector
    [InlineData("0.10.0", "0.9.0")]
    [InlineData("0.100.0", "0.99.0")]
    // third vector
    [InlineData("0.0.10", "0.0.9")]
    [InlineData("0.0.100", "0.0.99")]
    public void Every_vector_crosses_into_two_digits_numerically(string newer, string older)
    {
        Assert.True(Parse(newer) > Parse(older), $"{newer} should be newer than {older}");
        Assert.True(Parse(older) < Parse(newer), $"{older} should be older than {newer}");
    }

    /// <summary>
    /// The specific shape a string comparison gets wrong, kept separate because it is narrower
    /// than the theory above: the trap is a change of <b>digit width</b>, not two digits as
    /// such. "11.0.0" sorts correctly above "10.0.0" as a string — both are the same width —
    /// so pairing those would assert something untrue. It is 9→10 and 99→100 that invert.
    /// </summary>
    [Theory]
    [InlineData("10.0.0", "9.0.0")]
    [InlineData("100.0.0", "99.0.0")]
    [InlineData("0.10.0", "0.9.0")]
    [InlineData("0.100.0", "0.99.0")]
    [InlineData("0.0.10", "0.0.9")]
    [InlineData("0.0.100", "0.0.99")]
    public void A_string_comparison_would_call_these_newer_versions_older(string newer, string older)
    {
        Assert.True(string.CompareOrdinal(newer, older) < 0,
            $"{newer} should sort BELOW {older} as a string — that inversion is what this guards");

        Assert.True(Parse(newer) > Parse(older));
    }

    /// <summary>
    /// Vectors are unbounded non-negative integers, not capped at 99. Nothing in the app, the
    /// release workflow or the installer enforces a ceiling, so documenting one would be a
    /// rule that does not exist — and a cap would only ever be a reason to bump a higher
    /// vector for no engineering reason.
    /// </summary>
    [Fact]
    public void Vectors_are_not_capped_at_two_digits()
    {
        Assert.True(ReleaseVersion.TryParse("1.234.5678", out var big));
        Assert.Equal(new ReleaseVersion(1, 234, 5678), big);
        Assert.True(big > Parse("1.234.5677"));
    }

    private static ReleaseVersion Parse(string s)
    {
        Assert.True(ReleaseVersion.TryParse(s, out var v));
        return v;
    }
}

public class UpdateCheckTests
{
    /// <summary>
    /// These cases are all about version and feed handling, so they run as the Windows build
    /// — the platform whose behaviour must not change. Asset selection itself is exercised
    /// separately in <see cref="ReleaseAssetSelectionTests"/>.
    /// </summary>
    private static UpdateCheckResult Check(string currentVersion, string releaseJson) =>
        UpdateCheck.Evaluate(currentVersion, releaseJson, ReleaseAssets.WindowsInstaller);

    private static string ReleaseJson(
        string tag,
        bool draft = false,
        bool prerelease = false,
        string? assetName = ReleaseAssets.WindowsInstallerName,
        string? assetUrl = "https://github.com/mlengmark/O-view/releases/download/v9.9.9/O-view-Setup.exe")
    {
        var assets = assetName is null
            ? "[]"
            : $$"""[{"name":"{{assetName}}","browser_download_url":"{{assetUrl}}"},{"name":"O-view.Tray.exe","browser_download_url":"https://example/O-view.Tray.exe"}]""";
        return $$"""
        { "tag_name": "{{tag}}", "draft": {{(draft ? "true" : "false")}}, "prerelease": {{(prerelease ? "true" : "false")}}, "assets": {{assets}} }
        """;
    }

    [Fact]
    public void Newer_release_with_installer_asset_is_offered()
    {
        var result = Check("0.4.2", ReleaseJson("v0.4.3"));

        Assert.Equal(UpdateOutcome.UpdateAvailable, result.Outcome);
        Assert.NotNull(result.Available);
        Assert.Equal(new ReleaseVersion(0, 4, 3), result.Available!.Version);
        Assert.Equal("v0.4.3", result.Available.Tag);
        Assert.EndsWith("O-view-Setup.exe", result.Available.InstallerUrl);
    }

    [Fact]
    public void Same_version_is_up_to_date()
    {
        var result = Check("0.4.3", ReleaseJson("v0.4.3"));
        Assert.Equal(UpdateOutcome.UpToDate, result.Outcome);
        Assert.Null(result.Available);
    }

    [Fact]
    public void Older_published_release_is_up_to_date_never_offers_downgrade()
    {
        // Running a dev build ahead of the last release must not be told to "update" backwards.
        var result = Check("0.5.0", ReleaseJson("v0.4.3"));
        Assert.Equal(UpdateOutcome.UpToDate, result.Outcome);
    }

    [Fact]
    public void Assembly_four_part_version_compares_against_three_part_tag()
    {
        Assert.Equal(UpdateOutcome.UpToDate, Check("0.4.2.0", ReleaseJson("v0.4.2")).Outcome);
        Assert.Equal(UpdateOutcome.UpdateAvailable, Check("0.4.2.0", ReleaseJson("v0.4.3")).Outcome);
    }

    [Fact]
    public void Newer_tag_without_installer_asset_is_unknown_not_a_dangling_offer()
    {
        var result = Check("0.4.2", ReleaseJson("v0.4.3", assetName: null));
        Assert.Equal(UpdateOutcome.Unknown, result.Outcome);
        Assert.Null(result.Available);
    }

    [Fact]
    public void Draft_or_prerelease_is_not_offered()
    {
        Assert.Equal(UpdateOutcome.Unknown, Check("0.4.2", ReleaseJson("v0.4.3", draft: true)).Outcome);
        Assert.Equal(UpdateOutcome.Unknown, Check("0.4.2", ReleaseJson("v0.4.3", prerelease: true)).Outcome);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{ \"tag_name\": \"not-a-version\" }")]
    [InlineData("{ }")]
    [InlineData("[]")]
    [InlineData("\"a string\"")]
    public void Malformed_or_unusable_feed_is_unknown_never_throws(string json)
    {
        Assert.Equal(UpdateOutcome.Unknown, Check("0.4.2", json).Outcome);
    }

    [Fact]
    public void Unparseable_current_version_is_unknown()
    {
        Assert.Equal(UpdateOutcome.Unknown, Check("dev", ReleaseJson("v9.9.9")).Outcome);
    }
}
