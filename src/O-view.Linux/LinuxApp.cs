using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using OView.App;
using OView.Core.Models;
using OView.App.Platform;
using OView.Core.Providers.Jsonl;
using OView.Core.Providers.PlanHistory;
using OView.Linux.Notifications;
using OView.Linux.Panel;
using OView.Linux.Platform;
using OView.Linux.Rendering;
using OView.Linux.Tray;

namespace OView.Linux;

/// <summary>
/// The Linux face: a StatusNotifierItem carrying the live-rendered gauge, its tooltip, a
/// menu, and freedesktop notifications. Everything about what the numbers <i>mean</i> lives
/// in <see cref="UsageEngine"/> and is shared with the Windows head (ADR-0012).
/// </summary>
public sealed class LinuxApp : Application
{
    private static UsageEngine? _engine;
    private static TrayHostState _hostState;
    private static IAppLog? _log;

    private readonly FreedesktopNotifier _notifier = new();
    private readonly IThemeSource _theme = new PortalThemeSource();
    private TrayIcon? _tray;
    private PanelWindow? _panel;
    private CancellationTokenSource? _hostWatch;

    /// <summary>Set by <c>--panel-samples</c>: render the panel offscreen and exit.</summary>
    internal static string? SampleDirectory { get; set; }

    /// <summary>Supplied before the framework starts; Avalonia constructs this type itself.</summary>
    internal static void Configure(UsageEngine engine, TrayHostState hostState, IAppLog? log)
    {
        _engine = engine;
        _hostState = hostState;
        _log = log;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (SampleDirectory is { Length: > 0 } directory)
        {
            // Offscreen render and exit — no tray, no bus, no engine. The panel is
            // otherwise only inspectable on a desktop none of its authors has.
            PanelSampleRenderer.RenderAll(directory);

            // Posted rather than called inline: shutting down from inside framework
            // initialisation tears the lifetime out from under itself.
            Dispatcher.UIThread.Post(() =>
                (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown());
            return;
        }

        if (_engine is not null)
        {
            StartTray(_engine);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void StartTray(UsageEngine engine)
    {
        _tray = new TrayIcon
        {
            Icon = ToWindowIcon(UsageSnapshot.None),
            ToolTipText = "O-view",
            IsVisible = true,
            Menu = BuildMenu(),
        };
        _tray.Clicked += (_, _) => ShowPanel(engine);

        engine.SnapshotUpdated += snapshot => Dispatcher.UIThread.Post(() => Render(snapshot));
        engine.NotificationRequested += n => _ = _notifier.ShowAsync(n.Title, n.Message);

        engine.Start(new AvaloniaTimerFactory());

        // The icon reports IsVisible = true whether or not anything can display it, so a
        // missing host has to be discovered from the bus and said out loud. Notifications
        // are a different service from the tray, so this reaches the user even on the one
        // configuration where the icon cannot (ADR-0013 decision 2).
        if (_hostState is not TrayHostState.Present)
        {
            _log?.Write($"no tray host: {_hostState}");
            _ = _notifier.ShowAsync("O-view is running, but has nowhere to show its icon",
                SniHostProbe.Explain(_hostState));
            WatchForHostArriving();
        }
    }

    /// <summary>
    /// Opens the detail panel, rebuilt for the current theme so a desktop switched between
    /// light and dark never needs a restart — the same rule the WPF panel follows.
    /// </summary>
    private void ShowPanel(UsageEngine engine)
    {
        try
        {
            engine.Refresh();   // fresh figures on open; local reads are cheap

            _panel?.Close();
            _panel = new PanelWindow(new LinuxPanelTheme(_theme.IsPanelLight()));

            // Both reports are inspected on open, not cached, so the banners describe the
            // machine as it is now rather than as it was at startup.
            _panel.ShowWith(
                engine.Latest,
                engine.BuildStatistics(),
                ClaudeAccount.TryRead(),
                PlanHistoryDiagnostics.Inspect(),
                TranscriptScopeReport.Inspect(),
                DateTimeOffset.UtcNow);

            _log?.Write("panel opened");
        }
        catch (Exception ex)
        {
            // A panel that fails to open must not take the tray icon down with it.
            _log?.Write($"panel FAILED {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// A user can install an AppIndicator extension while O-view is running. Re-showing the
    /// icon when a host appears is the Linux <c>TaskbarCreated</c> (ADR-0003 item 5) — and
    /// without it they would have to know to restart, which nothing tells them.
    /// </summary>
    private void WatchForHostArriving()
    {
        _hostWatch = new CancellationTokenSource();
        var token = _hostWatch.Token;

        _ = Task.Run(async () =>
        {
            if (!await SniHostProbe.WaitForHostAsync(token) || token.IsCancellationRequested)
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                _log?.Write("tray host appeared — re-showing icon");
                if (_tray is not null && _engine is not null)
                {
                    _tray.IsVisible = false;
                    _tray.IsVisible = true;
                    Render(_engine.Latest);
                }
            });
        }, token);
    }

    private void Render(UsageSnapshot snapshot)
    {
        try
        {
            if (_tray is null)
            {
                return;
            }

            _tray.Icon = ToWindowIcon(snapshot);
            _tray.ToolTipText = TooltipFormatter.Format(snapshot);
            _log?.Write($"icon rendered session={snapshot.SessionPercent?.ToString() ?? "null"}");
        }
        catch (Exception ex)
        {
            // Keep the previous icon; a monitoring tool must not die on a bad render.
            _log?.Write($"render FAILED {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// SNI wants pixels, not a themed icon name — the gauge changes every poll, so there is
    /// no theme entry it could refer to.
    /// </summary>
    private static WindowIcon ToWindowIcon(UsageSnapshot snapshot)
    {
        // 24 px is the size Linux panels most commonly request; hosts scale from what they
        // are given. HiDPI sizing is one of the things still to confirm on real hardware.
        using var stream = new MemoryStream(SkiaIconRenderer.RenderPng(24, snapshot, lightPanel: false));
        return new WindowIcon(stream);
    }

    private NativeMenu BuildMenu()
    {
        var exit = new NativeMenuItem("Exit O-view");
        exit.Click += (_, _) =>
        {
            _hostWatch?.Cancel();
            (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        };

        return [exit];
    }
}
