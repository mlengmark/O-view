using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using OView.App;
using OView.App.Diagnostics;
using OView.App.Platform;
using OView.Core.Models;
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
        var args = ParseArgs(e.Args);

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

        _instance = new MutexSingleInstanceGuard();
        if (!_instance.TryAcquire())
        {
            Shutdown();
            return;
        }

        var log = args.TryGetValue("--log", out var logPath) ? new FileLog(logPath!) : null;
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

        _engine.Start(new DispatcherTimerFactory());

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
        // Both inspected on open (not cached) so the banners reflect the machine as it is
        // now. The scope report is what the token-scope note is built from — resolved
        // roots and real file counts, never a hard-coded path (issue #58).
        popup.DataReport = PlanHistoryDiagnostics.Inspect();
        popup.ScopeReport = TranscriptScopeReport.Inspect();
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
        _menu.ShowDocked(
            runAtStartup: _startup.IsEnabled(),
            notifyOnThreshold: _engine!.Settings.NotifyOnThreshold,
            thresholdPercent: _engine.Settings.ThresholdPercent,
            version: UpdateService.CurrentVersion);
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

        // The token tiles come from local session transcripts, a different source entirely
        // — and from BOTH places Claude writes them, Claude Code and Cowork. This block
        // used to count only the Claude Code root, so a report from a Cowork user showed
        // "0 .jsonl" beside working token tiles. It is now the same TranscriptScopeReport
        // the panel's scope note is built from, so the bundle and the banner can never
        // describe different scans (issue #58).
        text.Append(TranscriptScopeReport.Inspect().ToClipboardText());
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
        _controller?.Dispose();
        _trayHost?.Dispose();
        _engine?.Dispose();   // stops every timer and closes the rollup store
        _instance?.Dispose();
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

}
