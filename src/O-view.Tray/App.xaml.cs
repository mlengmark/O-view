using System.Globalization;
using System.IO;
using System.Windows;
using OView.Core.Models;
using OView.Core.Providers;
using OView.Core.Providers.Jsonl;
using OView.Core.Providers.PlanHistory;
using OView.Core.Storage;
using OView.Tray.Diagnostics;
using OView.Tray.Popup;
using OView.Tray.Tray;

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
        var provider = new CompositeUsageProvider(
            new PlanHistoryProvider(orgUuid: ClaudeAccount.TryRead()?.OrganizationUuid),
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
        };

        log?.Write($"startup interval={interval.TotalSeconds}s");
        _controller.Start();

        if (args.ContainsKey("--test-notify"))
        {
            _trayHost.ShowNotification("Claude usage", "Test notification (--test-notify).");
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
        EnsurePopup().ShowNearTrayIcon(
            _controller.Latest,
            PanelStatistics.Build(_store, DateTimeOffset.UtcNow),
            ClaudeAccount.TryRead());
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

            var exit = new System.Windows.Controls.MenuItem { Header = "Exit O-view" };
            exit.Click += (_, _) => Shutdown();

            _menu = new System.Windows.Controls.ContextMenu { StaysOpen = false };
            _menu.Items.Add(startup);
            _menu.Items.Add(notify);
            _menu.Items.Add(new System.Windows.Controls.Separator());
            _menu.Items.Add(exit);
        }

        // Re-read on every open: the value can change externally (another instance,
        // manual registry edit, Task Manager's startup page).
        ((System.Windows.Controls.MenuItem)_menu.Items[0]).IsChecked = StartupRegistration.IsEnabled();

        _menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        _menu.IsOpen = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
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
