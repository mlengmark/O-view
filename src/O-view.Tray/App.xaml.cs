using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using OView.Core.Models;
using OView.Core.Providers;
using OView.Core.Providers.Jsonl;
using OView.Core.Providers.PlanHistory;
using OView.Core.Storage;
using OView.Core.Updates;
using OView.Tray.Diagnostics;
using OView.Tray.Popup;
using OView.Tray.Tray;
using OView.Tray.Updates;
using Border = System.Windows.Controls.Border;
using Size = System.Windows.Size;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;

namespace OView.Tray;

/// <summary>
/// Tray-resident WPF app: no windows, no StartupUri; the WPF dispatcher pumps the
/// messages NotifyIcon's hidden window needs. Diagnostic args (all optional):
/// --interval-ms N, --log path, --stress N (refresh N times, log GDI delta, keep
/// running), --samples dir (render icon PNGs and exit — legibility verification),
/// --menu-check path (activate every tray-menu row over repeated open/close cycles and
/// report what fired — the menu's equivalent of --toggle-check).
/// </summary>
public partial class App : System.Windows.Application
{
    private Mutex? _instanceMutex;
    private RollupStore? _store;
    private NotifyIconTrayHost? _trayHost;
    private TrayController? _controller;
    private PopupWindow? _popup;
    private TraySettings _settings = new();
    private ThresholdWatcher? _watcher;
    private MenuWindow? _menu;
    private PlanHistoryProvider? _planHistory;
    private bool _offPlanNotified;
    private string? _simulateDivergence;
    private UpdateService? _updates;
    private DispatcherTimer? _updateTimer;
    private bool _updateFlowActive;

    /// <summary>
    /// An interactive update check is running, including its modal confirmation. Distinct
    /// from <see cref="_updateFlowActive"/>, which only spans the download that may follow.
    /// </summary>
    private bool _updateCheckActive;
    private string? _notifiedUpdateTag;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = ParseArgs(e.Args);

        if (args.TryGetValue("--samples", out var samplesDir))
        {
            RenderSamples(samplesDir!);
            Shutdown();
            return;
        }

        // The menu's equivalent of --samples, and handled before the mutex for the same
        // reason --diagnose is: the flyout can then be reviewed on a machine where O-view
        // is already running, without disturbing the live instance.
        if (args.TryGetValue("--menu-samples", out var menuSamplesDir))
        {
            RenderMenuSamples(menuSamplesDir!);
            Shutdown();
            return;
        }

        // Tile breakdowns across the model counts that change the chart's shape
        // (issue #37). Also before the mutex — the interesting cases are ones this
        // machine's real data may never produce, so they are built, not waited for.
        if (args.TryGetValue("--tile-samples", out var tileSamplesDir))
        {
            RenderTileSamples(tileSamplesDir!);
            Shutdown();
            return;
        }

        // The update dialog, in both themes. It is modal, so it cannot be screenshotted
        // by the same run that would take the screenshot — it has to be rendered.
        if (args.TryGetValue("--dialog-samples", out var dialogSamplesDir))
        {
            RenderDialogSamples(dialogSamplesDir!);
            Shutdown();
            return;
        }

        // Drives every menu row through repeated open → activate → reopen cycles and reports
        // what actually fired (issue #47). Before the mutex, like --menu-samples: it builds
        // its own flyout with recording handlers, so it needs no tray icon and cannot
        // disturb a running instance.
        if (args.TryGetValue("--menu-check", out var menuCheckReport))
        {
            RunMenuCheck(menuCheckReport ?? "menu-check.txt");
            return;     // the run shuts itself down when the sequence finishes
        }

        // Writes the same report as the Copy diagnostics menu item and exits. Handled
        // BEFORE the single-instance mutex so it can be run against a machine where
        // O-view is already running, without disturbing the live instance — the case
        // where a "no usage data" report actually needs diagnosing.
        if (args.TryGetValue("--diagnose", out var diagnoseTo))
        {
            WriteDiagnostics(diagnoseTo);
            Shutdown();
            return;
        }

        // Two instances would mean two icons and double polling (ADR-0003 item 7).
        _instanceMutex = new Mutex(initiallyOwned: true, "OView.Tray.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            Shutdown();
            return;
        }

        var log = args.TryGetValue("--log", out var logPath) ? new FileLog(logPath!) : null;
        var interval = args.TryGetValue("--interval-ms", out var ms) &&
                       int.TryParse(ms, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 50
            ? TimeSpan.FromMilliseconds(parsed)
            : TimeSpan.FromSeconds(60);

        _store = new RollupStore();

        // Observed weekly resets accrue in their own durable file, not in the rollup store
        // (ADR-0011): the store is a rebuildable cache that wipes itself on corruption,
        // whereas a missed reset costs a week. Older builds kept them in the store, so any
        // rows it still holds are carried across on the way past — idempotent, so this is
        // safe to run on every launch.
        var account = ClaudeAccount.TryRead();
        var weeklyResets = new WeeklyResetLog();
        try
        {
            weeklyResets.ImportLegacy(_store.GetLegacyWeeklyResets(), account?.OrganizationUuid ?? "");
        }
        catch (Exception ex)
        {
            log?.Write($"weekly-reset legacy import skipped: {ex.GetType().Name}: {ex.Message}");
        }

        _planHistory = new PlanHistoryProvider(
            orgUuid: account?.OrganizationUuid,
            weeklyResetLog: weeklyResets);
        var provider = new CompositeUsageProvider(
            _planHistory,
            new JsonlUsageProvider(_store));

        _trayHost = new NotifyIconTrayHost();
        _controller = new TrayController(_trayHost, provider, interval, log);

        _trayHost.IconClicked += (_, _) => ShowPopup();
        _trayHost.IconRightClicked += (_, _) => ShowMenu();

        _settings = TraySettings.Load();
        _watcher = new ThresholdWatcher(_settings.ThresholdPercent);
        _controller.SnapshotUpdated += snapshot =>
        {
            if (_settings.NotifyOnThreshold && _watcher.ShouldNotify(snapshot.SessionPercent))
            {
                _trayHost.ShowNotification("Claude usage",
                    $"Session usage is at {snapshot.SessionPercent}% of the 5-hour limit.");
            }

            CheckOffPlan(log);
        };

        log?.Write($"startup interval={interval.TotalSeconds}s");
        _controller.Start();

        // Auto-update (ADR-0009): a quiet background check surfaces a newer release as a
        // balloon; the actual download-and-install is only ever done from the menu, with
        // the user's confirmation.
        _updates = new UpdateService(log);
        StartUpdateChecks();

        if (args.ContainsKey("--test-notify"))
        {
            _trayHost.ShowNotification("Claude usage", "Test notification (--test-notify).");
        }

        // Verification hook: force an interactive update check (as if from the menu).
        if (args.ContainsKey("--check-updates"))
        {
            _ = CheckForUpdatesInteractiveAsync();
        }

        // Verification hooks for the startup-registration round trip.
        if (args.ContainsKey("--startup-on"))
        {
            log?.Write($"startup-on ok={StartupRegistration.Enable()} enabled={StartupRegistration.IsEnabled()}");
        }
        if (args.ContainsKey("--startup-off"))
        {
            log?.Write($"startup-off ok={StartupRegistration.Disable()} enabled={StartupRegistration.IsEnabled()}");
        }

        if (args.TryGetValue("--stress", out var stress) &&
            int.TryParse(stress, NumberStyles.Integer, CultureInfo.InvariantCulture, out var iterations))
        {
            _controller.StressTest(iterations);
        }

        // Verification hooks: open the popup immediately, optionally pinned (auto-hide
        // off so screenshots can be taken) and with a forced theme.
        if (args.ContainsKey("--show-popup"))
        {
            EnsurePopup().PinForVerification = args.ContainsKey("--popup-pin");
            _popup!.ThemeOverride = args.TryGetValue("--popup-theme", out var theme)
                ? theme == "light"
                : null;
            // Off-plan rendering can't be produced on demand from real data, so it is
            // verifiable via simulation. This whole feature exists because a UI failed
            // to communicate something expensive — the UI itself needs verifying.
            _simulateDivergence = args.TryGetValue("--simulate-divergence", out var sim) ? sim ?? "diverging" : null;
            ShowPopup();
        }

        // Drives the tray icon's own open → close → open sequence and reports the panel's
        // state after each step. The toggle regressed in a way that only shows on the
        // SECOND open, which a single --show-popup run can never catch.
        if (args.TryGetValue("--toggle-check", out var toggleReport))
        {
            RunToggleCheck(toggleReport ?? "toggle-check.txt");
        }

        // The same for the menu flyout (issue #33). --menu-samples renders it in
        // isolation; this opens the real thing on the real desktop, which is the only
        // way to verify the docked placement against a live taskbar and work area.
        if (args.ContainsKey("--show-menu"))
        {
            _menu = CreateMenu();
            _menu.PinForVerification = args.ContainsKey("--menu-pin");
            _menu.ThemeOverride = args.TryGetValue("--menu-theme", out var menuTheme)
                ? menuTheme == "light"
                : null;
            ShowMenu();
        }
    }

    /// <summary>
    /// Exercises every menu row the way a user does, repeatedly: open the flyout, activate a
    /// row, let it close, open it again. A single activation of a single row can never catch
    /// this class of fault — the flyout carries state across opens (`_closing`, `_closedAt`)
    /// and it is the SECOND and third cycles that expose it, exactly as `--toggle-check`
    /// exists because the panel's toggle only broke on the second open.
    ///
    /// Handlers here only record. The point is the plumbing from a row to its event — running
    /// the real actions would exit the app halfway through and download an installer.
    /// </summary>
    private void RunMenuCheck(string path)
    {
        var report = new System.Text.StringBuilder();
        var fired = new Dictionary<string, int>
        {
            ["diagnostics"] = 0, ["updates"] = 0, ["exit"] = 0,
        };

        var menu = new MenuWindow
        {
            SetRunAtStartup = enable => enable,          // no registry writes in a check
            SetNotifyOnThreshold = enable => enable,
        };
        menu.CopyDiagnosticsRequested += (_, _) => fired["diagnostics"]++;
        menu.CheckForUpdatesRequested += (_, _) => fired["updates"]++;
        menu.ExitRequested += (_, _) => fired["exit"]++;

        void Open() => menu.ShowDocked(true, true, 70, UpdateService.CurrentVersion);
        void Record(string label) =>
            report.AppendLine($"{label,-42} visible={menu.IsVisible,-5} closing={menu.IsClosing,-5} " +
                              $"fired=[diag {fired["diagnostics"]}, upd {fired["updates"]}, exit {fired["exit"]}]");

        // Each action row, three times over, with a full open/close cycle between — plus the
        // toggle rows, which must NOT close the flyout, and a click-away dismissal.
        var steps = new List<(string Label, Action Do)>();
        for (var cycle = 1; cycle <= 3; cycle++)
        {
            var n = cycle;
            foreach (var row in new[] { MenuWindow.MenuRow.Updates, MenuWindow.MenuRow.Diagnostics })
            {
                steps.Add(($"cycle {n}: open before {row}", Open));
                steps.Add(($"cycle {n}: activate {row}", () => menu.InvokeRow(row)));
                steps.Add(($"cycle {n}: settled after {row}", () => { }));
            }

            steps.Add(($"cycle {n}: open for toggles", Open));
            steps.Add(($"cycle {n}: toggle Run at startup", () => menu.InvokeRow(MenuWindow.MenuRow.Startup)));
            steps.Add(($"cycle {n}: toggle Notify", () => menu.InvokeRow(MenuWindow.MenuRow.Notify)));
            steps.Add(($"cycle {n}: toggles left it open", () => { }));
            steps.Add(($"cycle {n}: dismiss (click-away)", menu.DismissNow));
            steps.Add(($"cycle {n}: settled after dismiss", () => { }));
        }

        var step = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        timer.Tick += (_, _) =>
        {
            if (step > 0)
            {
                Record(steps[step - 1].Label);
            }

            if (step == steps.Count)
            {
                timer.Stop();
                var expected = 3;
                report.AppendLine();
                report.AppendLine($"activations of 'Check for updates…' : {fired["updates"]} of {expected} expected");
                report.AppendLine($"activations of 'Copy diagnostics'   : {fired["diagnostics"]} of {expected} expected");
                report.AppendLine($"RESULT: {(fired["updates"] == expected && fired["diagnostics"] == expected ? "PASS" : "FAIL")}");
                File.WriteAllText(path, report.ToString());
                Shutdown();
                return;
            }

            steps[step++].Do();
        };
        timer.Start();
    }

    /// <summary>
    /// Exercises the tray click the way a user does: open, close, open again — each step
    /// through the same ShowPopup() the icon calls, spaced far enough apart that the
    /// close transition and the toggle's grace window have both elapsed. Writes what the
    /// panel actually is at each point, so a "stopped reacting" report becomes a fact
    /// rather than a guess.
    /// </summary>
    private void RunToggleCheck(string path)
    {
        var log = new System.Text.StringBuilder();
        var step = 0;

        void Record(string label)
        {
            var p = _popup;
            log.AppendLine($"{label,-28} visible={p?.IsVisible} opacity={p?.Opacity:0.00} " +
                           $"clickAway={p?.ClosedByClickAway} " +
                           $"size={p?.ActualWidth:0}x{p?.ActualHeight:0} " +
                           $"left={p?.Left:0} top={p?.Top:0}");
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        timer.Tick += (_, _) =>
        {
            switch (step++)
            {
                case 0:
                    ShowPopup();
                    break;
                case 1:
                    Record("after 1st click (open)");
                    ShowPopup();          // the second click, while it is open
                    break;
                case 2:
                    Record("after 2nd click (close)");
                    break;
                case 3:
                    ShowPopup();          // the third click — should open again
                    break;
                case 4:
                    Record("after 3rd click (reopen)");
                    ShowPopup();
                    break;
                case 5:
                    Record("after 4th click (close)");
                    ShowPopup();
                    break;
                case 6:
                    Record("after 5th click (reopen)");
                    timer.Stop();
                    File.WriteAllText(path, log.ToString());
                    Shutdown();
                    break;
            }
        };
        timer.Start();
    }

    private void ShowPopup()
    {
        if (_controller is null || _store is null)
        {
            return;
        }

        var popup = EnsurePopup();

        // Clicking the tray icon is a TOGGLE, as every taskbar flyout is. The click
        // itself dismisses the panel by taking focus away from it, so by the time this
        // runs the panel is already closing — reopening it here made the icon a
        // one-way switch that could only ever open. Treat a click that lands right
        // after a dismissal as the second half of that toggle and leave it closed.
        if (popup.IsVisible || popup.ClosedByClickAway)
        {
            popup.DismissNow();
            return;
        }

        _controller.Refresh();  // fresh data on open; local reads are cheap
        // Inspected on open (not cached) so the banner reflects the file as it is now.
        popup.DataReport = PlanHistoryDiagnostics.Inspect();
        popup.ShowNearTrayIcon(
            _controller.Latest,
            BuildStatistics(),
            ClaudeAccount.TryRead());
    }

    private PanelStatistics BuildStatistics()
    {
        var utcNow = DateTimeOffset.UtcNow;
        var stats = PanelStatistics.Build(_store!, utcNow);
        if (_planHistory is null)
        {
            return stats;
        }

        var (windowStart, percents) = _planHistory.GetCurrentWindow(utcNow);

        if (_simulateDivergence is { } mode)
        {
            // Feed the real detector synthetic inputs rather than faking its output,
            // so the simulation exercises the same code path the real case would.
            var fake = mode == "limit" ? new[] { 99, 100 } : [6, 6, 6];
            return stats.WithDivergence(_store!, windowStart, fake) with
            {
                EstOffPlanUsd = 92.75m,
                Divergence = DivergenceDetector.Evaluate(fake, 69_091),
            };
        }

        return stats.WithDivergence(_store!, windowStart, percents);
    }

    /// <summary>
    /// Notifies once when usage starts bypassing the plan. Edge-triggered like the
    /// threshold watcher, and re-armed when it stops — the point is to catch the
    /// silent-and-expensive case the plan bars cannot show, not to nag.
    /// </summary>
    private void CheckOffPlan(FileLog? log)
    {
        if (_store is null || _planHistory is null || _trayHost is null)
        {
            return;
        }

        try
        {
            var stats = BuildStatistics();
            if (!stats.IsOffPlan)
            {
                _offPlanNotified = false;
                return;
            }

            log?.Write($"off-plan detected state={stats.Divergence?.State} " +
                       $"tokens={stats.Divergence?.OutputTokensInWindow} rise={stats.Divergence?.PlanRisePoints}");

            if (_offPlanNotified || !_settings.NotifyOnThreshold)
            {
                return;
            }

            _offPlanNotified = true;
            var spend = stats.EstOffPlanUsd is { } usd
                ? $" Est. {usd.ToString("C", System.Globalization.CultureInfo.GetCultureInfo("en-US"))} so far this window."
                : "";
            _trayHost.ShowNotification("Usage is billing beyond your plan",
                $"Work this session isn't drawing from your plan allowance.{spend} Open O-view for detail.");
        }
        catch (Exception ex)
        {
            log?.Write($"off-plan check FAILED {ex.GetType().Name}: {ex.Message}");
        }
    }

    private PopupWindow EnsurePopup() => _popup ??= new PopupWindow();

    /// <summary>
    /// Right-click menu — a docked flyout window, not a ContextMenu (issue #33; see
    /// <see cref="MenuWindow"/> for why the cursor-placed menu had to go). Still no
    /// WinForms: that stays confined to NotifyIcon itself (CLAUDE.md rule 5).
    /// </summary>
    private void ShowMenu()
    {
        _menu ??= CreateMenu();

        // Right-click toggles too, for the same reason the left-click does.
        if (_menu.IsVisible || _menu.ClosedByClickAway)
        {
            _menu.DismissNow();
            return;
        }

        // State is passed on every open, never cached: both settings can change
        // externally (another instance, a manual registry edit, Task Manager's
        // startup page).
        _menu.ShowDocked(
            runAtStartup: StartupRegistration.IsEnabled(),
            notifyOnThreshold: _settings.NotifyOnThreshold,
            thresholdPercent: _settings.ThresholdPercent,
            version: UpdateService.CurrentVersion);
    }

    private MenuWindow CreateMenu()
    {
        var menu = new MenuWindow
        {
            // Both setters return the state as it actually stands afterwards, not the
            // state that was requested — a registry write can fail, and a tick that
            // claims otherwise would be a fabricated fact about the machine.
            SetRunAtStartup = enable =>
            {
                var ok = enable ? StartupRegistration.Enable() : StartupRegistration.Disable();
                return ok ? enable : StartupRegistration.IsEnabled();
            },
            SetNotifyOnThreshold = enable =>
            {
                _settings = _settings with { NotifyOnThreshold = enable };
                _settings.Save();
                return _settings.NotifyOnThreshold;
            },
        };

        // One-click support bundle: a blank panel is otherwise indistinguishable from
        // "Desktop missing", "unexpected file format", or "file unreadable", and asking
        // users to run PowerShell by hand is not a diagnosis path.
        menu.CopyDiagnosticsRequested += (_, _) => CopyDiagnostics();
        // "Check for updates" sits directly above Exit, as requested in issue #18.
        menu.CheckForUpdatesRequested += async (_, _) => await CheckForUpdatesInteractiveAsync();
        menu.ExitRequested += (_, _) => Shutdown();

        return menu;
    }

    /// <summary>
    /// Puts a support bundle on the clipboard: what the app reads, from where, and what it
    /// found. Covers both usage sources — the plan-history file (session/weekly %) and the
    /// JSONL transcripts (token tiles) — plus whether account info resolved, because which
    /// of those are blank narrows the cause immediately. Contains no token and no
    /// conversation content; the org UUID is included as it is the documented filter key.
    /// </summary>
    private void CopyDiagnostics()
    {
        try
        {
            System.Windows.Clipboard.SetText(BuildDiagnostics());
            _trayHost?.ShowNotification("Diagnostics copied",
                "Paste them into your bug report. No tokens or conversation content are included.");
        }
        catch (Exception)
        {
            // The clipboard can be locked by another process; failing to copy must not crash.
            _trayHost?.ShowNotification("Couldn't copy diagnostics",
                "The clipboard was unavailable. Please try again.");
        }
    }

    /// <summary>Writes the diagnostics report to a file (the --diagnose hook).</summary>
    private static void WriteDiagnostics(string? path)
    {
        var target = path is { Length: > 0 } p ? p : "oview-diagnostics.txt";
        try
        {
            File.WriteAllText(target, BuildDiagnostics());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Diagnostic hook — a failure here must not surface as a crash dialog.
        }
    }

    private static string BuildDiagnostics()
    {
        var report = PlanHistoryDiagnostics.Inspect();
        var account = ClaudeAccount.TryRead();

        var text = new System.Text.StringBuilder();
        text.Append(report.ToClipboardText(UpdateService.CurrentVersion));
        // The resolved roots matter: if SpecialFolder resolution ever returns something
        // unexpected, the path above is wrong and every other field is a consequence.
        text.AppendLine($"  appdata root  : {Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)}");
        text.AppendLine($"  user profile  : {Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}");
        text.AppendLine($"  process       : {Environment.ProcessPath}");
        text.AppendLine($"  installed     : {UpdateService.IsInstalled}");
        text.AppendLine($"  account file  : {(account is null ? "not readable" : "read ok")}"
                        + $" (org {account?.OrganizationUuid ?? "n/a"}, tier {account?.Tier ?? "n/a"})");

        // The token tiles come from Claude Code transcripts, a different source entirely.
        // Size and recency matter as much as the count: four empty or long-stale files
        // produce a legitimate zero, whereas fat, freshly written files pointing at a zero
        // total would mean ingestion is broken. A bare count cannot tell those apart.
        var projects = ClaudeProjectsLocator.DefaultRoot;
        var files = ClaudeProjectsLocator.FindTranscripts(projects);
        long bytes = 0;
        DateTime newest = DateTime.MinValue;
        foreach (var f in files)
        {
            try
            {
                var info = new FileInfo(f);
                bytes += info.Length;
                if (info.LastWriteTimeUtc > newest)
                {
                    newest = info.LastWriteTimeUtc;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Per-file stat is diagnostic only.
            }
        }

        text.AppendLine($"  transcripts   : {files.Count} .jsonl, {bytes:N0} bytes total under {projects}");
        text.AppendLine($"  newest transcript: {(newest == DateTime.MinValue ? "n/a" : $"{(DateTime.UtcNow - newest).TotalHours:0.0} h old")}");
        AppendWeeklyResets(text);
        return text.ToString();
    }

    /// <summary>
    /// What the weekly-reset discovery has actually found (ADR-0011). "Weekly says
    /// waiting" is otherwise undiagnosable from the outside: it looks identical whether no
    /// reset has occurred yet, the file was never written, or drops are being detected and
    /// discarded. Listing the observations with their brackets separates those in one
    /// glance. Timestamps only — no usage figures, nothing identifying.
    /// </summary>
    private static void AppendWeeklyResets(System.Text.StringBuilder text)
    {
        try
        {
            var log = new WeeklyResetLog();
            var observations = log.GetObservations();
            text.AppendLine($"  weekly resets : {observations.Count} observed ({WeeklyResetLog.DefaultPath})");
            foreach (var o in observations.TakeLast(5))
            {
                text.AppendLine($"    reset       : by {o.LatestUtc:u} "
                                + $"(± {o.Uncertainty.TotalMinutes:0} min{(o.IsPrecise ? "" : ", Desktop not sampling")})");
            }

            var next = WeeklyResetDetector.PredictNextReset(observations, DateTimeOffset.UtcNow);
            text.AppendLine($"  next weekly   : {(next is null ? "unknown — waiting for first reset" : $"{next.AtUtc:u}")}");
        }
        catch (Exception ex)
        {
            text.AppendLine($"  weekly resets : unreadable ({ex.GetType().Name})");
        }
    }

    /// <summary>
    /// Background update cadence: one check ~30 s after launch (so it neither slows startup
    /// nor races the first refresh), then daily. A DispatcherTimer keeps every callback on
    /// the UI thread, so surfacing a balloon or a dialog needs no marshalling.
    /// </summary>
    private void StartUpdateChecks()
    {
        var initial = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        initial.Tick += (_, _) =>
        {
            initial.Stop();
            _ = BackgroundCheckAsync();
        };
        initial.Start();

        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(24) };
        _updateTimer.Tick += (_, _) => _ = BackgroundCheckAsync();
        _updateTimer.Start();
    }

    /// <summary>
    /// Quiet check: only *notifies* when a newer release exists, and only once per version
    /// (re-notifying every day would be nagging). It never downloads or installs anything —
    /// that stays behind the explicit menu action and its confirmation.
    /// </summary>
    private async Task BackgroundCheckAsync()
    {
        if (_updates is null || _trayHost is null || _updateFlowActive)
        {
            return;
        }

        var result = await _updates.CheckAsync();
        if (result is { Outcome: UpdateOutcome.UpdateAvailable, Available: { } update }
            && _notifiedUpdateTag != update.Tag)
        {
            _notifiedUpdateTag = update.Tag;
            _trayHost.ShowNotification("O-view update available",
                $"Version {update.Version} is available (you have {UpdateService.CurrentVersion}). " +
                "Right-click the icon → Check for updates to install.");
        }
    }

    /// <summary>
    /// The menu action: always reports an outcome (up to date, available, or unreachable),
    /// and offers to install when a newer release exists.
    /// </summary>
    private async Task CheckForUpdatesInteractiveAsync()
    {
        if (_updates is null || _trayHost is null || _updateFlowActive || _updateCheckActive)
        {
            // _updateCheckActive covers the whole interactive flow, including the modal
            // confirmation; _updateFlowActive only covers the download that follows it. A
            // second click while the first check is still awaiting the network — or sitting
            // on the confirm dialog — would otherwise start an independent flow and stack a
            // second modal behind the first.
            return;
        }

        _updateCheckActive = true;
        try
        {
            await RunUpdateCheckAsync();
        }
        finally
        {
            _updateCheckActive = false;
        }
    }

    private async Task RunUpdateCheckAsync()
    {
        var result = await _updates!.CheckAsync();
        switch (result.Outcome)
        {
            case UpdateOutcome.UpToDate:
                _trayHost!.ShowNotification("O-view is up to date",
                    $"You have the latest version ({UpdateService.CurrentVersion}).");
                break;

            case UpdateOutcome.UpdateAvailable when result.Available is { } update:
                await OfferUpdateAsync(update);
                break;

            default:
                _trayHost!.ShowNotification("Couldn't check for updates",
                    "O-view couldn't reach GitHub to check for a newer version. Please try again later.");
                break;
        }
    }

    /// <summary>
    /// Confirms with the user, then — for an installed build — downloads the installer and
    /// hands off to it (the app exits so the installer can replace the exe and relaunch it).
    /// A portable build cannot replace its own running exe, so it is sent to the release page.
    /// </summary>
    private async Task OfferUpdateAsync(AvailableUpdate update)
    {
        if (_updates is null || _trayHost is null)
        {
            return;
        }

        if (!UpdateService.IsInstalled)
        {
            if (ConfirmUpdate(update, "Open the download page for the new version?", "Open page"))
            {
                _updates.OpenInBrowser(UpdateService.ReleasePageUrl(update));
            }
            return;
        }

        if (!ConfirmUpdate(update, "Download and install it now?", "Update now",
            "O-view will close briefly and reopen automatically."))
        {
            return;
        }

        _updateFlowActive = true;
        try
        {
            var installer = await _updates.DownloadInstallerAsync(update);
            _updates.LaunchInstaller(installer);
            Shutdown();  // release the exe lock so the installer can replace it and relaunch
        }
        catch (Exception)
        {
            _updateFlowActive = false;
            _trayHost.ShowNotification("Update failed",
                "O-view couldn't download the update. Opening the releases page so you can download it manually.");
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
            title: "Update available",
            message: $"O-view {update.Version} is available — you have {UpdateService.CurrentVersion}. {question}",
            confirmLabel: confirmLabel,
            cancelLabel: "Not now",
            detail: detail);

    protected override void OnExit(ExitEventArgs e)
    {
        _updateTimer?.Stop();
        _controller?.Dispose();
        _trayHost?.Dispose();
        _store?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static Dictionary<string, string?> ParseArgs(string[] args)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                result[args[i]] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                    ? args[++i]
                    : null;
            }
        }
        return result;
    }

    /// <summary>
    /// Renders the tray-menu flyout in both themes, with the toggles on and off, over a
    /// desktop-ish backdrop so the rounded card and its border are actually visible
    /// (issue #33). Visual verification only — nothing here runs in normal operation.
    /// </summary>
    private static void RenderMenuSamples(string dir)
    {
        Directory.CreateDirectory(dir);
        const double scale = 2.0;
        const double pad = 16;

        foreach (var light in new[] { true, false })
        {
            foreach (var (name, startup, notify) in new (string, bool, bool)[]
            {
                ("both-on", true, true),
                ("both-off", false, false),
            })
            {
                var menu = new MenuWindow { ThemeOverride = light };
                menu.Populate(startup, notify, 70, UpdateService.CurrentVersion);
                var card = menu.RenderToBitmap(scale);

                // The flyout is a transparent-cornered window; composited over a flat
                // backdrop, the corner radius and border read the way they will on screen.
                var w = card.PixelWidth / scale;
                var h = card.PixelHeight / scale;
                var visual = new System.Windows.Media.DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawRectangle(
                        new System.Windows.Media.SolidColorBrush(light
                            ? System.Windows.Media.Color.FromRgb(0xDE, 0xDE, 0xDE)
                            : System.Windows.Media.Color.FromRgb(0x10, 0x10, 0x10)),
                        null,
                        new System.Windows.Rect(0, 0, w + 2 * pad, h + 2 * pad));
                    dc.DrawImage(card, new System.Windows.Rect(pad, pad, w, h));
                }

                var composed = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)((w + 2 * pad) * scale), (int)((h + 2 * pad) * scale),
                    96 * scale, 96 * scale, System.Windows.Media.PixelFormats.Pbgra32);
                composed.Render(visual);

                var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
                png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(composed));
                using var stream = File.Create(
                    Path.Combine(dir, $"menu-{name}-{(light ? "light" : "dark")}.png"));
                png.Save(stream);
            }
        }
    }

    /// <summary>
    /// Renders the stat tiles across the model counts that change the chart's shape,
    /// in both themes and both views (issue #37). The cases that matter — a folded
    /// "Other", an unpriced model — are ones a given machine's real data may never
    /// produce, so they are constructed rather than waited for.
    /// </summary>
    private static void RenderTileSamples(string dir)
    {
        Directory.CreateDirectory(dir);

        ModelSlice S(string model, long tokens, decimal? usd) =>
            new(model, ModelDisplayName.For(model), tokens, usd);

        // The 31-day window, and the tiles that draw from it. Deliberately shaped like
        // the reported case: "today" contains ONLY Opus 5, which used to make Opus 5 the
        // first segment there and therefore blue, while the 31-day tile ranked Opus 4.8
        // first and painted Opus 5 orange. One colour order, derived from the window,
        // is what stops that — so the samples share one, exactly as the panel does.
        ModelSlice[] window =
        [
            S("claude-opus-4-8", 350_000_000, 250.00m),
            S("claude-opus-5", 180_000_000, 120.00m),
            S("claude-fable-5", 40_000_000, 50.00m),
            S("claude-haiku-4-5", 5_900_000, 5.21m),
        ];
        var colourOrder = ModelBreakdown.ColourOrder(window);

        var cases = new (string Name, ModelSlice[] Slices)[]
        {
            ("today — Opus 5 only (was blue here, orange below)", [S("claude-opus-5", 155_400_000, 91.44m)]),
            ("31 days — all four models", window),
            ("two of the window's models", [window[0], window[2]]),
            ("three models, no remainder", [.. window.Take(3)]),
            ("unpriced model present",
                [S("claude-opus-4-8", 150_000_000, 30.00m), S("claude-brandnew-9", 4_000_000, null)]),
        };

        foreach (var light in new[] { true, false })
        {
            foreach (var expanded in new[] { false, true })
            {
                var rows = new StackPanel { Margin = new Thickness(16) };
                var pending = new List<(StatTile Tile, string Label, string Value, ModelSlice[] Slices, BreakdownMeasure Measure)>();
                foreach (var (name, slices) in cases)
                {
                    rows.Children.Add(new TextBlock
                    {
                        Text = name,
                        FontSize = 11,
                        Margin = new Thickness(0, 6, 0, 3),
                        Foreground = new System.Windows.Media.SolidColorBrush(light
                            ? System.Windows.Media.Color.FromRgb(0x33, 0x33, 0x33)
                            : System.Windows.Media.Color.FromRgb(0xCC, 0xCC, 0xCC)),
                    });

                    var pair = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                    var tokensTile = NewSampleTile(BreakdownMeasure.Tokens);
                    var valueTile = NewSampleTile(BreakdownMeasure.EstValue);
                    pair.Children.Add(tokensTile);
                    pair.Children.Add(valueTile);
                    rows.Children.Add(pair);

                    // Carry the coverage caveat on the samples too: it has to survive
                    // into the breakdown view, and a sample that never shows one cannot
                    // catch it going missing (it did).
                    pending.Add((tokensTile, "Tokens · 31 days",
                        FormatSampleTokens(slices.Sum(s => s.Tokens)), slices, BreakdownMeasure.Tokens));
                    pending.Add((valueTile, "Est. value · 31 days",
                        SumSampleUsd(slices), slices, BreakdownMeasure.EstValue));
                }

                // The tiles resolve their colours through DynamicResource, so the palette
                // has to live on a host they are parented to.
                var host = new Border
                {
                    Child = rows,
                    Background = new System.Windows.Media.SolidColorBrush(light
                        ? System.Windows.Media.Color.FromRgb(0xF9, 0xF9, 0xF9)
                        : System.Windows.Media.Color.FromRgb(0x20, 0x20, 0x20)),
                };
                PanelTheme.Apply(host.Resources, light);

                // Only now: the breakdown resolves its series colours through the
                // logical tree, so a tile populated before it is parented and themed has
                // nowhere to find them. The real panel gets this ordering for free — its
                // tiles are declared in XAML and the theme is applied before Populate.
                foreach (var (tile, label, value, slices, _) in pending)
                {
                    tile.Populate(label, value, "8 of 31 days recorded", slices, tile.SplitBy, colourOrder);
                    tile.SetExpanded(expanded);
                }

                if (expanded)
                {
                    RenderHoverCards(dir, light,
                    [
                        pending[6].Tile.BuildSampleTooltip(0),   // 5-models: named model, tokens
                        pending[6].Tile.BuildSampleTooltip(2),   // 5-models: folded "Other"
                        pending[7].Tile.BuildSampleTooltip(0),   // 5-models: named model, est. value
                        // An UNRECOGNISED model, where the friendly name is the raw id.
                        // Included because that is the case where dropping the card's
                        // title could have left nothing naming the row at all.
                        pending[8].Tile.BuildSampleTooltip(1),
                    ]);
                }

                host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                host.Arrange(new Rect(host.DesiredSize));
                host.UpdateLayout();

                const double scale = 2.0;
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)Math.Ceiling(host.ActualWidth * scale), (int)Math.Ceiling(host.ActualHeight * scale),
                    96 * scale, 96 * scale, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(host);

                var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
                png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using (var stream = File.Create(Path.Combine(dir,
                    $"tiles-{(expanded ? "breakdown" : "summary")}-{(light ? "light" : "dark")}.png")))
                {
                    png.Save(stream);
                }

                // Hover timing cannot be seen in a still, and the part that can silently
                // break is the inheritance from the control down to the segments — a
                // segment that missed it would fall back to WPF's defaults and look
                // identical. Report the resolved values so they are checked, not assumed.
                if (expanded && pending.FirstOrDefault().Tile?.ResolvedHoverTiming() is { } timing)
                {
                    // Intended values only — no "framework default" annotations. The
                    // unset baseline measured on the dev machine (1000 / 100 /
                    // int.MaxValue) did not match the documented defaults, so printing
                    // documented figures beside measured ones would assert something
                    // unverified.
                    File.WriteAllText(Path.Combine(dir, "hover-timing.txt"),
                        $"resolved on a bar segment ({(light ? "light" : "dark")}):{Environment.NewLine}" +
                        $"  InitialShowDelay : {timing.InitialShowDelay} ms (intended 400){Environment.NewLine}" +
                        $"  BetweenShowDelay : {timing.BetweenShowDelay} ms (intended 3000){Environment.NewLine}" +
                        $"  ShowDuration     : {timing.ShowDuration} ms (intended 20000){Environment.NewLine}");
                }
            }
        }
    }

    /// <summary>
    /// Renders the update dialog in both themes and both variants — the installed path
    /// ("Update now", with the relaunch note) and the portable one ("Open page").
    /// </summary>
    private static void RenderDialogSamples(string dir)
    {
        Directory.CreateDirectory(dir);
        const double scale = 2.0;
        const double pad = 16;

        var variants = new (string Name, string Message, string Confirm, string Detail)[]
        {
            ("installed", "O-view 0.5.1 is available — you have 0.5.0. Download and install it now?",
                "Update now", "O-view will close briefly and reopen automatically."),
            ("portable", "O-view 0.5.1 is available — you have 0.5.0. Open the download page for the new version?",
                "Open page", ""),
        };

        foreach (var light in new[] { true, false })
        {
            foreach (var (name, message, confirm, detail) in variants)
            {
                var dialog = new DialogWindow { ThemeOverride = light };
                dialog.Populate("Update available", message, confirm, "Not now", detail);
                var card = dialog.RenderToBitmap(scale);

                var w = card.PixelWidth / scale;
                var h = card.PixelHeight / scale;
                var visual = new System.Windows.Media.DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawRectangle(
                        new System.Windows.Media.SolidColorBrush(light
                            ? System.Windows.Media.Color.FromRgb(0xDE, 0xDE, 0xDE)
                            : System.Windows.Media.Color.FromRgb(0x10, 0x10, 0x10)),
                        null, new Rect(0, 0, w + 2 * pad, h + 2 * pad));
                    dc.DrawImage(card, new Rect(pad, pad, w, h));
                }

                var composed = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)((w + 2 * pad) * scale), (int)((h + 2 * pad) * scale),
                    96 * scale, 96 * scale, System.Windows.Media.PixelFormats.Pbgra32);
                composed.Render(visual);

                var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
                png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(composed));
                using var stream = File.Create(Path.Combine(dir,
                    $"dialog-{name}-{(light ? "light" : "dark")}.png"));
                png.Save(stream);
            }
        }
    }

    /// <summary>
    /// Renders the hover cards to their own PNG. They need their own pass because a
    /// ToolTip cannot be given a parent — it throws — so it can neither be laid out
    /// inside the sample panel nor appear in a screenshot of the panel itself. Measured,
    /// arranged and rendered standalone instead, with the palette written into each
    /// card's own resources so its DynamicResource lookups resolve without a tree above
    /// it to walk.
    /// </summary>
    private static void RenderHoverCards(string dir, bool light, System.Windows.Controls.ToolTip?[] tips)
    {
        const double scale = 2.0;
        const double gap = 12;

        var rendered = new List<System.Windows.Media.Imaging.BitmapSource>();
        foreach (var tip in tips)
        {
            if (tip is null)
            {
                continue;
            }

            PanelTheme.Apply(tip.Resources, light);
            tip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            tip.Arrange(new Rect(tip.DesiredSize));
            tip.UpdateLayout();

            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)Math.Ceiling(tip.DesiredSize.Width * scale),
                (int)Math.Ceiling(tip.DesiredSize.Height * scale),
                96 * scale, 96 * scale, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(tip);
            rendered.Add(bitmap);
        }

        if (rendered.Count == 0)
        {
            return;
        }

        var totalWidth = rendered.Sum(b => b.PixelWidth / scale) + gap * (rendered.Count + 1);
        var totalHeight = rendered.Max(b => b.PixelHeight / scale) + 2 * gap;

        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // Over the tile surface the cards actually float above, so the elevation
            // step reads the way it will in the app.
            dc.DrawRectangle(
                new System.Windows.Media.SolidColorBrush(light
                    ? System.Windows.Media.Color.FromRgb(0xEF, 0xEF, 0xEF)
                    : System.Windows.Media.Color.FromRgb(0x2B, 0x2B, 0x2B)),
                null, new Rect(0, 0, totalWidth, totalHeight));

            var x = gap;
            foreach (var card in rendered)
            {
                dc.DrawImage(card, new Rect(x, gap, card.PixelWidth / scale, card.PixelHeight / scale));
                x += card.PixelWidth / scale + gap;
            }
        }

        var composed = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Math.Ceiling(totalWidth * scale), (int)Math.Ceiling(totalHeight * scale),
            96 * scale, 96 * scale, System.Windows.Media.PixelFormats.Pbgra32);
        composed.Render(visual);

        var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
        png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(composed));
        using var stream = File.Create(Path.Combine(dir, $"hover-cards-{(light ? "light" : "dark")}.png"));
        png.Save(stream);
    }

    private static StatTile NewSampleTile(BreakdownMeasure measure) =>
        new()
        {
            Width = 180,
            Margin = new Thickness(0, 0, 8, 0),
            SplitBy = measure,
            FormatSlice = measure == BreakdownMeasure.Tokens
                ? s => FormatSampleTokens(s.Tokens)
                : s => s.EstUsd is { } v ? "$" + v.ToString("0.00", CultureInfo.InvariantCulture) : "unknown",
        };

    private static string FormatSampleTokens(long tokens) => tokens switch
    {
        >= 1_000_000 => string.Create(CultureInfo.InvariantCulture, $"{tokens / 1_000_000.0:0.0}M"),
        >= 1_000 => string.Create(CultureInfo.InvariantCulture, $"{tokens / 1_000.0:0.0}K"),
        _ => tokens.ToString(CultureInfo.InvariantCulture),
    };

    private static string SumSampleUsd(ModelSlice[] slices) =>
        "$" + slices.Sum(s => s.EstUsd ?? 0m).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>Renders every icon state at 100% and 150% scaling sizes for visual verification.</summary>
    private static void RenderSamples(string dir)
    {
        Directory.CreateDirectory(dir);
        var states = new (string Name, UsageSnapshot Snapshot)[]
        {
            ("live-06", new(DataSource.Live, 6, 1, null, DateTimeOffset.UtcNow)),
            ("live-47", new(DataSource.Live, 47, 20, null, DateTimeOffset.UtcNow)),
            ("live-58", new(DataSource.Live, 58, 30, null, DateTimeOffset.UtcNow)),
            ("live-72", new(DataSource.Live, 72, 40, null, DateTimeOffset.UtcNow)),
            ("live-91", new(DataSource.Live, 91, 60, null, DateTimeOffset.UtcNow)),
            ("live-100", new(DataSource.Live, 100, 80, null, DateTimeOffset.UtcNow)),
            ("estimate", new(DataSource.Estimate, null, null, null, DateTimeOffset.UtcNow)),
            ("none", UsageSnapshot.None),
        };

        foreach (var size in new[] { 16, 24 })  // 100% and 150% scaling
        {
            foreach (var light in new[] { false, true })
            {
                foreach (var (name, snapshot) in states)
                {
                    using var bmp = IconRenderer.Render(size, snapshot, light);
                    bmp.Save(Path.Combine(dir, $"{name}-{size}px-{(light ? "light" : "dark")}.png"));
                }
            }
        }
    }
}
