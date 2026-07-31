using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using OView.App;
using OView.Core.Models;
using OView.Linux.Notifications;
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
    private TrayIcon? _tray;
    private CancellationTokenSource? _hostWatch;

    /// <summary>Supplied before the framework starts; Avalonia constructs this type itself.</summary>
    internal static void Configure(UsageEngine engine, TrayHostState hostState, IAppLog? log)
    {
        _engine = engine;
        _hostState = hostState;
        _log = log;
    }

    public override void OnFrameworkInitializationCompleted()
    {
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
        _tray.Clicked += (_, _) => _log?.Write("tray clicked");

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
