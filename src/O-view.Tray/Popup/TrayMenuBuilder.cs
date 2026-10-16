using OView.App;
using OView.App.Platform;
using OView.Presentation.Text;
using OView.Tray.Diagnostics;
using OView.Tray.Tray;

namespace OView.Tray.Popup;

/// <summary>
/// Assembles the right-click menu and owns what its rows do: the panel-position reset, the two
/// settings toggles and the threshold, the weekly-reset entry, the refresh-resume undo, the
/// support bundle, the interactive update check and Exit. <see cref="MenuWindow"/> draws the rows and
/// <see cref="MenuText"/> owns every word; this class is the wiring between them and the
/// shell, the startup registration and the update flow.
///
/// <para>Showing the menu stays with <c>App</c>, because placing a docked flyout is the
/// skin's own job and the app's lifetime is its own — Exit arrives here as a callback for the
/// same reason it does in <see cref="UpdateFlow"/>.</para>
/// </summary>
public sealed class TrayMenuBuilder
{
    private readonly IStartupRegistration _startup;
    private readonly ISkinToShell _shell;
    private readonly ITrayHost _trayHost;
    private readonly UpdateFlow _updateFlow;
    private readonly DiagnosticsClipboard _diagnostics;
    private readonly Action _resetPanelPosition;
    private readonly Action _shutdown;

    /// <param name="resetPanelPosition">
    /// Forgets wherever the panel was dragged to and puts an open one back at the tray corner.
    /// A callback for the same reason <paramref name="shutdown"/> is one: the panel's own
    /// lifetime belongs to the app, and this class is the menu's wiring rather than its owner.
    /// </param>
    public TrayMenuBuilder(
        IStartupRegistration startup,
        ISkinToShell shell,
        ITrayHost trayHost,
        UpdateFlow updateFlow,
        DiagnosticsClipboard diagnostics,
        Action resetPanelPosition,
        Action shutdown)
    {
        _startup = startup;
        _shell = shell;
        _trayHost = trayHost;
        _updateFlow = updateFlow;
        _diagnostics = diagnostics;
        _resetPanelPosition = resetPanelPosition;
        _shutdown = shutdown;
    }

    public MenuWindow CreateMenu()
    {
        var menu = new MenuWindow
        {
            // Both setters return the state as it actually stands afterwards, not the
            // state that was requested — a registry write can fail, and a tick that
            // claims otherwise would be a fabricated fact about the machine. That rule
            // now lives once, on IStartupRegistration.Apply, so both heads share it.
            SetRunAtStartup = _startup.Apply,
            SetNotifyOnThreshold = enable => _shell.SetNotifyOnThreshold(enable),
            // Same contract as the two above: the row renders what the shell returns, so a
            // value that could not be applied is not drawn as though it had been.
            SetThresholdPercent = percent => _shell.SetThresholdPercent(percent),
            SetUpdateAutomatically = enable => _shell.SetUpdateAutomatically(enable),
        };

        menu.WeeklyResetRequested += (_, _) => EditWeeklyReset();
        // Undoes a self-imposed refresh block, then tries once immediately: someone who just
        // re-enabled this is asking for it now, not at the next background beat (issue #234).
        menu.ResumeUsageRefreshRequested += (_, _) =>
        {
            if (_shell.ResumeUsageRefresh())
            {
                _shell.RefreshUsageCache(force: true);
            }
        };

        menu.ResetPanelPositionRequested += (_, _) => _resetPanelPosition();

        // One-click support bundle: a blank panel is otherwise indistinguishable from
        // "Desktop missing", "unexpected file format", or "file unreadable", and asking
        // users to run PowerShell by hand is not a diagnosis path.
        menu.CopyDiagnosticsRequested += (_, _) => _diagnostics.Copy();
        // "Check for updates" sits directly above Exit, as requested in issue #18.
        menu.CheckForUpdatesRequested += async (_, _) =>
            await _updateFlow.CheckForUpdatesInteractiveAsync();
        menu.ExitRequested += (_, _) => _shutdown();

        return menu;
    }

    /// <summary>
    /// What the menu row shows on its right: the entered reset, or empty when O-view is
    /// deriving one. Reads the shell on every open rather than caching — the same rule the
    /// rest of the menu state follows.
    /// </summary>
    public string WeeklyResetRowLabel() =>
        _shell.Settings.WeeklyReset is { } reset
            ? MenuText.WeeklyResetValue(reset.DayText, reset.TimeText)
            : "";

    /// <summary>
    /// Tells the user once when an observed reset disproves what they entered (issue #186).
    ///
    /// <para>The provider has already set the entry aside in favour of the observation — a
    /// number O-view has evidence against must not stay on screen — so this explains a change
    /// that has already happened. Silence would leave them believing the time they typed.</para>
    ///
    /// <para>Once per conflicting observation, not once per poll: the check runs every
    /// refresh, and a warning that reappears every 20 seconds is one people learn to
    /// dismiss.</para>
    /// </summary>
    public void NotifyWeeklyResetConflict()
    {
        if (_shell.WeeklyResetConflict is not { } conflict
            || !_shell.IsWeeklyResetConflictUnseen(conflict))
        {
            return;
        }

        _shell.MarkWeeklyResetConflictNoticed(conflict);
        _trayHost.ShowNotification(
            NoticeText.WeeklyResetConflictTitle,
            PanelText.WeeklyResetConflict(conflict, TimeZoneInfo.Local),
            NotificationKind.Warning);
    }

    /// <summary>
    /// Opens the weekly-reset entry (issue #186), applies whatever comes back, and refreshes
    /// so the panel and the graph's week gridlines move with it — the gridlines read the
    /// snapshot's weekly reset, so they follow without separate wiring.
    /// </summary>
    private void EditWeeklyReset()
    {
        var (entry, changed) = WeeklyResetDialog.Show(_shell.Settings.WeeklyReset);
        if (!changed)
        {
            return;
        }

        _shell.SetWeeklyReset(entry);
        _shell.Refresh();
    }
}
