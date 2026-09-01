using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using OView.App;
using OView.App.Diagnostics;
using OView.App.Platform;
using OView.App.Pricing;
using OView.App.Updates;
using OView.Core.Models;
using OView.Core.Providers.CachedUsage;
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
/// --popup-samples dir (render the detail panel offscreen in both themes), --menu-check
/// path (activate every tray-menu row over repeated open/close cycles and report what
/// fired — the menu's equivalent of --toggle-check).
/// </summary>
public partial class App : System.Windows.Application
{
    private ISingleInstanceGuard? _instance;
    private readonly IStartupRegistration _startup = new RegistryStartupRegistration();
    private readonly IThemeSource _theme = new RegistryThemeSource();
    private UsageEngine? _engine;
    private NotifyIconTrayHost? _trayHost;
    private TrayController? _controller;
    private PopupWindow? _popup;
    private MenuWindow? _menu;
    private UpdateService? _updates;
    private bool _updateFlowActive;

    /// <summary>
    /// An interactive update check is running, including its modal confirmation. Distinct
    /// from <see cref="_updateFlowActive"/>, which only spans the download that may follow.
    /// </summary>
    private bool _updateCheckActive;
    private string? _notifiedUpdateTag;

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
        ("--diagnose", WriteDiagnostics),
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
        // live instance — which for --diagnose is the case that actually needs diagnosing,
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
        // pre-mutex for the same reason — it drives a panel of its own and must not fight the
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
            // Exiting silently is right for a second ordinary launch — the user clicked the
            // shortcut twice and the running instance is what they wanted. It is wrong for a
            // verification hook: --popup-check needs the assembled app, so unlike the pre-mutex
            // checks it cannot run beside a live instance, and a check that produces no report
            // is indistinguishable from one that failed (issue #249).
            if (args.TryGetValue("--popup-check", out var blockedReport))
            {
                File.WriteAllText(
                    blockedReport ?? "popup-check.txt",
                    "popup check NOT RUN — another instance holds the single-instance mutex.\n\n" +
                    "This check drives the real ShowPopup(), so it needs the engine and tray host\n" +
                    "that only exist after startup — which means it cannot run pre-mutex the way\n" +
                    "--menu-check and --fold-check do. Close the running O-view and try again.\n\n" +
                    "RESULT: NOT RUN\n");
            }

            Shutdown();
            return;
        }

        // On by default; --log only redirects it. It was opt-in until a machine stalled for
        // five days and the one thing that would have named the failing call was a flag
        // nobody had passed — a log you have to enable before the fault reproduces is never
        // on when it matters. FileLog bounds itself, so always-on costs a capped 6 MB.
        var log = new FileLog(args.TryGetValue("--log", out var logPath) ? logPath : null);
        log.WriteSessionHeader(UpdateService.CurrentVersion, UpdateService.CurrentInstallKind.ToString());
        var interval = args.TryGetValue("--interval-ms", out var ms) &&
                       int.TryParse(ms, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 50
            ? TimeSpan.FromMilliseconds(parsed)
            : TimeSpan.FromSeconds(60);

        // Everything about *what the numbers mean* — provider composition, the poll loop and
        // its cadence, the rollup store and weekly-reset log lifecycle, threshold and
        // off-plan decisions, settings, and the update schedule — lives in the engine, which
        // is platform-neutral and tested (ADR-0012). What stays here is the Windows face.
        _engine = new UsageEngine(new UsageEngineOptions
        {
            PollInterval = interval,
            Log = log,
            // Off-plan rendering can't be produced on demand from real data, so it is
            // verifiable via simulation. This whole feature exists because a UI failed
            // to communicate something expensive — the UI itself needs verifying.
            SimulateDivergence = args.TryGetValue("--simulate-divergence", out var sim)
                ? sim ?? "diverging"
                : null,

            // Lets the engine ask Claude Code to refresh its own usage cache (issue #234).
            // Without it, a machine with no Claude Desktop shows "unknown" indefinitely: Claude
            // Code refreshes that block only when /usage runs, so a 4.43-day-old one was measured
            // on a machine that had been running Claude Code all morning.
            //
            // No credential is involved — Claude Code authenticates itself and O-view reads the
            // file it already reads (ADR-0015).
            UsageCacheRefresher = new ClaudeCliRefresher(),
        });

        _trayHost = new NotifyIconTrayHost();
        _controller = new TrayController(_trayHost, _engine, _theme, log);

        _trayHost.IconClicked += (_, _) => ShowPopup();
        _trayHost.IconRightClicked += (_, _) => ShowMenu();

        // Auto-update (ADR-0009): a quiet background check surfaces a newer release as a
        // balloon; the actual download-and-install is only ever done from the menu, with
        // the user's confirmation. The engine owns the *when*; the HTTP and the UI are here.
        _updates = new UpdateService(log);
        _engine.UpdateCheckDue += () => _ = BackgroundCheckAsync();

        // The rate-card drift check (ADR-0016). It writes a log line and nothing else — it
        // never installs a rate, and a difference is a maintainer's cue rather than a user's
        // problem, so there is no balloon and no UI. The line reaches a bug report through the
        // diagnostics bundle's log tail.
        var rates = new RateCardFeed(log);
        _engine.RateCheckDue += () => _ = rates.CheckAsync(ModelCatalog.Bundled);

        // A conflict is discovered while resolving the weekly reset, which happens on every
        // poll — so this rides the same signal rather than needing its own schedule. The
        // notice itself fires once per conflicting observation (issue #186).
        _engine.SnapshotUpdated += _ => NotifyWeeklyResetConflict();

        // Ticks arrive on the UI thread; the reading happens off it and comes back through
        // the dispatcher, so a first ingest over a large transcript history cannot hold the
        // message pump (issue #125).
        _engine.Start(new DispatcherTimerFactory(), new WpfUiDispatcher());

        if (args.ContainsKey("--test-notify"))
        {
            _trayHost.ShowNotification("Claude usage", "Test notification (--test-notify).",
                NotificationKind.Information);
        }

        // Verification hook: force an interactive update check (as if from the menu).
        if (args.ContainsKey("--check-updates"))
        {
            _ = CheckForUpdatesInteractiveAsync();
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
            _controller.StressTest(iterations);
        }

        // The tray click, driven on the real desktop over ~5 s. Not beside --menu-check and
        // --fold-check in the pre-mutex table: this one needs the assembled app, because
        // driving the real ShowPopup is the point (issue #249).
        if (args.TryGetValue("--popup-check", out var popupCheckReport))
        {
            RunPopupCheck(popupCheckReport ?? "popup-check.txt");
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
        if (_engine is null)
        {
            return;
        }

        var popup = EnsurePopup();
        if (CompletedToggle(popup))
        {
            return;
        }

        _engine.Refresh();  // fresh data on open; local reads are cheap

        // Opening the panel is the moment freshness is observable, so it is what drives the
        // usage-cache refresh rather than a timer (issue #234). Forced, because a person
        // looking is not a poll and must not be held to the background floor.
        //
        // Unlike the Refresh() above this does NOT block the click: it spawns Claude Code off
        // the UI thread and publishes back when it lands, so the panel opens immediately on the
        // current snapshot and updates underneath if the figures moved. Doing it synchronously
        // here would put a process spawn in front of the user's click — issue #125's lesson.
        _engine.RefreshUsageCache(force: true);
        // Both inspected on open (not cached) so the banners reflect the machine as it is
        // now. The scope report is what the token-scope note is built from — resolved
        // roots and real file counts, never a hard-coded path (issue #58).
        popup.DataReport = PlanHistoryDiagnostics.Inspect();
        popup.ScopeReport = TranscriptScopeReport.Inspect();
        popup.SettingsForDisplay = _engine.Settings;
        // Read on open like the two reports above. The flag cache refreshes when Claude Code
        // starts, not when /usage runs, so it is often fresher than the percentages and must
        // be read on its own rather than folded into the poll (issue #254).
        popup.BoostNotices = BoostNotices.TryRead();
        // Whether extra usage is on for this account, for the off-plan banner (issue #259).
        // Taken from the engine rather than read again here: it is the same block the poll
        // above just refreshed, and the engine's reader is the one that honours the
        // injected-provider guard.
        popup.CachedUsage = _engine.CachedUsage;
        popup.ShowNearTrayIcon(
            _engine.Latest,
            _engine.BuildStatistics(),
            ClaudeAccount.TryRead());
    }

    /// <summary>
    /// Treats a tray click as the SECOND half of a toggle where that is what it is, and
    /// returns whether it was handled.
    ///
    /// <para>Clicking the tray icon is a toggle, as every taskbar flyout is. The click
    /// itself dismisses an open surface by taking focus away from it, so by the time this
    /// runs the surface is already closing — reopening it here made the icon a one-way
    /// switch that could only ever open. A click landing right after a dismissal is the
    /// close half, and leaves it closed.</para>
    ///
    /// <para>One guard for both surfaces (issue #54): left-click and right-click behaved
    /// identically here and were written twice.</para>
    /// </summary>
    /// <summary>
    /// Exercises the tray click the way a user does: open, close, open again — each step
    /// through the same <see cref="ShowPopup"/> the icon calls, spaced far enough apart that the
    /// close transition and the toggle's grace window have both elapsed. Writes what the panel
    /// actually is at each point, so a "stopped reacting" report becomes a fact rather than a
    /// guess.
    ///
    /// <para><b>Why this lives on <see cref="App"/> and not beside the other checks</b> (issue
    /// #249). <c>--menu-check</c> and <c>--fold-check</c> sit in <see cref="SampleRenderer"/> and
    /// construct their own windows, so they can run before the single-instance mutex. This one
    /// cannot: driving the <i>real</i> <see cref="ShowPopup"/> is the entire point, and that needs
    /// the engine, the tray host and the settings that only exist after startup. So it runs last,
    /// against the assembled app, and shuts it down when the sequence ends.</para>
    ///
    /// <para><b>The reopen is the assertion that matters.</b> A tray click is a toggle, and the
    /// click itself dismisses an open surface by taking focus from it — so reopening naively made
    /// the icon a one-way switch that could only ever open. <see cref="CompletedToggle"/> is what
    /// stops that, and its grace window is why the steps are spaced rather than run back to
    /// back.</para>
    ///
    /// <para>It also records <c>UsageRefreshAttempts</c> either side of the first open, which is
    /// the one part of issue #234 no unit test could reach: everything behind the panel-open path
    /// is verified, but the call inside <see cref="ShowPopup"/> had never run assembled.</para>
    /// </summary>
    private void RunPopupCheck(string path)
    {
        var report = new System.Text.StringBuilder();
        var refreshesBefore = _engine!.UsageRefreshAttempts;

        void Record(string label) =>
            report.AppendLine(
                $"{label,-34} visible={_popup?.IsVisible.ToString() ?? "<none>",-5} " +
                $"clickAway={_popup?.ClosedByClickAway.ToString() ?? "<none>",-5} " +
                $"refreshAttempts={_engine!.UsageRefreshAttempts}");

        var opened = new bool[2];

        var steps = new List<(string Label, Action Do)>
        {
            ("open (first click)", () => ShowPopup()),
            ("after first open", () => opened[0] = _popup?.IsVisible == true),
            ("dismiss", () => _popup?.DismissNow()),
            ("after dismiss", () => { }),
            ("open again", () => ShowPopup()),
            ("after reopen", () => opened[1] = _popup?.IsVisible == true),
        };

        var step = 0;
        report.AppendLine($"popup check · {UpdateService.CurrentVersion}");
        report.AppendLine($"refresher available: {_engine.CanRefreshUsageCache}");
        report.AppendLine();

        // Comfortably longer than both the close transition and the toggle's grace window, so a
        // failure here is the behaviour rather than the spacing.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        timer.Tick += (_, _) =>
        {
            if (step > 0)
            {
                Record(steps[step - 1].Label);
            }

            if (step == steps.Count)
            {
                timer.Stop();

                var refreshed = _engine!.UsageRefreshAttempts > refreshesBefore;
                report.AppendLine();
                report.AppendLine($"first click opened the panel   : {opened[0]}");
                report.AppendLine($"reopen after dismiss opened it : {opened[1]}");
                report.AppendLine($"panel open triggered a refresh : {refreshed}" +
                                  (_engine.CanRefreshUsageCache ? "" : "  (no refresher — not required)"));

                // The refresh is only required of a build that has a refresher at all; the other
                // two are required of every build, because they are the click working.
                var pass = opened[0] && opened[1] && (refreshed || !_engine.CanRefreshUsageCache);
                report.AppendLine($"RESULT: {(pass ? "PASS" : "FAIL")}");

                File.WriteAllText(path, report.ToString());
                Shutdown();
                return;
            }

            steps[step++].Do();
        };
        timer.Start();
    }

    private static bool CompletedToggle(IFlyout flyout)
    {
        if (!flyout.IsVisible && !flyout.ClosedByClickAway)
        {
            return false;
        }

        flyout.DismissNow();
        return true;
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
        if (CompletedToggle(_menu))
        {
            return;
        }

        // State is passed on every open, never cached: both settings can change
        // externally (another instance, a manual registry edit, Task Manager's
        // startup page).
        _menu.ShowDocked(new MenuWindow.MenuState(
            RunAtStartup: _startup.IsEnabled(),
            NotifyOnThreshold: _engine!.Settings.NotifyOnThreshold,
            ThresholdPercent: _engine.Settings.ThresholdPercent,
            UpdateAutomatically: _engine.Settings.UpdateAutomatically,
            // Asked on every open rather than cached at startup, for the same reason the
            // rest of this is: it is a fact about how this build was installed, and the
            // policy is the single place that decides it (ADR-0009).
            CanUpdateAutomatically: UpdatePolicy.MayDownloadAndRun(UpdateService.CurrentInstallKind),
            Version: UpdateService.CurrentVersion,
            WeeklyReset: WeeklyResetRowLabel(),
            // Empty on every ordinary open; the row only appears once the refresh has stopped
            // itself, which is the only state there is anything to undo (issue #234).
            UsageRefreshBlocked: _engine.UsageRefreshBlocked ?? ""));
    }

    /// <summary>
    /// What the menu row shows on its right: the entered reset, or empty when O-view is
    /// deriving one. Reads the engine on every open rather than caching — the same rule the
    /// rest of the menu state follows.
    /// </summary>
    private string WeeklyResetRowLabel() =>
        _engine?.Settings.WeeklyReset is { } reset ? $"{reset.DayText[..3]} {reset.TimeText}" : "";

    /// <summary>
    /// Opens the weekly-reset entry (issue #186), applies whatever comes back, and refreshes
    /// so the panel and the graph's week gridlines move with it — the gridlines read the
    /// snapshot's weekly reset, so they follow without separate wiring.
    /// </summary>
    private void EditWeeklyReset()
    {
        if (_engine is null)
        {
            return;
        }

        var (entry, changed) = WeeklyResetDialog.Show(_engine.Settings.WeeklyReset);
        if (!changed)
        {
            return;
        }

        _engine.SetWeeklyReset(entry);
        _engine.Refresh();
    }

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
    private void NotifyWeeklyResetConflict()
    {
        if (_engine?.WeeklyResetConflict is not { } conflict
            || !_engine.IsWeeklyResetConflictUnseen(conflict))
        {
            return;
        }

        _engine.MarkWeeklyResetConflictNoticed(conflict);
        _trayHost?.ShowNotification(
            "Weekly reset time doesn't match",
            PanelText.WeeklyResetConflict(conflict, TimeZoneInfo.Local),
            NotificationKind.Warning);
    }

    private MenuWindow CreateMenu()
    {
        var menu = new MenuWindow
        {
            // Both setters return the state as it actually stands afterwards, not the
            // state that was requested — a registry write can fail, and a tick that
            // claims otherwise would be a fabricated fact about the machine. That rule
            // now lives once, on IStartupRegistration.Apply, so both heads share it.
            SetRunAtStartup = _startup.Apply,
            SetNotifyOnThreshold = enable => _engine!.SetNotifyOnThreshold(enable),
            // Same contract as the two above: the row renders what the engine returns, so a
            // value that could not be applied is not drawn as though it had been.
            SetThresholdPercent = percent => _engine!.SetThresholdPercent(percent),
            SetUpdateAutomatically = enable => _engine!.SetUpdateAutomatically(enable),
        };

        // One-click support bundle: a blank panel is otherwise indistinguishable from
        // "Desktop missing", "unexpected file format", or "file unreadable", and asking
        // users to run PowerShell by hand is not a diagnosis path.
        menu.WeeklyResetRequested += (_, _) => EditWeeklyReset();
        // Undoes a self-imposed refresh block, then tries once immediately: someone who just
        // re-enabled this is asking for it now, not at the next background beat (issue #234).
        menu.ResumeUsageRefreshRequested += (_, _) =>
        {
            if (_engine?.ResumeUsageRefresh() == true)
            {
                _engine.RefreshUsageCache(force: true);
            }
        };

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
    /// conversation content, and is redacted before it leaves — the account name is replaced
    /// in every path and org UUIDs are truncated to eight characters, because this goes on
    /// the clipboard on its way to a public issue (see <c>Redact</c>).
    /// </summary>
    private void CopyDiagnostics()
    {
        try
        {
            System.Windows.Clipboard.SetText(BuildDiagnostics());
            _trayHost?.ShowNotification("Diagnostics copied",
                "Paste them into your bug report. No tokens or conversation content are included.",
                NotificationKind.Information);
        }
        catch (Exception)
        {
            // The clipboard can be locked by another process; failing to copy must not crash.
            _trayHost?.ShowNotification("Couldn't copy diagnostics",
                "The clipboard was unavailable. Please try again.",
                NotificationKind.Warning);
        }
    }

    /// <summary>Writes the diagnostics report to a file (the --diagnose hook).</summary>
    private static void WriteDiagnostics(string? path)
    {
        var target = path is { Length: > 0 } p ? p : "oview-diagnostics.txt";
        try
        {
            // deepAudit: this hook is a deliberate command-line run with no UI to freeze and no
            // engine to interrupt, which makes it the one place the transcript reconciliation
            // can afford to run (issue #218).
            File.WriteAllText(target, BuildDiagnostics(deepAudit: true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Diagnostic hook — a failure here must not surface as a crash dialog.
        }
    }

    /// <summary>
    /// The bundle itself is shared with the Linux head (<see cref="DiagnosticsBundle"/>) so
    /// a report reads the same on either platform and cannot drift. What is supplied here
    /// is only what this head knows: its version and how it was installed. Windows has no
    /// desktop-environment or session-type question, and its notification area is part of
    /// the shell and cannot be absent — so those fields are omitted rather than padded with
    /// "n/a".
    /// </summary>
    private static string BuildDiagnostics(bool deepAudit = false)
    {
        var environment = new DiagnosticsEnvironment(
            Version: UpdateService.CurrentVersion,
            InstallKind: UpdateService.CurrentInstallKind.ToString());

        // When there is a running engine, the store is read through the connection that engine
        // is actually using — so the bundle reports what the app believes rather than what a
        // reader opened for the occasion finds. The --diagnose hook runs before the engine
        // exists and takes the other path, and the two label themselves differently on purpose.
        var engine = ((App?)Current)?._engine;
        return engine is null
            ? DiagnosticsBundle.Build(environment, deepAudit)
            : DiagnosticsBundle.Build(environment, engine.InspectStore());
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
        if (_engine!.Settings.UpdateAutomatically
            && UpdatePolicy.MayDownloadAndRun(UpdateService.CurrentInstallKind))
        {
            await InstallAutomaticallyAsync(update);
            return;
        }

        _trayHost.ShowNotification("O-view update available",
            $"Version {update.Version} is available (you have {UpdateService.CurrentVersion}). " +
            "Right-click the icon → Check for updates to install.",
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
    private async Task InstallAutomaticallyAsync(AvailableUpdate update)
    {
        if (_updates is null || _trayHost is null || _updateFlowActive)
        {
            return;
        }

        _trayHost.ShowNotification("Installing O-view " + update.Version,
            $"You have {UpdateService.CurrentVersion}. O-view will close briefly and reopen. "
            + "Turn this off with Update automatically in the tray menu.",
            NotificationKind.Information);

        _updateFlowActive = true;
        try
        {
            var installer = await _updates.DownloadInstallerAsync(update);
            _updates.LaunchInstaller(installer);
            Shutdown();  // release the exe lock so the installer can replace it and relaunch
        }
        catch (UpdateVerificationException)
        {
            _updateFlowActive = false;
            // The one case that earns Error. A checksum mismatch means the bytes that arrived
            // were not the bytes the release published, and O-view refused to run them.
            _trayHost.ShowNotification("Update not installed",
                "The download didn't match the checksum published with this release, so O-view "
                + "didn't run it. Your current version is untouched.",
                NotificationKind.Error);
        }
        catch (Exception)
        {
            // No releases page here. The confirmed flow opens it because a human is already
            // in the loop and can decide; nobody is watching this one, so a browser window
            // appearing unbidden is not a useful answer. It retries on the next daily check.
            _updateFlowActive = false;
            _trayHost.ShowNotification("Update didn't install",
                $"O-view couldn't download version {update.Version}. It will try again later, "
                + "or use Check for updates to install it now.",
                NotificationKind.Warning);
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
                    $"You have the latest version ({UpdateService.CurrentVersion}).",
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
                _trayHost!.ShowNotification("Update check rate limited",
                    PanelText.RateLimitedNotice(result.RetryAfterUtc, TimeZoneInfo.Local),
                    NotificationKind.Warning);
                break;

            default:
                // Warning, not Error: GitHub being briefly unreachable is a retryable
                // condition on the user's own network, and nothing on the machine is wrong.
                _trayHost!.ShowNotification("Couldn't check for updates",
                    "O-view couldn't reach GitHub to check for a newer version. Please try again later.",
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
    private async Task OfferUpdateAsync(AvailableUpdate update)
    {
        if (_updates is null || _trayHost is null)
        {
            return;
        }

        var action = UpdatePolicy.ActionFor(UpdateService.CurrentInstallKind);

        if (action is not UpdateAction.InstallInPlace)
        {
            // A running single-file exe cannot overwrite itself, and the installer would
            // create a parallel install rather than update the loose one. On Linux this
            // branch also covers an apt build, which must never overwrite dpkg's files —
            // but that head has its own copy and never reaches this method.
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
        catch (UpdateVerificationException)
        {
            // Deliberately does NOT open the releases page. Every other failure here means
            // "try again by hand", but this one means the file that arrived was not the file
            // the release published — sending the user to download it manually would hand
            // them the thing the check just rejected. Say what was observed and stop
            // (CLAUDE.md rule 6); the installer has already been deleted, and nothing on the
            // machine has changed.
            _updateFlowActive = false;
            // The one case that earns Error. A checksum mismatch means the bytes that arrived
            // were not the bytes the release published, and O-view refused to run them.
            _trayHost.ShowNotification("Update not installed",
                "The download didn't match the checksum published with this release, so O-view "
                + "didn't run it. Your current version is untouched.",
                NotificationKind.Error);
        }
        catch (Exception)
        {
            _updateFlowActive = false;
            // Warning rather than Error: the download failed, but the user is being handed a
            // working way to finish the job on the next line.
            _trayHost.ShowNotification("Update failed",
                "O-view couldn't download the update. Opening the releases page so you can download it manually.",
                NotificationKind.Warning);
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
        _controller?.Dispose();
        _trayHost?.Dispose();
        _engine?.Dispose();   // stops every timer and closes the rollup store
        _instance?.Dispose();
        base.OnExit(e);
    }

}
