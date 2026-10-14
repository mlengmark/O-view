using OView.App;
using OView.App.Updates;
using OView.Core.Updates;
using OView.Presentation.Text;
using OView.Tray.Popup;
using OView.Tray.Tray;
using OView.Tray.Updates;

namespace OView.Tray;

/// <summary>
/// The Windows update flow: the quiet background check, the automatic install, the menu's
/// interactive check and the offer that follows it. <see cref="UpdateService"/> does the IO
/// and <see cref="UpdateText"/> owns every word; this class is the sequencing and the
/// guards that keep two checks or two installs from overlapping.
///
/// <para>Shutting the app down after handing off to the installer arrives as a callback, so
/// the flow drives the app's lifetime without owning it — the WPF <c>Application</c> stays
/// the skin's.</para>
/// </summary>
public sealed class UpdateFlow
{
    private readonly UpdateService _updates;
    private readonly ITrayHost _trayHost;
    private readonly ISkinToShell _shell;
    private readonly Action _shutdown;

    private bool _flowActive;

    /// <summary>
    /// An interactive update check is running, including its modal confirmation. Distinct
    /// from <see cref="_flowActive"/>, which only spans the download that may follow.
    /// </summary>
    private bool _checkActive;
    private string? _notifiedUpdateTag;

    public UpdateFlow(UpdateService updates, ITrayHost trayHost, ISkinToShell shell, Action shutdown)
    {
        _updates = updates;
        _trayHost = trayHost;
        _shell = shell;
        _shutdown = shutdown;
    }

    /// <summary>
    /// Quiet check: only *notifies* when a newer release exists, and only once per version
    /// (re-notifying every day would be nagging). It never downloads or installs anything —
    /// that stays behind the explicit menu action and its confirmation.
    /// </summary>
    public async Task BackgroundCheckAsync()
    {
        if (_flowActive)
        {
            return;
        }

        var result = await _updates.CheckAsync();
        if (result is not { Outcome: UpdateOutcome.UpdateAvailable, Available: { } update }
            || _notifiedUpdateTag == update.Tag)
        {
            return;
        }

        _notifiedUpdateTag = update.Tag;

        // Both conditions, every time. The setting alone is a preference; MayDownloadAndRun is
        // the permission, and it stays the only thing deciding whether anything is fetched or
        // executed (ADR-0009's amendment). A settings.json copied from a Windows machine onto a
        // tarball install holds the preference true and must still install nothing.
        if (_shell.Settings.UpdateAutomatically
            && UpdatePolicy.MayDownloadAndRun(UpdateService.CurrentInstallKind))
        {
            await InstallAutomaticallyAsync(update);
            return;
        }

        _trayHost.ShowNotification(
            UpdateText.AvailableTitle,
            UpdateText.AvailableBody(update.Version, UpdateService.CurrentVersion),
            NotificationKind.Information);
    }

    /// <summary>
    /// The automatic path: install without the per-release confirmation, because the user
    /// already gave it once by turning the setting on (ADR-0009 as amended, issue #140).
    ///
    /// <para><b>Automatic is not silent.</b> The balloon goes out <i>before</i> anything is
    /// downloaded, naming the version and warning that O-view will close and reopen.
    /// Invisibility was the worst property of the alternative ADR-0009 rejected, and it stays
    /// rejected — what this removes is the per-release click, not the user's knowledge.</para>
    ///
    /// <para>Every failure path is the confirmed flow's, unchanged — including the deliberate
    /// refusal to open the releases page after a checksum mismatch, which would hand the user
    /// exactly the file the check just rejected.</para>
    /// </summary>
    public async Task InstallAutomaticallyAsync(AvailableUpdate update)
    {
        if (_flowActive)
        {
            return;
        }

        _trayHost.ShowNotification(
            UpdateText.InstallingTitle(update.Version),
            UpdateText.InstallingBody(UpdateService.CurrentVersion),
            NotificationKind.Information);

        _flowActive = true;
        try
        {
            var installer = await _updates.DownloadInstallerAsync(update);
            _updates.LaunchInstaller(installer);
            _shutdown();  // release the exe lock so the installer can replace it and relaunch
        }
        catch (UpdateVerificationException)
        {
            _flowActive = false;
            // The one case that earns Error. A checksum mismatch means the bytes that arrived
            // were not the bytes the release published, and O-view refused to run them.
            _trayHost.ShowNotification(
                UpdateText.ChecksumMismatchTitle, UpdateText.ChecksumMismatchBody,
                NotificationKind.Error);
        }
        catch (Exception)
        {
            // No releases page here. The confirmed flow opens it because a human is already
            // in the loop and can decide; nobody is watching this one, so a browser window
            // appearing unbidden is not a useful answer. It retries on the next daily check.
            _flowActive = false;
            _trayHost.ShowNotification(
                UpdateText.AutomaticFailedTitle, UpdateText.AutomaticFailedBody(update.Version),
                NotificationKind.Warning);
        }
    }

    /// <summary>
    /// The menu action: always reports an outcome (up to date, available, or unreachable),
    /// and offers to install when a newer release exists.
    /// </summary>
    public async Task CheckForUpdatesInteractiveAsync()
    {
        if (_flowActive || _checkActive)
        {
            // _checkActive covers the whole interactive flow, including the modal
            // confirmation; _flowActive only covers the download that follows it. A
            // second click while the first check is still awaiting the network — or sitting
            // on the confirm dialog — would otherwise start an independent flow and stack a
            // second modal behind the first.
            return;
        }

        _checkActive = true;
        try
        {
            await RunUpdateCheckAsync();
        }
        finally
        {
            _checkActive = false;
        }
    }

    private async Task RunUpdateCheckAsync()
    {
        var result = await _updates.CheckAsync();
        switch (result.Outcome)
        {
            case UpdateOutcome.UpToDate:
                _trayHost.ShowNotification(
                    UpdateText.UpToDateTitle,
                    UpdateText.UpToDateBody(UpdateService.CurrentVersion),
                    NotificationKind.Information);
                break;

            case UpdateOutcome.UpdateAvailable when result.Available is { } update:
                await OfferUpdateAsync(update);
                break;

            case UpdateOutcome.RateLimited:
                // Named separately because the remedy is different and the cause is usually
                // not this machine: GitHub's limit is 60/hour per IP address, so a shared
                // network can exhaust it without this user making a request. Telling them
                // their connection failed sent them to debug the wrong thing (issue #176).
                _trayHost.ShowNotification(
                    UpdateText.RateLimitedTitle,
                    PanelText.RateLimitedNotice(result.RetryAfterUtc, TimeZoneInfo.Local),
                    NotificationKind.Warning);
                break;

            default:
                // Warning, not Error: GitHub being briefly unreachable is a retryable
                // condition on the user's own network, and nothing on the machine is wrong.
                _trayHost.ShowNotification(
                    UpdateText.CheckFailedTitle, UpdateText.CheckFailedBody,
                    NotificationKind.Warning);
                break;
        }
    }

    /// <summary>
    /// Confirms with the user, then acts according to how this build was installed
    /// (<see cref="UpdatePolicy"/>): an installer build downloads and hands off, exiting so
    /// the installer can replace the exe and relaunch it; anything the user or a package
    /// manager owns is sent to the release page instead.
    /// </summary>
    public async Task OfferUpdateAsync(AvailableUpdate update)
    {
        var action = UpdatePolicy.ActionFor(UpdateService.CurrentInstallKind);

        if (action is not UpdateAction.InstallInPlace)
        {
            // A running single-file exe cannot overwrite itself, and the installer would
            // create a parallel install rather than update the loose one. On Linux this
            // branch also covers an apt build, which must never overwrite dpkg's files —
            // but that head has its own copy and never reaches this method.
            if (ConfirmUpdate(update, UpdateText.OpenPageQuestion, UpdateText.OpenPageConfirm))
            {
                _updates.OpenInBrowser(UpdateService.ReleasePageUrl(update));
            }
            return;
        }

        if (!ConfirmUpdate(update, UpdateText.InstallQuestion, UpdateText.InstallConfirm,
            UpdateText.InstallDetail))
        {
            return;
        }

        _flowActive = true;
        try
        {
            var installer = await _updates.DownloadInstallerAsync(update);
            _updates.LaunchInstaller(installer);
            _shutdown();  // release the exe lock so the installer can replace it and relaunch
        }
        catch (UpdateVerificationException)
        {
            // Deliberately does NOT open the releases page. Every other failure here means
            // "try again by hand", but this one means the file that arrived was not the file
            // the release published — sending the user to download it manually would hand
            // them the thing the check just rejected. Say what was observed and stop
            // (CLAUDE.md rule 6); the installer has already been deleted, and nothing on the
            // machine has changed.
            _flowActive = false;
            // The one case that earns Error. A checksum mismatch means the bytes that arrived
            // were not the bytes the release published, and O-view refused to run them.
            _trayHost.ShowNotification(
                UpdateText.ChecksumMismatchTitle, UpdateText.ChecksumMismatchBody,
                NotificationKind.Error);
        }
        catch (Exception)
        {
            _flowActive = false;
            // Warning rather than Error: the download failed, but the user is being handed a
            // working way to finish the job on the next line.
            _trayHost.ShowNotification(
                UpdateText.FailedTitle, UpdateText.FailedBody, NotificationKind.Warning);
            _updates.OpenInBrowser(UpdateService.ReleasePageUrl(update));
        }
    }

    /// <summary>
    /// The update prompt, on the app's own dialog rather than a system MessageBox — that
    /// box was raw Win32 chrome with a stock glyph and nothing identifying which app was
    /// asking. The primary button names the action ("Update now") instead of answering
    /// "Yes" to a question the user has to re-read to be sure of.
    /// </summary>
    private static bool ConfirmUpdate(
        AvailableUpdate update, string question, string confirmLabel, string detail = "") =>
        DialogWindow.Confirm(
            title: UpdateText.DialogTitle,
            message: UpdateText.DialogMessage(update.Version, UpdateService.CurrentVersion, question),
            confirmLabel: confirmLabel,
            cancelLabel: UpdateText.DialogCancel,
            detail: detail);
}
