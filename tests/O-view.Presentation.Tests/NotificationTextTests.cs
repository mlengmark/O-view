using OView.Core.Providers.CachedUsage;
using OView.Core.Updates;
using OView.Presentation.Text;

namespace OView.Presentation.Tests;

/// <summary>
/// Every notification both heads raise, and the update confirmation dialog, asserted
/// character for character as the shells produced them before the extraction.
///
/// <para><b>A notification gets one pass.</b> A Windows balloon is gone in seconds and a
/// freedesktop notification may never be seen at all, so there is no re-reading and no
/// "where was that again?", which makes these the strings most worth pinning and the ones
/// whose only other verification was reading the call site.</para>
/// </summary>
public class NotificationTextTests
{
    private static readonly ReleaseVersion Next = new(0, 7, 1);
    private const string Current = "0.7.0";

    // ── the notices that are not about updates ──────────────────────────────────────

    /// <summary>
    /// A tray icon that blinks out and back unexplained reads as a crash, so the restart has
    /// to name its cause and say the click is worth repeating.
    /// </summary>
    [Fact]
    public void TheRestartNoticeExplainsItselfAndSaysWhatToDo()
    {
        Assert.Equal("O-view restarted", NoticeText.RestartedTitle);
        Assert.Equal(
            "A cleanup tool removed some of O-view's display files from your temp folder, so "
            + "O-view restarted to restore them. Click the icon again to open it.",
            NoticeText.RestartedBody);
    }

    /// <summary>
    /// The diagnostics bundle is on its way to a public issue, so the confirmation says what
    /// is not in it. That reassurance is what gets it pasted rather than read line by line
    /// first.
    /// </summary>
    [Fact]
    public void TheDiagnosticsNoticesSayWhatIsAndIsNotIncluded()
    {
        Assert.Equal("Diagnostics copied", NoticeText.DiagnosticsCopiedTitle);
        Assert.Equal(
            "Paste them into your bug report. No tokens or conversation content are included.",
            NoticeText.DiagnosticsCopiedBody);

        Assert.Equal("Couldn't copy diagnostics", NoticeText.DiagnosticsCopyFailedTitle);
        Assert.Equal(
            "The clipboard was unavailable. Please try again.",
            NoticeText.DiagnosticsCopyFailedBody);
    }

    /// <summary>
    /// The conflict notice's heading only has to say which thing disagrees;
    /// <see cref="PanelText.WeeklyResetConflict"/> under it carries the observed time and what
    /// to do (issue #186).
    /// </summary>
    [Fact]
    public void TheWeeklyResetConflictTitleNamesWhatDisagrees() =>
        Assert.Equal("Weekly reset time doesn't match", NoticeText.WeeklyResetConflictTitle);

    /// <summary>
    /// The threshold balloon's title is generic, the crossing itself is in the body, because
    /// one title is reused for every percentage a user may set (issue #141).
    /// </summary>
    [Fact]
    public void TheThresholdNoticeNamesTheCrossingInTheBody()
    {
        Assert.Equal("Claude usage", NoticeText.ThresholdTitle);
        Assert.Equal(
            "Session usage is at 95% of the 5-hour limit.", NoticeText.ThresholdBody(95));
    }

    // ── the missing tray host (ADR-0013 decision 2) ─────────────────────────────────

    /// <summary>
    /// The user has just launched something that appears to have done nothing at all, so the
    /// title leads with the app running, the fact that reframes the rest from a failure into
    /// a missing extension.
    /// </summary>
    [Fact]
    public void TheNoHostTitleLeadsWithTheAppRunning()
    {
        Assert.Equal(
            "O-view is running, but has nowhere to show its icon", NoticeText.NoTrayHostTitle);
        Assert.StartsWith("O-view is running", NoticeText.NoTrayHostTitle, StringComparison.Ordinal);
    }

    /// <summary>
    /// States what was observed rather than asserting anything about the machine (rule 6):
    /// "no host on the bus" is a fact, "GNOME is missing an extension" is a guess about why.
    /// It also promises the recovery, because the icon does reappear on its own once a host
    /// arrives and a user who assumed a restart was needed would never find that out.
    /// </summary>
    [Fact]
    public void TheAbsentHostSentenceReportsTheBusAndNamesTheRecovery()
    {
        Assert.Equal(
            "O-view found no notification-area host on the session bus, so its icon has "
            + "nowhere to appear. GNOME does not provide one by default, installing an "
            + "AppIndicator/KStatusNotifierItem extension adds it, and O-view will pick it up "
            + "without needing a restart.",
            NoticeText.TrayHostAbsent);
    }

    /// <summary>
    /// "I could not ask" and "I asked and nothing was there" are different facts, and only
    /// one of them justifies telling the user to install an extension. So the unknown case
    /// must claim nothing about hosts or about GNOME.
    /// </summary>
    [Fact]
    public void TheUnreachableBusSentenceClaimsNothingAboutHosts()
    {
        Assert.Equal(
            "O-view could not reach the session bus to check for a notification-area host.",
            NoticeText.TrayHostUnknown);

        Assert.DoesNotContain("GNOME", NoticeText.TrayHostUnknown, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "extension", NoticeText.TrayHostUnknown, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(NoticeText.TrayHostAbsent, NoticeText.TrayHostUnknown);
    }

    [Fact]
    public void ThePresentHostSentenceSaysSo() =>
        Assert.Equal("A notification-area host is running.", NoticeText.TrayHostPresent);

    // ── the off-plan balloon (issue #259) ───────────────────────────────────────────

    /// <summary>
    /// An account with extra usage off is not billing beyond its plan, and saying otherwise
    /// was not a hedge but a false statement about the reader's money, so only the enabled
    /// state keeps the billing claim.
    /// </summary>
    [Fact]
    public void OnlyTheEnabledStateClaimsBillingBeyondThePlan()
    {
        Assert.Equal(
            "Usage is billing beyond your plan", OffPlanText.NotificationTitle(ExtraUsageState.Enabled));
        Assert.Equal(
            "Usage is not drawing from your plan", OffPlanText.NotificationTitle(ExtraUsageState.Disabled));
        Assert.Equal(
            "Usage is not drawing from your plan", OffPlanText.NotificationTitle(ExtraUsageState.Unknown));
    }

    /// <summary>
    /// The estimate is the API-rate VALUE of the work, not a charge, withheld beside "extra
    /// usage is switched off" because a balloon has no room to qualify the two against each
    /// other, unlike the panel.
    /// </summary>
    [Fact]
    public void TheEstimateIsWithheldOnlyWhereNothingCanBeBilled()
    {
        Assert.Equal(
            "Work this session isn't drawing from your plan allowance. Est. $92.75 so far this "
            + "window. Extra usage is switched on for this account. Open O-view for detail.",
            OffPlanText.NotificationBody(92.75m, ExtraUsageState.Enabled));

        Assert.Equal(
            "Work this session isn't drawing from your plan allowance. Extra usage is switched "
            + "off, so this should not be billing beyond your plan. Open O-view for detail.",
            OffPlanText.NotificationBody(92.75m, ExtraUsageState.Disabled));

        Assert.DoesNotContain(
            "Est.", OffPlanText.NotificationBody(92.75m, ExtraUsageState.Disabled), StringComparison.Ordinal);
    }

    /// <summary>
    /// Unknown withholds the setting sentence, same as Disabled would, but <b>not</b> the
    /// estimate, the two are read from different sources, and a figure already built from
    /// the plan-history window does not become less true because the cached block was
    /// absent. Only Disabled, which states the figure cannot be billed, withholds it.
    /// </summary>
    [Fact]
    public void UnknownWithholdsTheSettingButNotAnAlreadyBuiltEstimate() =>
        Assert.Equal(
            "Work this session isn't drawing from your plan allowance. Est. $92.75 so far this "
            + "window. Open O-view for detail.",
            OffPlanText.NotificationBody(92.75m, ExtraUsageState.Unknown));

    /// <summary>
    /// Nothing read and nothing to report (rule 6): no estimate and an unread setting states
    /// the observation and stops there.
    /// </summary>
    [Fact]
    public void WithNoEstimateAndNothingReadOnlyTheObservationIsStated() =>
        Assert.Equal(
            "Work this session isn't drawing from your plan allowance. Open O-view for detail.",
            OffPlanText.NotificationBody(null, ExtraUsageState.Unknown));

    // ── the update notifications ────────────────────────────────────────────────────

    [Fact]
    public void TheAvailableNoticeNamesBothVersionsAndTheRoute()
    {
        Assert.Equal("O-view update available", UpdateText.AvailableTitle);
        Assert.Equal(
            "Version 0.7.1 is available (you have 0.7.0). Right-click the icon → Check for "
            + "updates to install.",
            UpdateText.AvailableBody(Next, Current));
    }

    /// <summary>
    /// Automatic is not silent (ADR-0009 as amended, issue #140): the balloon goes out before
    /// anything is downloaded, names the version, warns about the restart, and says how to
    /// turn the behaviour off. Invisibility was the worst property of the alternative ADR-0009
    /// rejected, and what the setting removes is the per-release click, not the knowledge.
    /// </summary>
    [Fact]
    public void TheAutomaticInstallSaysItIsHappeningAndHowToStopIt()
    {
        Assert.Equal("Installing O-view 0.7.1", UpdateText.InstallingTitle(Next));
        Assert.Equal(
            "You have 0.7.0. O-view will close briefly and reopen. Turn this off with Update "
            + "automatically in the tray menu.",
            UpdateText.InstallingBody(Current));
    }

    /// <summary>
    /// The one case that earns an error badge, and the one that must never offer the releases
    /// page: the bytes that arrived were not the bytes the release published, so sending the
    /// reader to download it by hand would hand them exactly the file the check rejected. The
    /// reassurance about the current version is the other half, nothing on the machine
    /// changed.
    /// </summary>
    [Fact]
    public void TheChecksumMismatchSaysWhatWasObservedAndOffersNoDownload()
    {
        Assert.Equal("Update not installed", UpdateText.ChecksumMismatchTitle);
        Assert.Equal(
            "The download didn't match the checksum published with this release, so O-view "
            + "didn't run it. Your current version is untouched.",
            UpdateText.ChecksumMismatchBody);

        Assert.DoesNotContain(
            "releases page", UpdateText.ChecksumMismatchBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "try again", UpdateText.ChecksumMismatchBody, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The automatic path's download failure promises the next scheduled attempt rather than
    /// opening a browser, because nobody is watching it, and that is the whole reason it is
    /// worded apart from <see cref="UpdateText.FailedBody"/>.
    /// </summary>
    [Fact]
    public void TheAutomaticFailurePromisesARetryRatherThanABrowser()
    {
        Assert.Equal("Update didn't install", UpdateText.AutomaticFailedTitle);
        Assert.Equal(
            "O-view couldn't download version 0.7.1. It will try again later, or use Check "
            + "for updates to install it now.",
            UpdateText.AutomaticFailedBody(Next));

        Assert.NotEqual(UpdateText.FailedBody, UpdateText.AutomaticFailedBody(Next));
    }

    /// <summary>
    /// The confirmed path's failure says what it is about to do rather than asking: the user
    /// confirmed an install a moment ago, so the releases page opening continues that answer.
    /// </summary>
    [Fact]
    public void TheConfirmedFailureAnnouncesTheReleasesPage()
    {
        Assert.Equal("Update failed", UpdateText.FailedTitle);
        Assert.Equal(
            "O-view couldn't download the update. Opening the releases page so you can "
            + "download it manually.",
            UpdateText.FailedBody);
    }

    /// <summary>
    /// The interactive check always reports an outcome, silence would be indistinguishable
    /// from a check that never ran, and names the version, so the answer cannot be mistaken
    /// for a stale one.
    /// </summary>
    [Fact]
    public void TheUpToDateNoticeNamesTheVersionItIsSureAbout()
    {
        Assert.Equal("O-view is up to date", UpdateText.UpToDateTitle);
        Assert.Equal("You have the latest version (0.7.0).", UpdateText.UpToDateBody(Current));
    }

    /// <summary>
    /// A throttled check is named apart from an unreachable GitHub because the remedy differs
    /// and the cause is usually not this machine, the limit is per IP address, so a shared
    /// network exhausts it without this user making a request (issue #176). Telling them
    /// their connection failed sent them to debug the wrong thing.
    /// </summary>
    [Fact]
    public void AThrottledCheckIsNotReportedAsAFailedOne()
    {
        Assert.Equal("Update check rate limited", UpdateText.RateLimitedTitle);
        Assert.Equal("Couldn't check for updates", UpdateText.CheckFailedTitle);
        Assert.NotEqual(UpdateText.RateLimitedTitle, UpdateText.CheckFailedTitle);

        Assert.Equal(
            "O-view couldn't reach GitHub to check for a newer version. Please try again later.",
            UpdateText.CheckFailedBody);
    }

    // ── the confirmation dialog ─────────────────────────────────────────────────────

    /// <summary>
    /// Both questions carry the same version sentence, assembled once. They were built at two
    /// call sites from the same interpolation, which is two places for one of them to drift.
    /// </summary>
    [Fact]
    public void BothQuestionsSitUnderTheSameVersionSentence()
    {
        Assert.Equal("Update available", UpdateText.DialogTitle);

        Assert.Equal(
            "O-view 0.7.1 is available, you have 0.7.0. Download and install it now?",
            UpdateText.DialogMessage(Next, Current, UpdateText.InstallQuestion));

        Assert.Equal(
            "O-view 0.7.1 is available, you have 0.7.0. Open the download page for the new version?",
            UpdateText.DialogMessage(Next, Current, UpdateText.OpenPageQuestion));
    }

    /// <summary>
    /// The primary button names the action rather than answering "Yes" to a question the
    /// reader would have to re-read to be sure of, and the two paths do different things, so
    /// they cannot share a label.
    /// </summary>
    [Fact]
    public void ThePrimaryButtonNamesWhichActionItIs()
    {
        Assert.Equal("Update now", UpdateText.InstallConfirm);
        Assert.Equal("Open page", UpdateText.OpenPageConfirm);
        Assert.Equal("Not now", UpdateText.DialogCancel);
        Assert.NotEqual(UpdateText.InstallConfirm, UpdateText.OpenPageConfirm);
    }

    /// <summary>
    /// The app vanishing and returning is surprising enough to state before it happens. It
    /// belongs to the install question only, the download page opens no installer and closes
    /// nothing.
    /// </summary>
    [Fact]
    public void OnlyTheInstallQuestionWarnsAboutTheRestart()
    {
        Assert.Equal("O-view will close briefly and reopen automatically.", UpdateText.InstallDetail);
        Assert.DoesNotContain("close", UpdateText.OpenPageQuestion, StringComparison.OrdinalIgnoreCase);
    }

    // ── the notice-only heads (ADR-0009 as amended for Linux) ───────────────────────

    [Fact]
    public void TheNoticeTitleCarriesTheVersion() =>
        Assert.Equal("O-view 0.7.1 is available", UpdateText.NoticeTitle(Next));

    /// <summary>
    /// The .deb notice deliberately does not say "run apt upgrade". There is no O-view apt
    /// repository, so that command would report nothing to do and the user would reasonably
    /// conclude the notification was wrong (rule 6). It names the real step, and says whose
    /// files these are, which is why O-view is not doing it for them.
    /// </summary>
    [Fact]
    public void ThePackageNoticeNamesTheRealStepAndNotAptUpgrade()
    {
        var body = UpdateText.PackageNoticeBody("https://example.invalid/releases/v0.7.1");

        Assert.Equal(
            "Download the new .deb from https://example.invalid/releases/v0.7.1 and install "
            + "it with sudo apt install. O-view will not update itself: these files belong to "
            + "your package manager.",
            body);

        Assert.DoesNotContain("apt upgrade", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The tarball has no package manager to defer to, so the step is unpacking over what is
    /// there, and the two notices must not be interchangeable, because following the wrong
    /// one does nothing.
    /// </summary>
    [Fact]
    public void TheTarballNoticeNamesUnpackingAndDiffersFromThePackageOne()
    {
        var url = "https://example.invalid/releases/v0.7.1";

        Assert.Equal(
            $"Download the new tarball from {url} and extract it over your existing copy. "
            + "O-view will not update itself.",
            UpdateText.TarballNoticeBody(url));

        Assert.NotEqual(UpdateText.TarballNoticeBody(url), UpdateText.PackageNoticeBody(url));
    }

    /// <summary>
    /// Every one of these says O-view will not install anything, on both install kinds. It is
    /// the sentence that keeps the notice from reading as a promise the head cannot keep.
    /// </summary>
    [Fact]
    public void BothNoticesSayOViewWillNotUpdateItself()
    {
        var url = "https://example.invalid/releases/v0.7.1";

        foreach (var body in new[] { UpdateText.PackageNoticeBody(url), UpdateText.TarballNoticeBody(url) })
        {
            Assert.Contains("will not update itself", body, StringComparison.Ordinal);
            Assert.Contains(url, body, StringComparison.Ordinal);
        }
    }
}
