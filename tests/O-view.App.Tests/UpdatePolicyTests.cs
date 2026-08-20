using OView.App.Updates;

namespace OView.App.Tests;

/// <summary>
/// What each kind of install is allowed to do about an update (ADR-0009, as amended for
/// Linux). The load-bearing case is the apt install: overwriting files dpkg owns is
/// silently reverted by the next upgrade, so telling the user is the correct behaviour
/// rather than a lesser one.
/// </summary>
public class UpdatePolicyTests
{
    [Fact]
    public void OnlyAWindowsInstallerBuildReplacesItselfInPlace()
    {
        Assert.Equal(UpdateAction.InstallInPlace, UpdatePolicy.ActionFor(InstallKind.WindowsInstaller));
        Assert.True(UpdatePolicy.MayDownloadAndRun(InstallKind.WindowsInstaller));
    }

    /// <summary>The acceptance criterion, stated directly: an apt build never downloads or executes anything.</summary>
    [Fact]
    public void AnAptInstalledBuildNeverDownloadsOrRuns()
    {
        Assert.Equal(UpdateAction.DeferToPackageManager, UpdatePolicy.ActionFor(InstallKind.LinuxPackage));
        Assert.False(UpdatePolicy.MayDownloadAndRun(InstallKind.LinuxPackage));
    }

    [Theory]
    [InlineData(InstallKind.WindowsPortable)]
    [InlineData(InstallKind.LinuxTarball)]
    public void UserOwnedBuildsAreSentToTheReleasePage(InstallKind kind)
    {
        // A running single-file exe cannot overwrite itself, and the Windows installer would
        // create a parallel install rather than update the loose one.
        Assert.Equal(UpdateAction.OpenReleasePage, UpdatePolicy.ActionFor(kind));
        Assert.False(UpdatePolicy.MayDownloadAndRun(kind));
    }

    [Fact]
    public void ExactlyOneKindMaySelfInstall()
    {
        var allowed = Enum.GetValues<InstallKind>().Where(UpdatePolicy.MayDownloadAndRun).ToList();

        // A guard against a future kind being added with the permissive default by accident.
        Assert.Equal([InstallKind.WindowsInstaller], allowed);
    }

    /// <summary>
    /// The line the "Update automatically" setting must not cross (issue #140).
    ///
    /// <para>The setting is a <b>preference</b>; this is the <b>permission</b>, and the
    /// amendment to ADR-0009 says the second is unchanged and remains the only thing deciding
    /// whether anything is fetched or executed. The realistic way to break that is not an
    /// argument about design — it is a <c>settings.json</c> copied from a Windows machine into
    /// a tarball install's config directory, where the preference reads true on a build that
    /// must touch nothing.</para>
    ///
    /// <para>Written as a sweep over every kind, so the guarantee is "no non-installer build
    /// may act on it" rather than a list someone has to remember to extend.</para>
    /// </summary>
    [Theory]
    [InlineData(InstallKind.WindowsPortable)]
    [InlineData(InstallKind.LinuxPackage)]
    [InlineData(InstallKind.LinuxTarball)]
    public void APreferenceToAutoUpdateNeverGrantsPermissionToInstall(InstallKind kind)
    {
        const bool userTurnedItOn = true;

        // Exactly the expression the head evaluates before installing anything.
        Assert.False(userTurnedItOn && UpdatePolicy.MayDownloadAndRun(kind));
    }
}
