using OView.Core.Updates;

namespace OView.Core.Tests;

/// <summary>
/// The bug v0.6.0 creates if this is wrong: a release carrying both platforms' assets, and
/// a Linux build that finds <c>O-view-Setup.exe</c>, downloads it, and hands it to
/// <c>Process.Start</c> with Inno Setup switches.
///
/// <para>The governing rule is that an install only ever considers updates for its own
/// platform.</para>
/// </summary>
public class ReleaseAssetSelectionTests
{
    private const string Deb = "o-view_0.6.0_amd64.deb";
    private const string DebArm = "o-view_0.6.0_arm64.deb";
    private const string Tar = "o-view-0.6.0-linux-x64.tar.gz";

    private static string ReleaseJson(string tag, params string[] assetNames)
    {
        var assets = string.Join(",", assetNames.Select(n =>
            $$"""{"name":"{{n}}","browser_download_url":"https://example/{{n}}"}"""));
        return $$"""{ "tag_name": "{{tag}}", "draft": false, "prerelease": false, "assets": [{{assets}}] }""";
    }

    // ── the regression this issue exists to prevent ─────────────────────────────────

    [Fact]
    public void LinuxBuildDoesNotOfferAWindowsOnlyRelease()
    {
        var json = ReleaseJson("v0.6.0", ReleaseAssets.WindowsInstallerName, ReleaseAssets.WindowsPortableName);

        var result = UpdateCheck.Evaluate("0.5.11", json, ReleaseAssets.DebianPackage("amd64"));

        // Not "offers the exe" — nothing at all. There is no Linux artifact to install.
        Assert.Equal(UpdateOutcome.Unknown, result.Outcome);
        Assert.Null(result.Available);
    }

    [Fact]
    public void LinuxBuildSelectsTheLinuxAssetFromAReleaseCarryingBoth()
    {
        var json = ReleaseJson("v0.6.0", ReleaseAssets.WindowsInstallerName, Deb, Tar);

        var result = UpdateCheck.Evaluate("0.5.11", json, ReleaseAssets.DebianPackage("amd64"));

        Assert.Equal(UpdateOutcome.UpdateAvailable, result.Outcome);
        Assert.EndsWith(Deb, result.Available!.InstallerUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsBuildSelectsTheInstallerFromAReleaseCarryingBoth()
    {
        var json = ReleaseJson("v0.6.0", Deb, Tar, ReleaseAssets.WindowsInstallerName);

        var result = UpdateCheck.Evaluate("0.5.11", json, ReleaseAssets.WindowsInstaller);

        Assert.Equal(UpdateOutcome.UpdateAvailable, result.Outcome);
        Assert.EndsWith(ReleaseAssets.WindowsInstallerName, result.Available!.InstallerUrl, StringComparison.Ordinal);
    }

    /// <summary>arm64 is a real target — Claude Desktop for Linux ships for it, so O-view's users are there.</summary>
    [Fact]
    public void ArchitecturesDoNotMatchEachOther()
    {
        var json = ReleaseJson("v0.6.0", Deb, DebArm);

        Assert.EndsWith(DebArm,
            UpdateCheck.Evaluate("0.5.11", json, ReleaseAssets.DebianPackage("arm64")).Available!.InstallerUrl,
            StringComparison.Ordinal);
        Assert.EndsWith(Deb,
            UpdateCheck.Evaluate("0.5.11", json, ReleaseAssets.DebianPackage("amd64")).Available!.InstallerUrl,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AnArchWithNoPackageInTheReleaseOffersNothing()
    {
        var json = ReleaseJson("v0.6.0", Deb);   // amd64 only

        Assert.Equal(UpdateOutcome.Unknown,
            UpdateCheck.Evaluate("0.5.11", json, ReleaseAssets.DebianPackage("arm64")).Outcome);
    }

    [Fact]
    public void TarballAndPackageAreNotInterchangeable()
    {
        Assert.False(ReleaseAssets.DebianPackage("amd64").Matches(Tar));
        Assert.False(ReleaseAssets.Tarball("linux-x64").Matches(Deb));
    }

    // ── individual selectors ────────────────────────────────────────────────────────

    [Fact]
    public void WindowsInstallerMatchesItsFrozenNameRegardlessOfCase()
    {
        // Windows filesystems are case-insensitive and the published name is frozen, so a
        // case difference must not be treated as a different asset.
        Assert.True(ReleaseAssets.WindowsInstaller.Matches("O-view-Setup.exe"));
        Assert.True(ReleaseAssets.WindowsInstaller.Matches("o-view-setup.EXE"));
    }

    [Fact]
    public void WindowsInstallerDoesNotMatchThePortableExe() =>
        Assert.False(ReleaseAssets.WindowsInstaller.Matches(ReleaseAssets.WindowsPortableName));

    [Fact]
    public void DebianSelectorAcceptsAnyVersion()
    {
        var selector = ReleaseAssets.DebianPackage("amd64");

        Assert.True(selector.Matches("o-view_0.6.0_amd64.deb"));
        Assert.True(selector.Matches("o-view_1.12.3_amd64.deb"));
        Assert.False(selector.Matches("something-else_0.6.0_amd64.deb"));
    }

    /// <summary>
    /// The selector for a build that must never install what it finds. It still checks — so
    /// the user can be told a newer version exists — but has nothing to act on.
    /// </summary>
    [Fact]
    public void TheNoneSelectorNeverMatchesAnything()
    {
        var json = ReleaseJson("v0.6.0", ReleaseAssets.WindowsInstallerName, Deb, Tar);

        Assert.Equal(UpdateOutcome.Unknown,
            UpdateCheck.Evaluate("0.5.11", json, ReleaseAssets.None).Outcome);
    }

    [Fact]
    public void SelectorsDescribeThemselvesForDiagnostics()
    {
        Assert.Equal("O-view-Setup.exe", ReleaseAssets.WindowsInstaller.Description);
        Assert.Contains("amd64", ReleaseAssets.DebianPackage("amd64").Description, StringComparison.Ordinal);
        Assert.Contains("linux-arm64", ReleaseAssets.Tarball("linux-arm64").Description, StringComparison.Ordinal);
    }
}
