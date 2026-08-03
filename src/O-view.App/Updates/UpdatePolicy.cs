using System.Runtime.InteropServices;
using OView.Core.Updates;

namespace OView.App.Updates;

/// <summary>How this build arrived on the machine, which decides what it may do about updates.</summary>
public enum InstallKind
{
    /// <summary>Windows, installed by the Inno Setup installer into <c>%LOCALAPPDATA%\Programs\O-view</c> (ADR-0008).</summary>
    WindowsInstaller,

    /// <summary>Windows, a loose exe the user downloaded and runs from wherever they put it.</summary>
    WindowsPortable,

    /// <summary>Linux, installed by apt/dpkg. The package manager owns these files.</summary>
    LinuxPackage,

    /// <summary>Linux, extracted from the tarball. Owned by nobody but the user.</summary>
    LinuxTarball,
}

/// <summary>What O-view should do when a newer release exists.</summary>
public enum UpdateAction
{
    /// <summary>Download the installer and hand off to it, then exit so it can replace the exe (ADR-0009).</summary>
    InstallInPlace,

    /// <summary>Open the release page and let the user download it themselves.</summary>
    OpenReleasePage,

    /// <summary>Say a newer version exists and stop. The package manager does the work.</summary>
    DeferToPackageManager,
}

/// <summary>
/// The rule connecting the two, in one place because it is a decision rather than a
/// mechanism (ADR-0009, as amended for Linux).
///
/// <para><b>Why an apt install must not self-update.</b> Anthropic's own Claude Desktop for
/// Linux does not, and the convention exists for a good reason: files installed by dpkg are
/// owned by dpkg. An app that overwrites them is silently reverted by the next
/// <c>apt upgrade</c> — or leaves the package database describing a version that is no longer
/// on disk. Telling the user is the correct behaviour, not a lesser one.</para>
///
/// <para>A build that cannot self-install still <i>checks</i>, so it can say a newer version
/// exists. What it must never do is download or execute anything.</para>
/// </summary>
public static class UpdatePolicy
{
    public static UpdateAction ActionFor(InstallKind kind) => kind switch
    {
        InstallKind.WindowsInstaller => UpdateAction.InstallInPlace,
        InstallKind.LinuxPackage => UpdateAction.DeferToPackageManager,
        // A running single-file exe cannot overwrite itself, and the Windows installer would
        // create a parallel install beside the loose exe rather than update it.
        _ => UpdateAction.OpenReleasePage,
    };

    /// <summary>
    /// Whether this build may download an asset and run it. False for anything the user or a
    /// package manager owns — the property acceptance criteria are written against.
    /// </summary>
    public static bool MayDownloadAndRun(InstallKind kind) =>
        ActionFor(kind) is UpdateAction.InstallInPlace;

    /// <summary>
    /// Which published asset proves to <i>this</i> build that a newer version exists.
    ///
    /// <para><b>Detection is not permission, and conflating them silently disabled the
    /// Linux notice.</b> An apt build was handed <see cref="ReleaseAssets.None"/> on the
    /// reasoning that it must never install anything — but
    /// <see cref="UpdateCheck.Evaluate"/> only reports <c>UpdateAvailable</c> when the
    /// selector matches something, so a selector matching nothing meant an apt build could
    /// never report an update <i>at all</i>. It returned <c>Unknown</c> forever. ADR-0009
    /// says that build should "say a newer version exists, and stop"; with no asset to
    /// recognise, it could not do the first half.</para>
    ///
    /// <para>So detection uses the asset that build would actually install, and
    /// <see cref="MayDownloadAndRun"/> — unchanged — remains the only thing that decides
    /// whether anything is downloaded or executed. A <c>.deb</c> build now recognises the
    /// <c>.deb</c>, says so, and still touches nothing.</para>
    ///
    /// <para>An architecture this app does not publish for yields
    /// <see cref="ReleaseAssets.None"/>, so it reports <c>Unknown</c> rather than pointing
    /// the user at a package that would not run.</para>
    /// </summary>
    public static ReleaseAssetSelector DetectionAsset(InstallKind kind, Architecture architecture) => kind switch
    {
        // Both Windows kinds detect on the installer: it is the asset that proves a release
        // is real and complete. Only the installed one is allowed to act on it.
        InstallKind.WindowsInstaller or InstallKind.WindowsPortable => ReleaseAssets.WindowsInstaller,

        InstallKind.LinuxPackage => DebianArchitecture(architecture) is { } debArch
            ? ReleaseAssets.DebianPackage(debArch)
            : ReleaseAssets.None,

        InstallKind.LinuxTarball => RuntimeIdentifier(architecture) is { } rid
            ? ReleaseAssets.Tarball(rid)
            : ReleaseAssets.None,

        _ => ReleaseAssets.None,
    };

    /// <summary>Debian's architecture names, which are not .NET's.</summary>
    public static string? DebianArchitecture(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "amd64",
        Architecture.Arm64 => "arm64",
        _ => null,
    };

    /// <summary>The .NET runtime identifiers the release workflow builds tarballs for.</summary>
    public static string? RuntimeIdentifier(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "linux-x64",
        Architecture.Arm64 => "linux-arm64",
        _ => null,
    };
}
