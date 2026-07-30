namespace OView.Core.Updates;

/// <summary>
/// Recognises one platform's release asset by name. Handed to
/// <see cref="UpdateCheck.Evaluate"/> so the comparison stays a pure predicate and Core
/// never needs to know what OS it is running on.
/// </summary>
/// <param name="Description">What this looks for, for diagnostics and messages.</param>
/// <param name="Matches">Whether a published asset name is the one this build can install.</param>
public sealed record ReleaseAssetSelector(string Description, Func<string, bool> Matches);

/// <summary>
/// The names the release workflow publishes, and how each platform recognises its own.
///
/// <para><b>One decision in two places, so it lives here once.</b> The workflow writes
/// these names and the update checker matches them; if they are restated separately they
/// will drift, and the symptom is an app that quietly stops updating.</para>
///
/// <para><b>The Windows names are frozen.</b> Every already-installed Windows build looks
/// for the literal string <c>O-view-Setup.exe</c>. Renaming it would strand every existing
/// user on their current version — they would never see the release that fixed it. That is
/// why the Windows names carry no version or architecture while the Linux ones do, and why
/// matching cannot be a simple equality test on both platforms.</para>
/// </summary>
public static class ReleaseAssets
{
    /// <summary>Frozen — see the class remarks before changing.</summary>
    public const string WindowsInstallerName = "O-view-Setup.exe";

    /// <summary>Frozen. The portable exe, offered as a manual download.</summary>
    public const string WindowsPortableName = "O-view.Tray.exe";

    /// <summary>The Inno Setup installer, which can replace an installed build in place (ADR-0009).</summary>
    public static ReleaseAssetSelector WindowsInstaller { get; } = new(
        WindowsInstallerName,
        name => string.Equals(name, WindowsInstallerName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A Debian package for one architecture, e.g. <c>o-view_0.6.0_amd64.deb</c>. Matched by
    /// shape rather than equality because the name carries the version.
    /// </summary>
    public static ReleaseAssetSelector DebianPackage(string architecture) => new(
        $"o-view_<version>_{architecture}.deb",
        name => name.StartsWith("o-view_", StringComparison.Ordinal)
                && name.EndsWith($"_{architecture}.deb", StringComparison.Ordinal));

    /// <summary>
    /// The portable Linux tarball for one runtime identifier, e.g.
    /// <c>o-view-0.6.0-linux-x64.tar.gz</c>.
    /// </summary>
    public static ReleaseAssetSelector Tarball(string runtimeIdentifier) => new(
        $"o-view-<version>-{runtimeIdentifier}.tar.gz",
        name => name.StartsWith("o-view-", StringComparison.Ordinal)
                && name.EndsWith($"-{runtimeIdentifier}.tar.gz", StringComparison.Ordinal));

    /// <summary>
    /// Matches nothing. For a build that must never install anything it finds — an
    /// apt-managed Linux install, where overwriting package-manager files is actively
    /// harmful (ADR-0009). Such a build still *checks*, so it can tell the user a newer
    /// version exists; it simply has no asset to act on.
    /// </summary>
    public static ReleaseAssetSelector None { get; } = new("(no installable asset)", static _ => false);
}
