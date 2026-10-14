using System.Runtime.InteropServices;
using OView.App;
using OView.App.Updates;
using OView.Core.Models;
using OView.Core.Updates;

namespace OView.Linux.Updates;

/// <summary>
/// Tells the user a newer O-view exists, and stops there.
///
/// <para><b>Why this exists.</b> ADR-0009 (as amended for Linux) specifies that a build which
/// cannot replace itself still <i>checks</i>, so it can say a newer version is out. Until
/// v0.6.0 that half was simply missing: the Linux head never subscribed to the engine's
/// update-check event, so it never checked. Combined with the <c>.deb</c> installing no apt
/// source — <c>apt upgrade</c> cannot learn about a version it has no repository for — a
/// Linux user had <b>no update path at all</b>. Install once, and never find out anything
/// had shipped.</para>
///
/// <para><b>What it will never do.</b> Download, extract, execute, or write anything outside
/// O-view's own settings file. Files installed by dpkg belong to dpkg, and a tarball belongs
/// to whoever unpacked it. The check is a read and a notification, and
/// <see cref="UpdatePolicy.MayDownloadAndRun"/> is asserted rather than assumed, so a future
/// edit that made this head "helpfully" install something trips the guard instead of
/// shipping.</para>
/// </summary>
public sealed class LinuxUpdateNotice
{
    private readonly ReleaseFeed _feed;
    private readonly Func<string, string, Task> _notify;
    private readonly IAppLog? _log;
    private readonly InstallKind _installKind;
    private readonly string _currentVersion;
    private readonly string? _settingsPath;

    public LinuxUpdateNotice(
        InstallKind installKind,
        string currentVersion,
        Func<string, string, Task> notify,
        IAppLog? log = null,
        ReleaseFeed? feed = null,
        string? settingsPath = null)
    {
        _installKind = installKind;
        _currentVersion = currentVersion;
        _notify = notify;
        _log = log;
        _feed = feed ?? new ReleaseFeed(log);
        _settingsPath = settingsPath;
    }

    /// <summary>
    /// One check. Safe to call on a timer; safe to call when offline. Never throws — a
    /// failed update check must not disturb an app that is otherwise working.
    /// </summary>
    public async Task CheckAsync(CancellationToken cancellation = default)
    {
        try
        {
            // The guard, stated rather than trusted. If this head is ever given an install
            // kind that may self-install, that is a bug in the caller and not something to
            // act on quietly.
            if (UpdatePolicy.MayDownloadAndRun(_installKind))
            {
                _log?.Write($"update notice refused: {_installKind} may self-install, which this head must not do");
                return;
            }

            var asset = UpdatePolicy.DetectionAsset(_installKind, RuntimeInformation.OSArchitecture);
            var result = await _feed.CheckAsync(_currentVersion, asset, cancellation).ConfigureAwait(false);

            if (result.Outcome is not UpdateOutcome.UpdateAvailable || result.Available is not { } update)
            {
                return;
            }

            var settings = TraySettings.Load(_settingsPath);
            if (string.Equals(settings.LastUpdateNoticeTag, update.Tag, StringComparison.Ordinal))
            {
                return;   // already said so; saying it daily is nagging, not informing
            }

            await _notify(NoticeTitle(update), NoticeBody(_installKind, update)).ConfigureAwait(false);

            // Recorded only after the notification was attempted, so a crash mid-notify
            // leaves the user still due to be told rather than silently marked as informed.
            (settings with { LastUpdateNoticeTag = update.Tag }).Save(_settingsPath);
            _log?.Write($"update notice shown for {update.Tag} (install kind {_installKind})");
        }
        catch (Exception ex)
        {
            _log?.Write($"update notice failed {ex.GetType().Name}: {ex.Message}");
        }
    }

    public static string NoticeTitle(AvailableUpdate update) => $"O-view {update.Version} is available";

    /// <summary>
    /// What the user should actually do — which differs by how they installed, and is the
    /// whole reason this says more than "an update is available".
    ///
    /// <para>It deliberately does not say "run apt upgrade". There is no O-view apt
    /// repository, so that command would report nothing to do and the user would reasonably
    /// conclude the notification was wrong. Naming the real step is the honest version
    /// (CLAUDE.md rule 6).</para>
    /// </summary>
    public static string NoticeBody(InstallKind kind, AvailableUpdate update) => kind switch
    {
        InstallKind.LinuxPackage =>
            $"Download the new .deb from {ReleaseFeed.ReleasePageUrl(update)} and install it "
            + "with sudo apt install. O-view will not update itself: these files belong to "
            + "your package manager.",

        _ =>
            $"Download the new tarball from {ReleaseFeed.ReleasePageUrl(update)} and extract "
            + "it over your existing copy. O-view will not update itself.",
    };
}
