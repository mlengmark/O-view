using OView.App.Updates;
using OView.Core.Models;
using OView.Core.Updates;
using OView.Linux.Updates;

namespace OView.Linux.Tests;

/// <summary>
/// The Linux update notice. Its whole job is to say a newer version exists and then do
/// nothing, so the tests are mostly about what it must <i>not</i> do.
/// </summary>
public class LinuxUpdateNoticeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("oview-notice-").FullName;

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    // ── the message ─────────────────────────────────────────────────────────────────

    private static AvailableUpdate Update =>
        new(new ReleaseVersion(0, 7, 0), "v0.7.0", "https://x/o-view_0.7.0_amd64.deb");

    [Fact]
    public void TheTitleNamesTheVersionSoTheNoticeIsSelfExplanatory()
    {
        Assert.Contains("0.7.0", LinuxUpdateNotice.NoticeTitle(Update), StringComparison.Ordinal);
    }

    /// <summary>
    /// The notice must not tell a .deb user to run "apt upgrade". There is no O-view apt
    /// repository, so that command reports nothing to do — and a user who runs it and sees
    /// nothing reasonably concludes the notification was wrong. Rule 6: do not assert
    /// something about the user's machine that is not true of it.
    /// </summary>
    [Fact]
    public void ThePackageNoticeDoesNotClaimAptUpgradeWillFindIt()
    {
        var body = LinuxUpdateNotice.NoticeBody(InstallKind.LinuxPackage, Update);

        Assert.DoesNotContain("apt upgrade", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("apt-get upgrade", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EachInstallKindIsToldWhatToActuallyDo()
    {
        var package = LinuxUpdateNotice.NoticeBody(InstallKind.LinuxPackage, Update);
        var tarball = LinuxUpdateNotice.NoticeBody(InstallKind.LinuxTarball, Update);

        Assert.Contains(".deb", package, StringComparison.Ordinal);
        Assert.Contains("tarball", tarball, StringComparison.OrdinalIgnoreCase);

        // Both link to the release, and both say plainly that nothing happens on its own.
        foreach (var body in new[] { package, tarball })
        {
            Assert.Contains("github.com/mlengmark/O-view/releases/tag/v0.7.0", body, StringComparison.Ordinal);
            Assert.Contains("will not update itself", body, StringComparison.OrdinalIgnoreCase);
        }

        Assert.NotEqual(package, tarball);
    }

    // ── behaviour ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// A check that reaches no network must be silent. "I could not tell" is not "you are up
    /// to date", and it is certainly not a notification.
    /// </summary>
    [Fact]
    public async Task AFailedCheckNotifiesNothingAndThrowsNothing()
    {
        var shown = new List<string>();
        var notice = new LinuxUpdateNotice(
            InstallKind.LinuxPackage,
            "0.6.0",
            (title, _) => { shown.Add(title); return Task.CompletedTask; },
            log: null,
            feed: new ReleaseFeed(),          // real feed, unreachable host below
            settingsPath: SettingsPath);

        // No network in CI; whatever happens, this must not throw and must not notify on a
        // result it could not establish.
        await notice.CheckAsync(new CancellationTokenSource(TimeSpan.FromMilliseconds(1)).Token);

        Assert.Empty(shown);
    }

    /// <summary>
    /// The guard, stated as a test because it is the property the whole design rests on: an
    /// install kind that may self-install is refused rather than quietly served.
    /// </summary>
    [Fact]
    public async Task AKindThatMaySelfInstallIsRefusedOutright()
    {
        var shown = new List<string>();
        var notice = new LinuxUpdateNotice(
            InstallKind.WindowsInstaller,     // wrong head; must not be acted on
            "0.6.0",
            (title, _) => { shown.Add(title); return Task.CompletedTask; },
            settingsPath: SettingsPath);

        await notice.CheckAsync();

        Assert.Empty(shown);
        Assert.False(File.Exists(SettingsPath));   // nothing recorded either
    }

    // ── "once per version", which is the difference between informing and nagging ────

    [Fact]
    public void ARecordedTagSurvivesARestart()
    {
        new TraySettings(LastUpdateNoticeTag: "v0.7.0").Save(SettingsPath);

        Assert.Equal("v0.7.0", TraySettings.Load(SettingsPath).LastUpdateNoticeTag);
    }

    /// <summary>
    /// Settings files written before v0.6.0 have no such field. They must load as "never
    /// told" rather than failing and resetting every other preference with them.
    /// </summary>
    [Fact]
    public void AnOlderSettingsFileWithoutTheFieldStillLoads()
    {
        File.WriteAllText(SettingsPath, """{"NotifyOnThreshold":false,"ThresholdPercent":85}""");

        var loaded = TraySettings.Load(SettingsPath);

        Assert.Equal("", loaded.LastUpdateNoticeTag);
        Assert.False(loaded.NotifyOnThreshold);
        Assert.Equal(85, loaded.ThresholdPercent);
    }

    /// <summary>Recording the notice must not disturb the preferences beside it.</summary>
    [Fact]
    public void RecordingTheNoticeLeavesOtherSettingsAlone()
    {
        new TraySettings(NotifyOnThreshold: false, ThresholdPercent: 85).Save(SettingsPath);

        var updated = TraySettings.Load(SettingsPath) with { LastUpdateNoticeTag = "v0.7.0" };
        updated.Save(SettingsPath);

        var reloaded = TraySettings.Load(SettingsPath);
        Assert.Equal("v0.7.0", reloaded.LastUpdateNoticeTag);
        Assert.False(reloaded.NotifyOnThreshold);
        Assert.Equal(85, reloaded.ThresholdPercent);
    }
}
