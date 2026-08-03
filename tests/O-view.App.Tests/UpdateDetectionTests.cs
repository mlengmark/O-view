using System.Runtime.InteropServices;
using OView.App.Updates;
using OView.Core.Updates;

namespace OView.App.Tests;

/// <summary>
/// Detection versus permission — the distinction that silently disabled the Linux update
/// notice.
///
/// <para>An apt build was given <see cref="ReleaseAssets.None"/> because it must never
/// install anything. But <see cref="UpdateCheck.Evaluate"/> only reports
/// <c>UpdateAvailable</c> when the selector matches a published asset, so a selector matching
/// nothing meant that build reported <c>Unknown</c> forever. ADR-0009 says it should "say a
/// newer version exists, and stop"; it could not do the first half.</para>
/// </summary>
public class UpdateDetectionTests
{
    /// <summary>A release carrying every platform's assets — what unified releases publish.</summary>
    private const string UnifiedRelease = """
        {
          "tag_name": "v0.7.0",
          "draft": false,
          "prerelease": false,
          "assets": [
            { "name": "O-view-Setup.exe",              "browser_download_url": "https://x/O-view-Setup.exe" },
            { "name": "O-view.Tray.exe",               "browser_download_url": "https://x/O-view.Tray.exe" },
            { "name": "o-view_0.7.0_amd64.deb",        "browser_download_url": "https://x/amd64.deb" },
            { "name": "o-view_0.7.0_arm64.deb",        "browser_download_url": "https://x/arm64.deb" },
            { "name": "o-view-0.7.0-linux-x64.tar.gz", "browser_download_url": "https://x/x64.tar.gz" },
            { "name": "o-view-0.7.0-linux-arm64.tar.gz","browser_download_url": "https://x/arm64.tar.gz" }
          ]
        }
        """;

    // ── the regression ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(InstallKind.LinuxPackage, Architecture.X64)]
    [InlineData(InstallKind.LinuxPackage, Architecture.Arm64)]
    [InlineData(InstallKind.LinuxTarball, Architecture.X64)]
    [InlineData(InstallKind.LinuxTarball, Architecture.Arm64)]
    public void ALinuxBuildCanTellThatANewerVersionExists(InstallKind kind, Architecture arch)
    {
        var result = UpdateCheck.Evaluate(
            "0.6.0", UnifiedRelease, UpdatePolicy.DetectionAsset(kind, arch));

        // The half that was missing. Before the split this was Unknown for LinuxPackage no
        // matter what the release contained.
        Assert.Equal(UpdateOutcome.UpdateAvailable, result.Outcome);
        Assert.Equal("v0.7.0", result.Available!.Tag);
    }

    /// <summary>
    /// The other half, unchanged: being able to *see* the asset must not make it installable.
    /// </summary>
    [Theory]
    [InlineData(InstallKind.LinuxPackage)]
    [InlineData(InstallKind.LinuxTarball)]
    [InlineData(InstallKind.WindowsPortable)]
    public void SeeingTheAssetDoesNotMakeItRunnable(InstallKind kind)
    {
        Assert.False(UpdatePolicy.MayDownloadAndRun(kind));
    }

    [Fact]
    public void OnlyTheInstalledWindowsBuildMayDownloadAndRun()
    {
        Assert.True(UpdatePolicy.MayDownloadAndRun(InstallKind.WindowsInstaller));
    }

    // ── each build detects on its own asset, never another platform's ────────────────

    [Theory]
    [InlineData(InstallKind.LinuxPackage, Architecture.X64, "amd64.deb")]
    [InlineData(InstallKind.LinuxPackage, Architecture.Arm64, "arm64.deb")]
    [InlineData(InstallKind.LinuxTarball, Architecture.X64, "x64.tar.gz")]
    [InlineData(InstallKind.LinuxTarball, Architecture.Arm64, "arm64.tar.gz")]
    public void EachLinuxBuildResolvesItsOwnArchitecture(
        InstallKind kind, Architecture arch, string expectedUrlSuffix)
    {
        var result = UpdateCheck.Evaluate(
            "0.6.0", UnifiedRelease, UpdatePolicy.DetectionAsset(kind, arch));

        Assert.EndsWith(expectedUrlSuffix, result.Available!.InstallerUrl, StringComparison.Ordinal);
    }

    /// <summary>
    /// The #79 regression, restated at the detection layer: no Linux build may ever resolve
    /// to a Windows asset, even when the release carries one.
    /// </summary>
    [Theory]
    [InlineData(InstallKind.LinuxPackage, Architecture.X64)]
    [InlineData(InstallKind.LinuxPackage, Architecture.Arm64)]
    [InlineData(InstallKind.LinuxTarball, Architecture.X64)]
    [InlineData(InstallKind.LinuxTarball, Architecture.Arm64)]
    public void NoLinuxBuildEverResolvesToAWindowsAsset(InstallKind kind, Architecture arch)
    {
        var result = UpdateCheck.Evaluate(
            "0.6.0", UnifiedRelease, UpdatePolicy.DetectionAsset(kind, arch));

        Assert.DoesNotContain(".exe", result.Available!.InstallerUrl, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An architecture no asset is published for must report Unknown, not point the user at
    /// a package that would not run on their machine (rule 6).
    /// </summary>
    [Theory]
    [InlineData(Architecture.X86)]
    [InlineData(Architecture.Arm)]
    public void AnUnpublishedArchitectureClaimsNothing(Architecture arch)
    {
        foreach (var kind in new[] { InstallKind.LinuxPackage, InstallKind.LinuxTarball })
        {
            var asset = UpdatePolicy.DetectionAsset(kind, arch);
            Assert.Equal(UpdateOutcome.Unknown,
                UpdateCheck.Evaluate("0.6.0", UnifiedRelease, asset).Outcome);
        }
    }

    // ── Windows must not regress ────────────────────────────────────────────────────

    [Theory]
    [InlineData(InstallKind.WindowsInstaller)]
    [InlineData(InstallKind.WindowsPortable)]
    public void BothWindowsBuildsStillDetectOnTheFrozenInstallerName(InstallKind kind)
    {
        var result = UpdateCheck.Evaluate(
            "0.6.0", UnifiedRelease, UpdatePolicy.DetectionAsset(kind, Architecture.X64));

        Assert.Equal(UpdateOutcome.UpdateAvailable, result.Outcome);
        Assert.EndsWith(ReleaseAssets.WindowsInstallerName, result.Available!.InstallerUrl, StringComparison.Ordinal);
    }

    /// <summary>
    /// The pre-v0.6.0 world, and the exact case a Linux build meets today: the latest
    /// release carries Windows assets only. It must find nothing rather than the .exe.
    /// </summary>
    [Fact]
    public void AWindowsOnlyReleaseOffersALinuxBuildNothing()
    {
        const string windowsOnly = """
            {
              "tag_name": "v0.5.11",
              "draft": false,
              "prerelease": false,
              "assets": [
                { "name": "O-view-Setup.exe", "browser_download_url": "https://x/O-view-Setup.exe" },
                { "name": "O-view.Tray.exe",  "browser_download_url": "https://x/O-view.Tray.exe" }
              ]
            }
            """;

        foreach (var kind in new[] { InstallKind.LinuxPackage, InstallKind.LinuxTarball })
        {
            var result = UpdateCheck.Evaluate(
                "0.5.0", windowsOnly, UpdatePolicy.DetectionAsset(kind, Architecture.X64));

            Assert.Equal(UpdateOutcome.Unknown, result.Outcome);
            Assert.Null(result.Available);
        }
    }

    // ── the name mapping, which is easy to get subtly wrong ─────────────────────────

    [Fact]
    public void DebianArchitectureNamesAreDebianSpellingsNotDotNetOnes()
    {
        Assert.Equal("amd64", UpdatePolicy.DebianArchitecture(Architecture.X64));
        Assert.Equal("arm64", UpdatePolicy.DebianArchitecture(Architecture.Arm64));
        Assert.Null(UpdatePolicy.DebianArchitecture(Architecture.X86));

        Assert.Equal("linux-x64", UpdatePolicy.RuntimeIdentifier(Architecture.X64));
        Assert.Equal("linux-arm64", UpdatePolicy.RuntimeIdentifier(Architecture.Arm64));
        Assert.Null(UpdatePolicy.RuntimeIdentifier(Architecture.X86));
    }

    /// <summary>
    /// The workflow writes these names and this code matches them. Asserted together so a
    /// rename on either side fails here rather than showing up as an app that quietly stops
    /// updating.
    /// </summary>
    [Fact]
    public void TheSelectorsMatchExactlyWhatTheReleaseWorkflowPublishes()
    {
        Assert.True(UpdatePolicy.DetectionAsset(InstallKind.LinuxPackage, Architecture.X64)
            .Matches("o-view_0.6.0_amd64.deb"));
        Assert.True(UpdatePolicy.DetectionAsset(InstallKind.LinuxTarball, Architecture.Arm64)
            .Matches("o-view-0.6.0-linux-arm64.tar.gz"));
        Assert.True(UpdatePolicy.DetectionAsset(InstallKind.WindowsInstaller, Architecture.X64)
            .Matches("O-view-Setup.exe"));

        // And do not cross-match, which is what an over-loose "contains" would do.
        Assert.False(UpdatePolicy.DetectionAsset(InstallKind.LinuxPackage, Architecture.X64)
            .Matches("o-view_0.6.0_arm64.deb"));
        Assert.False(UpdatePolicy.DetectionAsset(InstallKind.LinuxTarball, Architecture.X64)
            .Matches("o-view-0.6.0-linux-arm64.tar.gz"));
    }
}
