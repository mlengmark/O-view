using OView.Core.Updates;

namespace OView.Presentation.Text;

/// <summary>
/// What the update flow says: the notifications and the confirmation dialog.
///
/// <para><b>Why all of it belongs together.</b> ADR-0009 gives the same release three endings
/// depending on how O-view was installed — install it, open the download page, or name the
/// file to fetch by hand — and the honesty of each one is relative to the others. A failure
/// that promises a retry must not appear on the path that has no retry; a checksum mismatch
/// must not offer the releases page, because that hands the user exactly the file the check
/// rejected. Those are decisions about the <i>set</i>, and they were spread across two heads
/// with no way to read them side by side.</para>
///
/// <para>The newer version is a <see cref="ReleaseVersion"/> wherever one is named, because
/// that is what the feed produced. The <i>current</i> version is a string throughout: it
/// comes off the running assembly, which can carry something three numbers do not
/// describe.</para>
/// </summary>
public static class UpdateText
{
    // ── the quiet background check ──────────────────────────────────────────────────

    public const string AvailableTitle = "O-view update available";

    /// <summary>Names the menu path rather than describing it, because that is the next thing
    /// the reader has to find.</summary>
    public static string AvailableBody(ReleaseVersion newVersion, string currentVersion) =>
        $"Version {newVersion} is available (you have {currentVersion}). "
        + "Right-click the icon → Check for updates to install.";

    // ── the automatic path ──────────────────────────────────────────────────────────

    /// <summary>
    /// Heads the balloon that goes out <b>before</b> anything is downloaded on the automatic
    /// path (ADR-0009 as amended, issue #140). Automatic is not silent: invisibility was the
    /// worst property of the alternative ADR-0009 rejected, and what the setting removes is
    /// the per-release click, not the user's knowledge.
    /// </summary>
    public static string InstallingTitle(ReleaseVersion newVersion) =>
        $"Installing O-view {newVersion}";

    /// <summary>Warns that the app will disappear and come back, and says how to stop this
    /// happening again — the one thing a user who did not expect it needs.</summary>
    public static string InstallingBody(string currentVersion) =>
        $"You have {currentVersion}. O-view will close briefly and reopen. "
        + "Turn this off with Update automatically in the tray menu.";

    /// <summary>
    /// The one case that earns an error badge: the bytes that arrived were not the bytes the
    /// release published, and O-view refused to run them. Shared by the automatic and the
    /// confirmed path, because the fact and the reassurance are identical.
    /// </summary>
    public const string ChecksumMismatchTitle = "Update not installed";

    /// <inheritdoc cref="ChecksumMismatchTitle"/>
    public const string ChecksumMismatchBody =
        "The download didn't match the checksum published with this release, so O-view "
        + "didn't run it. Your current version is untouched.";

    /// <summary>
    /// A download that failed on the automatic path. Worded apart from
    /// <see cref="FailedTitle"/> because nobody is watching this one: it promises the next
    /// scheduled attempt rather than opening a browser window unbidden.
    /// </summary>
    public const string AutomaticFailedTitle = "Update didn't install";

    /// <inheritdoc cref="AutomaticFailedTitle"/>
    public static string AutomaticFailedBody(ReleaseVersion newVersion) =>
        $"O-view couldn't download version {newVersion}. It will try again later, or use "
        + "Check for updates to install it now.";

    // ── the interactive check ───────────────────────────────────────────────────────

    /// <summary>The check always reports an outcome; silence would be indistinguishable from
    /// one that never ran.</summary>
    public const string UpToDateTitle = "O-view is up to date";

    /// <summary>Names the version, so the reader can tell this answer from a stale one.</summary>
    public static string UpToDateBody(string currentVersion) =>
        $"You have the latest version ({currentVersion}).";

    /// <summary>
    /// Named apart from an unreachable GitHub because the remedy differs and the cause is
    /// usually not this machine (issue #176). <see cref="PanelText.RateLimitedNotice"/> under
    /// it explains the shared limit.
    /// </summary>
    public const string RateLimitedTitle = "Update check rate limited";

    public const string CheckFailedTitle = "Couldn't check for updates";

    /// <summary>A retryable condition on the user's own network, so it says so and claims
    /// nothing about the install.</summary>
    public const string CheckFailedBody =
        "O-view couldn't reach GitHub to check for a newer version. Please try again later.";

    /// <summary>A download that failed with a human already in the loop.</summary>
    public const string FailedTitle = "Update failed";

    /// <summary>Says what it is about to do rather than asking: the user confirmed an install
    /// a moment ago, so the releases page opening continues that answer.</summary>
    public const string FailedBody =
        "O-view couldn't download the update. Opening the releases page so you can download "
        + "it manually.";

    // ── the confirmation dialog ─────────────────────────────────────────────────────

    public const string DialogTitle = "Update available";

    /// <summary>
    /// Both versions and then the question, which differs by how this build was installed.
    /// Taking the question as an argument keeps the two halves from being assembled
    /// differently at the two call sites.
    /// </summary>
    public static string DialogMessage(
        ReleaseVersion newVersion, string currentVersion, string question) =>
        $"O-view {newVersion} is available — you have {currentVersion}. {question}";

    /// <summary>Asked of a build the user or a package manager owns, which O-view must not
    /// overwrite.</summary>
    public const string OpenPageQuestion = "Open the download page for the new version?";

    /// <summary>Names the action rather than answering "Yes" to a question the reader would
    /// have to re-read to be sure of. So does <see cref="InstallConfirm"/>.</summary>
    public const string OpenPageConfirm = "Open page";

    /// <summary>Asked of a build that can replace itself in place.</summary>
    public const string InstallQuestion = "Download and install it now?";

    /// <inheritdoc cref="OpenPageConfirm"/>
    public const string InstallConfirm = "Update now";

    /// <summary>Under the install question only: the app vanishing and returning is
    /// surprising enough to state before it happens rather than explain afterwards.</summary>
    public const string InstallDetail = "O-view will close briefly and reopen automatically.";

    public const string DialogCancel = "Not now";

    // The notice-only heads — the two bodies a build that checks but never installs sends
    // instead (ADR-0009 as amended for Linux) — belong here beside the rest of the flow, and
    // are the one part of this move still waiting: LinuxUpdateNotice lives under a guarded
    // path, which this slice was not cleared to change.
}
