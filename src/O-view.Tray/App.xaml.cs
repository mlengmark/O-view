using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using OView.App;
using OView.App.Diagnostics;
using OView.App.Platform;
using OView.App.Pricing;
using OView.App.Rendering;
using OView.App.Updates;
using OView.Core.Models;
using OView.Core.Providers.CachedUsage;
using OView.Core.Providers.Jsonl;
using OView.Core.Providers.PlanHistory;
using OView.Presentation.Text;
using OView.Tray.Diagnostics;
using OView.Tray.Popup;
using OView.Tray.Tray;
using OView.Tray.Updates;

namespace OView.Tray;

/// <summary>
/// Tray-resident WPF app: no windows, no StartupUri; the WPF dispatcher pumps the
/// messages NotifyIcon's hidden window needs. Diagnostic args (all optional):
/// --interval-ms N, --log path, --stress N (refresh N times, log GDI delta, keep
/// running), --samples dir (render icon PNGs and exit, legibility verification),
/// --popup-samples dir (render the detail panel offscreen in both themes), --menu-check
/// path (activate every tray-menu row over repeated open/close cycles and report what
/// fired, the menu's equivalent of --toggle-check).
/// </summary>
public partial class App : System.Windows.Application
{
    private ISingleInstanceGuard? _instance;
    private readonly IStartupRegistration _startup = new RegistryStartupRegistration();
    private readonly IThemeSource _theme = new RegistryThemeSource();

    /// <summary>
    /// Where the panel's dragged place is kept, and the rule that decides whether it may be
    /// used. One per app rather than one per panel, because the panel is rebuilt on demand and
    /// the file it reads is a fact about the machine either way.
    /// </summary>
    private readonly FilePanelPositionStore _panelPositions = new();

    private ShellHost? _host;

    /// <summary>The only thing this skin may ask of the shell; the host owns the rest.</summary>
    private ISkinToShell? _shell;

    private NotifyIconTrayHost? _trayHost;
    private TrayController? _controller;
    private PopupWindow? _popup;
    private MenuWindow? _menu;
    private UpdateService? _updates;
    private UpdateFlow? _updateFlow;
    private TrayMenuBuilder? _menuBuilder;
    private IAppLog? _log;

    /// <summary>
    /// Passed to the instance started by <see cref="RestartForMissingNativeLibrary"/>, so it
    /// can say why it restarted, a tray icon that blinks out and back unexplained reads as
    /// a crash.
    /// </summary>
    private const string RestartedForNativeLibraryFlag = "--restarted-native";

    /// <summary>
    /// Verification hooks that render and exit. A table rather than seven near-identical
    /// <c>if (args.TryGetValue(…)) { …; Shutdown(); return; }</c> blocks, so adding one is
    /// a row instead of another copy (GitHub issue #52). The implementations live in
    /// <see cref="SampleRenderer"/>.
    /// </summary>
    private static readonly (string Flag, Action<string?> Run)[] PreMutexHooks =
    [
        ("--samples", dir => SampleRenderer.RenderSamples(dir!)),
        ("--menu-samples", dir => SampleRenderer.RenderMenuSamples(dir!)),
        ("--tile-samples", dir => SampleRenderer.RenderTileSamples(dir!)),
        ("--dialog-samples", dir => SampleRenderer.RenderDialogSamples(dir!)),
        ("--popup-samples", dir => SampleRenderer.RenderPopupSamples(dir!)),
        ("--diagnose", DiagnosticsClipboard.WriteToFile),
    ];

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = CommandLine.Parse(e.Args);

        // What the panel's explanatory copy should tell a stuck user to do. Set before any
        // panel can be shown, and before the hooks below, which render that copy.
        DiagnosticsHint.Use("Right-click the tray icon → Copy diagnostics");

        // Verification hooks, all handled BEFORE the single-instance mutex so they can be
        // run against a machine where O-view is already running without disturbing the
        // live instance, which for --diagnose is the case that actually needs diagnosing,
        // and for the renderers means they never fight the running app for the display.
        // Each renders offscreen and exits; none needs a desktop session.
        foreach (var (flag, run) in PreMutexHooks)
        {
            if (args.TryGetValue(flag, out var value))
            {
                run(value);
                Shutdown();
                return;
            }
        }

        // Separate from the table because it does NOT return synchronously: it drives the
        // flyout on a timer over ~25 s and shuts the app down when the sequence finishes.
        if (args.TryGetValue("--menu-check", out var menuCheckReport))
        {
            SampleRenderer.RunMenuCheck(menuCheckReport ?? "menu-check.txt", Shutdown);
            return;
        }

        // The disclosure fold, sampled on the real desktop over ~2 s: a per-frame geometry
        // trace plus filmstrips grabbed off the screen. Same shape as the two above and
        // pre-mutex for the same reason, it drives a panel of its own and must not fight the
        // running instance for the tray or the display. Takes a DIRECTORY, like the sample
        // renderers, because it writes several files.
        if (args.TryGetValue("--fold-check", out var foldCheckDir))
        {
            SampleRenderer.RunFoldCheck(foldCheckDir ?? "fold-check", Shutdown);
            return;
        }

        _instance = new MutexSingleInstanceGuard();
        if (!_instance.TryAcquire())
        {
            // Exiting silently is right for a second ordinary launch, the user clicked the
            // shortcut twice and the running instance is what they wanted. It is wrong for a
            // verification hook: --popup-check needs the assembled app, so unlike the pre-mutex
            // checks it cannot run beside a live instance, and a check that produces no report
            // is indistinguishable from one that failed (issue #249).
            if (args.TryGetValue("--popup-check", out var blockedReport))
            {
                File.WriteAllText(
                    blockedReport ?? "popup-check.txt",
                    "popup check NOT RUN, another instance holds the single-instance mutex.\n\n" +
                    "This check drives the real ShowPopup(), so it needs the engine and tray host\n" +
                    "that only exist after startup, which means it cannot run pre-mutex the way\n" +
                    "--menu-check and --fold-check do. Close the running O-view and try again.\n\n" +
                    "RESULT: NOT RUN\n");
            }

            Shutdown();
            return;
        }

        // On by default; --log only redirects it. It was opt-in until a machine stalled for
        // five days and the one thing that would have named the failing call was a flag
        // nobody had passed, a log you have to enable before the fault reproduces is never
        // on when it matters. FileLog bounds itself, so always-on costs a capped 6 MB.
        var log = new FileLog(args.TryGetValue("--log", out var logPath) ? logPath : null);
        log.WriteSessionHeader(UpdateService.CurrentVersion, UpdateService.CurrentInstallKind.ToString());
        _log = log;

        // Now, while the host has just (re-)extracted them, and not at the first click, by
        // which time a temp cleaner may have deleted them (see WpfNativeLibraries).
        var unpinned = WpfNativeLibraries.Pin();
        log.Write(unpinned.Count == 0
            ? "wpf native libraries pinned"
            : $"wpf native libraries not pinned: {string.Join(", ", unpinned)}");
        var interval = args.TryGetValue("--interval-ms", out var ms) &&
                       int.TryParse(ms, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 50
            ? TimeSpan.FromMilliseconds(parsed)
            : TimeSpan.FromSeconds(60);

        // Everything about *what the numbers mean*, provider composition, the poll loop and
        // its cadence, the rollup store and weekly-reset log lifecycle, threshold and
        // off-plan decisions, settings, and the update schedule, lives on the other side of
        // the seam, which is platform-neutral and tested (ADR-0012). What stays here is the
        // Windows face, and these options are all it says about the rest.
        var shellOptions = new UsageEngineOptions
        {
            PollInterval = interval,
            Log = log,
            // Off-plan rendering can't be produced on demand from real data, so it is
            // verifiable via simulation. This whole feature exists because a UI failed
            // to communicate something expensive, the UI itself needs verifying.
            SimulateDivergence = args.TryGetValue("--simulate-divergence", out var sim)
                ? sim ?? "diverging"
                : null,

            // Lets the engine ask Claude Code to refresh its own usage cache (issue #234).
            // Without it, a machine with no Claude Desktop shows "unknown" indefinitely: Claude
            // Code refreshes that block only when /usage runs, so a 4.43-day-old one was measured
            // on a machine that had been running Claude Code all morning.
            //
            // No credential is involved, Claude Code authenticates itself and O-view reads the
            // file it already reads (ADR-0015).
            UsageCacheRefresher = new ClaudeCliRefresher(),
        };

        _trayHost = new NotifyIconTrayHost();

        // The rate-card drift check (ADR-0016). It writes a log line and nothing else, it
        // never installs a rate, and a difference is a maintainer's cue rather than a user's
        // problem, so there is no balloon and no UI. The line reaches a bug report through the
        // diagnostics bundle's log tail.
        var rates = new RateCardFeed(log);

        // The seam (docs/architecture.md): the shell builds and owns everything behind it, and
        // this file names none of it beyond the options above. The controller is the skin, the
        // shell pushes at it, always on this thread, and it asks back through the one interface
        // it is handed here.
        //
        // The three pushes the controller does not draw itself arrive as plain callbacks, so it
        // holds neither the update flow nor the menu: the quiet background update check
        // (ADR-0009, whose HTTP and UI stay on this side), the rate-card check, and the
        // weekly-reset conflict notice, which rides the snapshot because that is when the
        // conflict is discovered rather than needing a schedule of its own (issue #186).
        _host = new ShellHost(shellOptions, new WpfUiDispatcher(), shell =>
            _controller = new TrayController(
                _trayHost, shell, _theme, log,
                afterSnapshot: () => _menuBuilder?.NotifyWeeklyResetConflict(),
                updateCheckDue: () => _ = _updateFlow?.BackgroundCheckAsync(),
                rateCheckDue: () => _ = rates.CheckAsync(ModelCatalog.Bundled)));
        _shell = _host.Shell;

        _trayHost.IconClicked += (_, _) => OpenFromTray(ShowPopup);
        _trayHost.IconRightClicked += (_, _) => OpenFromTray(ShowMenu);

        // Auto-update (ADR-0009): a quiet background check surfaces a newer release as a
        // balloon; the actual download-and-install is only ever done from the menu, with
        // the user's confirmation. The shell owns the *when*; the HTTP and the UI are here.
        _updates = new UpdateService(log);
        _updateFlow = new UpdateFlow(_updates, _trayHost, _shell, Shutdown);

        // The menu's rows and their actions, and the support bundle behind one of them.
        // Built here because everything it drives, the shell, the tray host, the update
        // flow, exists by this point; what stays on this file is showing the flyout.
        _menuBuilder = new TrayMenuBuilder(
            _startup, _shell, _trayHost, _updateFlow,
            new DiagnosticsClipboard(_trayHost, _shell), ResetPanelPosition, Shutdown);

        // Ticks arrive on the UI thread; the reading happens off it and comes back through
        // the dispatcher, so a first ingest over a large transcript history cannot hold the
        // message pump (issue #125).
        _host.Start(new DispatcherTimerFactory());

        if (args.ContainsKey(RestartedForNativeLibraryFlag))
        {
            _trayHost.ShowNotification(
                NoticeText.RestartedTitle, NoticeText.RestartedBody, NotificationKind.Warning);
        }

        if (args.ContainsKey("--test-notify"))
        {
            _trayHost.ShowNotification(NoticeText.ThresholdTitle, "Test notification (--test-notify).",
                NotificationKind.Information);
        }

        // Verification hook: force an interactive update check (as if from the menu).
        if (args.ContainsKey("--check-updates"))
        {
            _ = _updateFlow.CheckForUpdatesInteractiveAsync();
        }

        // Verification hooks for the startup-registration round trip.
        if (args.ContainsKey("--startup-on"))
        {
            log?.Write($"startup-on ok={_startup.Enable()} enabled={_startup.IsEnabled()}");
        }
        if (args.ContainsKey("--startup-off"))
        {
            log?.Write($"startup-off ok={_startup.Disable()} enabled={_startup.IsEnabled()}");
        }

        if (args.TryGetValue("--stress", out var stress) &&
            int.TryParse(stress, NumberStyles.Integer, CultureInfo.InvariantCulture, out var iterations))
        {
            _controller!.StressTest(iterations);
        }

        // The tray click, driven on the real desktop over ~5 s. Not beside --menu-check and
        // --fold-check in the pre-mutex table: this one needs the assembled app, because
        // driving the real ShowPopup is the point (issue #249).
        if (args.TryGetValue("--popup-check", out var popupCheckReport))
        {
            TrayClickChecks.RunPopupCheck(
                popupCheckReport ?? "popup-check.txt", ShowPopup, () => _popup, _shell!, Shutdown);
            return;
        }

        // Verification hooks: open the popup immediately, optionally pinned (auto-hide
        // off so screenshots can be taken) and with a forced theme.
        if (args.ContainsKey("--show-popup"))
        {
            EnsurePopup().PinForVerification = args.ContainsKey("--popup-pin");
            _popup!.ThemeOverride = args.TryGetValue("--popup-theme", out var theme)
                ? theme == "light"
                : null;
            // --simulate-divergence is read where the engine is constructed, because the
            // simulation feeds the real detector rather than the panel.
            ShowPopup();
        }

        // Drives the tray icon's own open → close → open sequence and reports the panel's
        // state after each step. The toggle regressed in a way that only shows on the
        // SECOND open, which a single --show-popup run can never catch.
        if (args.TryGetValue("--toggle-check", out var toggleReport))
        {
            TrayClickChecks.RunToggleCheck(toggleReport ?? "toggle-check.txt", ShowPopup, () => _popup, Shutdown);
        }

        // The same for the menu flyout (issue #33). --menu-samples renders it in
        // isolation; this opens the real thing on the real desktop, which is the only
        // way to verify the docked placement against a live taskbar and work area.
        if (args.ContainsKey("--show-menu"))
        {
            _menu = _menuBuilder.CreateMenu();
            _menu.PinForVerification = args.ContainsKey("--menu-pin");
            _menu.ThemeOverride = args.TryGetValue("--menu-theme", out var menuTheme)
                ? menuTheme == "light"
                : null;
            ShowMenu();
        }
    }

    private void ShowPopup()
    {
        if (_shell is null)
        {
            return;
        }

        var popup = EnsurePopup();
        if (CompletedToggle(popup))
        {
            return;
        }

        _shell.Refresh();  // fresh data on open; local reads are cheap

        // Opening the panel is the moment freshness is observable, so it is what drives the
        // usage-cache refresh rather than a timer (issue #234). Forced, because a person
        // looking is not a poll and must not be held to the background floor.
        //
        // Unlike the Refresh() above this does NOT block the click: it spawns Claude Code off
        // the UI thread and publishes back when it lands, so the panel opens immediately on the
        // current snapshot and updates underneath if the figures moved. Doing it synchronously
        // here would put a process spawn in front of the user's click, issue #125's lesson.
        _shell.RefreshUsageCache(force: true);
        // Both inspected on open (not cached) so the banners reflect the machine as it is
        // now. The scope report is what the token-scope note is built from, resolved
        // roots and real file counts, never a hard-coded path (issue #58).
        popup.DataReport = PlanHistoryDiagnostics.Inspect();
        popup.ScopeReport = TranscriptScopeReport.Inspect();
        popup.SettingsForDisplay = _shell.Settings;
        // Read on open like the two reports above. The flag cache refreshes when Claude Code
        // starts, not when /usage runs, so it is often fresher than the percentages and must
        // be read on its own rather than folded into the poll (issue #254).
        popup.BoostNotices = BoostNotices.TryRead();
        // Whether extra usage is on for this account, for the off-plan banner (issue #259).
        // Taken from the engine rather than read again here: it is the same block the poll
        // above just refreshed, and the engine's reader is the one that honours the
        // injected-provider guard.
        popup.CachedUsage = _shell.CachedUsage;
        popup.ShowNearTrayIcon(
            _shell.Latest,
            _shell.BuildStatistics(),
            ClaudeAccount.TryRead());
    }

    /// <summary>
    /// Treats a tray click as the SECOND half of a toggle where that is what it is, and
    /// returns whether it was handled.
    ///
    /// <para>Clicking the tray icon is a toggle, as every taskbar flyout is. The click
    /// itself dismisses an open surface by taking focus away from it, so by the time this
    /// runs the surface is already closing, reopening it here made the icon a one-way
    /// switch that could only ever open. A click landing right after a dismissal is the
    /// close half, and leaves it closed.</para>
    ///
    /// <para>One guard for both surfaces (issue #54): left-click and right-click behaved
    /// identically here and were written twice.</para>
    /// </summary>
    private static bool CompletedToggle(IFlyout flyout)
    {
        if (!flyout.IsVisible && !flyout.ClosedByClickAway)
        {
            return false;
        }

        flyout.DismissNow();
        return true;
    }

    private PopupWindow EnsurePopup() => _popup ??= new PopupWindow
    {
        // Set here and nowhere else: the offscreen sample renderers build their own panels
        // without one, which is correct, they place no window, so a remembered place would be
        // read for a position nothing ever applies.
        PlacementMemory = new PanelPlacementMemory(_panelPositions),
    };

    /// <summary>
    /// The menu's "Reset panel position" row: forget this display's saved place, then put an
    /// open panel back at the tray corner.
    ///
    /// <para>Forgetting first, so the re-placement below reads a store with nothing in it and
    /// reaches the corner rule by the ordinary route rather than being told where to go.</para>
    /// </summary>
    private void ResetPanelPosition()
    {
        if (PopupPositioner.CurrentDisplayId() is { } displayId)
        {
            _panelPositions.Forget(displayId);
        }

        _popup?.ReturnToCorner();
    }

    /// <summary>
    /// Runs a tray-click action. Anything it throws arrives inside NotifyIcon's window
    /// procedure, where WinForms shows its raw "Unhandled exception" box, which is how a
    /// missing native library reached users. That one case is recoverable, because a fresh
    /// launch re-extracts the files, so it restarts; anything else is logged and rethrown
    /// unchanged rather than hidden.
    /// </summary>
    private void OpenFromTray(Action open)
    {
        try
        {
            open();
        }
        catch (Exception ex) when (WpfNativeLibraries.IsMissingNativeLibrary(ex))
        {
            _log?.Write($"tray open failed, native library missing; restarting: {ex}");
            RestartForMissingNativeLibrary();
        }
        catch (Exception ex)
        {
            _log?.Write($"tray open failed: {ex}");
            throw;
        }
    }

    /// <summary>
    /// Starts a new instance and exits this one. The single-instance mutex is released
    /// first or the new process would see it held and exit silently. Not a loop: the new
    /// instance only restarts again if a later click fails the same way.
    /// </summary>
    private void RestartForMissingNativeLibrary()
    {
        var exe = Environment.ProcessPath;
        if (exe is null)
        {
            return;
        }

        _instance?.Dispose();
        _instance = null;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = exe,
            Arguments = RestartedForNativeLibraryFlag,
            UseShellExecute = false,
        });
        Shutdown();
    }

    /// <summary>
    /// Right-click menu, a docked flyout window, not a ContextMenu (issue #33; see
    /// <see cref="MenuWindow"/> for why the cursor-placed menu had to go). Still no
    /// WinForms: that stays confined to NotifyIcon itself (CLAUDE.md rule 5).
    /// </summary>
    private void ShowMenu()
    {
        _menu ??= _menuBuilder!.CreateMenu();
        if (CompletedToggle(_menu))
        {
            return;
        }

        // State is passed on every open, never cached: both settings can change
        // externally (another instance, a manual registry edit, Task Manager's
        // startup page).
        _menu.ShowDocked(new MenuWindow.MenuState(
            RunAtStartup: _startup.IsEnabled(),
            NotifyOnThreshold: _shell!.Settings.NotifyOnThreshold,
            ThresholdPercent: _shell.Settings.ThresholdPercent,
            UpdateAutomatically: _shell.Settings.UpdateAutomatically,
            // Asked on every open rather than cached at startup, for the same reason the
            // rest of this is: it is a fact about how this build was installed, and the
            // policy is the single place that decides it (ADR-0009).
            CanUpdateAutomatically: UpdatePolicy.MayDownloadAndRun(UpdateService.CurrentInstallKind),
            Version: UpdateService.CurrentVersion,
            WeeklyReset: _menuBuilder!.WeeklyResetRowLabel(),
            // Empty on every ordinary open; the row only appears once the refresh has stopped
            // itself, which is the only state there is anything to undo (issue #234).
            UsageRefreshBlocked: _shell.UsageRefreshBlocked ?? ""));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // The shell first: it stops every timer and closes the rollup store, so nothing can
        // still be pushed at the controller once the tray host below is gone.
        _host?.Dispose();
        _trayHost?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }

}
