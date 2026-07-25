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

namespace OView.Tray;

/// <summary>
/// Tray-resident WPF app: no windows, no StartupUri; the WPF dispatcher pumps the
/// messages NotifyIcon's hidden window needs. Diagnostic args (all optional):
/// --interval-ms N, --log path, --stress N (refresh N times, log GDI delta, keep
/// running), --samples dir (render icon PNGs and exit — legibility verification).
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
    private System.Windows.Controls.ContextMenu? _menu;
    private PlanHistoryProvider? _planHistory;
    private bool _offPlanNotified;
    private string? _simulateDivergence;
    private UpdateService? _updates;
    private DispatcherTimer? _updateTimer;
    private bool _updateFlowActive;
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
        // The store doubles as the weekly-reset log so the 7-day reset accrues across
        // runs (issue #6) — plan-history retention alone is far too short.
        _planHistory = new PlanHistoryProvider(
            orgUuid: ClaudeAccount.TryRead()?.OrganizationUuid,
            weeklyResetLog: _store);
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
    }

    private void ShowPopup()
    {
        if (_controller is null || _store is null)
        {
            return;
        }

        _controller.Refresh();  // fresh data on open; local reads are cheap
        var popup = EnsurePopup();
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
    /// Right-click menu. WPF ContextMenu, not WinForms ContextMenuStrip — WinForms
    /// stays confined to NotifyIcon itself (CLAUDE.md rule 5).
    /// </summary>
    private void ShowMenu()
    {
        if (_menu is null)
        {
            var startup = new System.Windows.Controls.MenuItem
            {
                Header = "Run at startup",
                IsCheckable = true,
                IsChecked = StartupRegistration.IsEnabled(),
            };
            startup.Click += (_, _) =>
            {
                var ok = startup.IsChecked ? StartupRegistration.Enable() : StartupRegistration.Disable();
                startup.IsChecked = ok ? startup.IsChecked : StartupRegistration.IsEnabled();
            };

            var notify = new System.Windows.Controls.MenuItem
            {
                Header = $"Notify at {_settings.ThresholdPercent}% session usage",
                IsCheckable = true,
                IsChecked = _settings.NotifyOnThreshold,
            };
            notify.Click += (_, _) =>
            {
                _settings = _settings with { NotifyOnThreshold = notify.IsChecked };
                _settings.Save();
            };

            // One-click support bundle: a blank panel is otherwise indistinguishable from
            // "Desktop missing", "unexpected file format", or "file unreadable", and asking
            // users to run PowerShell by hand is not a diagnosis path.
            var copyDiagnostics = new System.Windows.Controls.MenuItem { Header = "Copy diagnostics" };
            copyDiagnostics.Click += (_, _) => CopyDiagnostics();

            // "Check for updates" sits directly above Exit, as requested in issue #18.
            var checkUpdates = new System.Windows.Controls.MenuItem { Header = "Check for updates…" };
            checkUpdates.Click += async (_, _) => await CheckForUpdatesInteractiveAsync();

            var exit = new System.Windows.Controls.MenuItem { Header = "Exit O-view" };
            exit.Click += (_, _) => Shutdown();

            _menu = new System.Windows.Controls.ContextMenu { StaysOpen = false };
            _menu.Items.Add(startup);
            _menu.Items.Add(notify);
            _menu.Items.Add(new System.Windows.Controls.Separator());
            _menu.Items.Add(copyDiagnostics);
            _menu.Items.Add(checkUpdates);
            _menu.Items.Add(exit);

            // A tray app has no activated window, so a StaysOpen=false menu never gets
            // the deactivation that dismisses it on an outside click — it lingers until
            // an item is chosen (issue #11). Foreground the popup's own window once it
            // is up, so clicking off the menu deactivates and closes it.
            _menu.Opened += (_, _) =>
            {
                if (PresentationSource.FromVisual(_menu) is System.Windows.Interop.HwndSource source)
                {
                    NativeMethods.SetForegroundWindow(source.Handle);
                }
            };
        }

        // Re-read on every open: the value can change externally (another instance,
        // manual registry edit, Task Manager's startup page).
        ((System.Windows.Controls.MenuItem)_menu.Items[0]).IsChecked = StartupRegistration.IsEnabled();

        _menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        _menu.IsOpen = true;
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

        // The token tiles come from the JSONL transcripts, a different source entirely —
        // if those are blank too, the cause is broader than plan-history.
        var projects = ClaudeProjectsLocator.DefaultRoot;
        var transcripts = 0;
        try
        {
            transcripts = Directory.Exists(projects)
                ? Directory.GetFiles(projects, "*.jsonl", SearchOption.AllDirectories).Length
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Count is diagnostic only.
        }
        text.AppendLine($"  transcripts   : {transcripts} .jsonl under {projects}");
        return text.ToString();
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
        if (_updates is null || _trayHost is null || _updateFlowActive)
        {
            return;
        }

        var result = await _updates.CheckAsync();
        switch (result.Outcome)
        {
            case UpdateOutcome.UpToDate:
                _trayHost.ShowNotification("O-view is up to date",
                    $"You have the latest version ({UpdateService.CurrentVersion}).");
                break;

            case UpdateOutcome.UpdateAvailable when result.Available is { } update:
                await OfferUpdateAsync(update);
                break;

            default:
                _trayHost.ShowNotification("Couldn't check for updates",
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
            if (ConfirmUpdate(update, "Open the download page for the new version?"))
            {
                _updates.OpenInBrowser(UpdateService.ReleasePageUrl(update));
            }
            return;
        }

        if (!ConfirmUpdate(update,
            "Download and install it now? O-view will close briefly and reopen automatically."))
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

    private static bool ConfirmUpdate(AvailableUpdate update, string question) =>
        System.Windows.MessageBox.Show(
            $"O-view {update.Version} is available (you have {UpdateService.CurrentVersion}).\n\n{question}",
            "O-view update",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Information) == System.Windows.MessageBoxResult.Yes;

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
