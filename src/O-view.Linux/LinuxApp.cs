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
using OView.App.Updates;
using OView.Linux.Notifications;
using OView.Linux.Updates;
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
    private readonly IStartupRegistration _startup = new XdgAutostartRegistration();
    private readonly IUiDispatcher _dispatcher = new AvaloniaUiDispatcher();
    private NativeMenuItem? _startupItem;
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
        _tray.Clicked += TrayClickHandler(engine);

        // No Post needed: the engine publishes through the dispatcher handed to Start below,
        // so this already arrives on the UI thread.
        engine.SnapshotUpdated += Render;
        engine.NotificationRequested += n => _ = _notifier.ShowAsync(n.Title, n.Message);

        // Says a newer version exists; never downloads or runs anything (ADR-0009 as
        // amended). Without this the engine raises UpdateCheckDue and nothing listens, which
        // is how a Linux install ended up with no update path at all: the app said nothing,
        // and apt cannot know about a release it has no repository for.
        var notice = new LinuxUpdateNotice(
            Program.DetectInstallKind(),
            ReleaseFeed.VersionOf(typeof(LinuxApp).Assembly),
            (title, body) => _notifier.ShowAsync(title, body),
            _log);
        engine.UpdateCheckDue += () => _ = notice.CheckAsync();

        // Ticks arrive on the UI thread; the reading happens off it and comes back through
        // the dispatcher. The pair is what keeps the icon and the menu responsive while a
        // first run ingests a large transcript history (issue #125).
        engine.Start(new AvaloniaTimerFactory(), _dispatcher);

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
    /// The tray icon's click handler, marshalled onto the UI thread (issue #143).
    ///
    /// <para>Its own method so the wiring is assertable without a session bus: the returned
    /// delegate's <c>Method.DeclaringType</c> is <see cref="BusCallback"/>, and a test can
    /// check that on the real delegate rather than on a re-implementation. It proves the
    /// marshalling is still in place; it cannot prove <see cref="StartTray"/> still uses this
    /// method, and nothing available on a machine with no Linux desktop can.</para>
    /// </summary>
    internal EventHandler TrayClickHandler(UsageEngine engine) =>
        BusCallback.For(_dispatcher, () => ShowPanel(engine), _log);

    /// <summary>
    /// Opens the detail panel, rebuilt for the current theme so a desktop switched between
    /// light and dark never needs a restart — the same rule the WPF panel follows.
    ///
    /// <para><b>UI thread only.</b> Reached through <see cref="BusCallback"/>, because the
    /// tray click that calls it arrives on the D-Bus thread and everything below here builds
    /// and shows an Avalonia window. Called directly from a bus callback, this segfaulted
    /// (issue #143) — and note that the <c>catch</c> below cannot help with that: a SIGSEGV
    /// is not an exception, which is why the report was a core dump and an empty log.</para>
    /// </summary>
    private void ShowPanel(UsageEngine engine)
    {
        try
        {
            // Opens on the last polled snapshot — at most one poll old, and exactly what
            // the icon beside it is already showing. It used to force a synchronous
            // Refresh() here, on the assumption that "local reads are cheap"; they are not
            // when the machine holds hundreds of MB of transcripts, and that read ran on
            // the UI thread with the user's click waiting behind it (issue #125).
            RefreshStartupItem();   // the tick, which anything on the machine may have changed

            _panel?.Close();
            _panel = new PanelWindow(new LinuxPanelTheme(_theme.IsPanelLight()), _log);

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
        var startup = new NativeMenuItem("Run at startup")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _startup.IsEnabled(),
        };

        // The tick shows the state as it ACTUALLY stands after the write, never the state
        // that was requested. Writing the autostart file can fail — a read-only home, a full
        // disk — and a tick claiming otherwise would be a fabricated fact about the user's
        // machine (rule 6). IStartupRegistration.Apply is shared with the Windows head
        // precisely so the two cannot get this subtly different.
        //
        // Marshalled for the same reason the tray click is (issue #143): a DBusMenu event
        // arrives on the bus thread, and this both touches the disk and sets IsChecked on an
        // AvaloniaObject. It was the second of the three faults that one root cause produced.
        startup.Click += BusCallback.For(_dispatcher, () =>
        {
            var actual = _startup.Apply(!startup.IsChecked);
            startup.IsChecked = actual;
            _log?.Write($"run at startup -> {actual} ({XdgAutostartRegistration.DefaultDirectory})");
        }, _log);

        var exit = new NativeMenuItem("Exit O-view");

        // The third. Shutdown() tears down the dispatcher, so calling it from the bus thread
        // races the UI thread it is dismantling.
        exit.Click += BusCallback.For(_dispatcher, () =>
        {
            _hostWatch?.Cancel();
            (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        }, _log);

        _startupItem = startup;
        return [startup, exit];
    }

    /// <summary>
    /// Re-reads the autostart state from disk. The autostart file is the single source of
    /// truth and anything may have changed it — the desktop's own startup-applications
    /// settings, or <c>o-view --startup-off</c> in a terminal — so a menu built once at
    /// launch would otherwise show a tick that quietly went stale.
    /// </summary>
    private void RefreshStartupItem()
    {
        if (_startupItem is not null)
        {
            _startupItem.IsChecked = _startup.IsEnabled();
        }
    }
}
