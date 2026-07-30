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
}
