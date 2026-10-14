using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using OView.App;
using OView.Core.Models;
using OView.App.Platform;
using OView.Core.Providers.CachedUsage;
using OView.Core.Providers.Jsonl;
using OView.Core.Providers.PlanHistory;
using OView.App.Pricing;
using OView.App.Updates;
using OView.Linux.Notifications;
using OView.Linux.Updates;
using OView.Linux.Panel;
using OView.Linux.Platform;
using OView.Linux.Rendering;
using OView.Linux.Tray;
using OView.Presentation.Text;

namespace OView.Linux;

/// <summary>
/// The Linux face: a StatusNotifierItem carrying the live-rendered gauge, its tooltip, a
/// menu, and freedesktop notifications. Everything about what the numbers <i>mean</i> lives
/// behind the seam and is shared with the Windows head (ADR-0012): this head asks through
/// <see cref="ISkinToShell"/> and is told through <see cref="IShellToSkin"/>, which it
/// implements, so it names nothing the shell is made of.
/// </summary>
public sealed class LinuxApp : Application, IShellToSkin
{
    private readonly FreedesktopNotifier _notifier = new();
    private readonly IThemeSource _theme = new PortalThemeSource();
    private readonly IStartupRegistration _startup = new XdgAutostartRegistration();

    /// <summary>
    /// This head's one hop onto the UI thread. <see cref="Program"/> hands the same object to
    /// the shell, so a push and a bus callback arrive by exactly the same route — and there is
    /// one place to look when either turns out not to.
    /// </summary>
    private readonly IUiDispatcher _dispatcher = new AvaloniaUiDispatcher();

    /// <summary>The shell's lifetime: this head starts it, <see cref="Program"/> disposes it.</summary>
    private ShellHost? _host;

    /// <summary>What this head asks of the shell — the same object for the shell's whole life.</summary>
    private ISkinToShell? _shell;

    private TrayHostState _hostState;
    private IAppLog? _log;

    /// <summary>
    /// The two things the shell only tells this head it is <i>time</i> for. Both are built in
    /// <see cref="StartTray"/>, which finishes by starting the polling that is the only source
    /// of those pushes, so neither can be asked for before it exists.
    /// </summary>
    private LinuxUpdateNotice? _updateNotice;
    private RateCardFeed? _rates;

    private NativeMenuItem? _startupItem;

    /// <summary>The resume row (issue #234), hidden until the refresh blocks itself.</summary>
    private NativeMenuItem? _resumeItem;

    /// <summary>Why refreshing is blocked, or null. Read on every render to keep the row honest.</summary>
    private Func<string?>? _usageRefreshBlocked;
    private TrayIcon? _tray;
    private PanelWindow? _panel;
    private CancellationTokenSource? _hostWatch;

    /// <summary>Set by <c>--panel-samples</c>: render the panel offscreen and exit.</summary>
    internal static string? SampleDirectory { get; set; }

    /// <summary>
    /// The hop the shell is given, so both halves of this head use one object. Named around
    /// Avalonia's own <see cref="Avalonia.Threading.Dispatcher"/>, which this file also uses.
    /// </summary>
    internal IUiDispatcher UiDispatcher => _dispatcher;

    /// <summary>
    /// Handed the shell once it exists — Avalonia constructs this type itself, so this cannot
    /// be a constructor argument.
    ///
    /// <para>Polling is deliberately <i>not</i> started here: <see cref="StartTray"/> does
    /// that, once the icon and the menu are up to receive the first snapshot. Disposing is
    /// <see cref="Program"/>'s, whose exit paths are the only ones that cover every way this
    /// process can end.</para>
    /// </summary>
    internal void Attach(ShellHost host, TrayHostState hostState, IAppLog? log)
    {
        _host = host;
        _shell = host.Shell;
        _hostState = hostState;
        _log = log;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (SampleDirectory is { Length: > 0 } directory)
        {
            // Offscreen render and exit — no tray, no bus, no shell. The panel is
            // otherwise only inspectable on a desktop none of its authors has.
            PanelSampleRenderer.RenderAll(directory);

            // Posted rather than called inline: shutting down from inside framework
            // initialisation tears the lifetime out from under itself.
            Dispatcher.UIThread.Post(() =>
                (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown());
            return;
        }

        if (_host is not null)
        {
            StartTray(_host);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void StartTray(ShellHost host)
    {
        // The mode the lifetime actually has, not the one Program asked for — the two are
        // different facts, and the wrong one here used to end the app the second time anyone
        // opened the panel without writing a word about it (issue #312). A line naming it is the
        // only way a log that stops mid-session can be told from one that was shut down.
        _log?.Write(
            "shutdown mode="
            + ((ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.ShutdownMode.ToString()
               ?? "(no desktop lifetime)"));

        _tray = new TrayIcon
        {
            Icon = ToWindowIcon(UsageSnapshot.None),
            ToolTipText = "O-view",
            IsVisible = true,
            Menu = BuildMenu(host.Shell),
        };
        _tray.Clicked += TrayClickHandler(host.Shell);

        // What actually publishes the icon, and the line whose absence meant this head never
        // reached a notification area on any desktop. A constructed TrayIcon has no platform
        // implementation behind it: the toolkit builds one only for an icon in the
        // application's own collection, and until then every property set above is dropped by
        // a null-conditional call that reports nothing and returns nothing to check. So the
        // gauge was drawn every poll, the icon reported itself visible, and the session bus
        // never heard a word — which is also why the toolkit's log has nothing to say about
        // it (see Program.AvaloniaLog): the backend that would have complained was never
        // constructed.
        TrayIcon.SetIcons(this, new TrayIcons { _tray });

        // Says a newer version exists; never downloads or runs anything (ADR-0009 as
        // amended). Without this the shell says it is time to check and nothing listens, which
        // is how a Linux install ended up with no update path at all: the app said nothing,
        // and apt cannot know about a release it has no repository for.
        _updateNotice = new LinuxUpdateNotice(
            Program.DetectInstallKind(),
            ReleaseFeed.VersionOf(typeof(LinuxApp).Assembly),
            (title, body) => _notifier.ShowAsync(title, body),
            _log);

        // The rate-card drift check (ADR-0016). One line in the log, no UI, no rate ever
        // installed — see the Windows head, which wires it identically because there is
        // nothing platform-specific about asking a public page what it says.
        _rates = new RateCardFeed(_log);

        // Last, so the four pushes below cannot arrive before what answers them exists. Ticks
        // arrive on the UI thread; the reading happens off it and comes back through the
        // dispatcher this head handed the shell. The pair is what keeps the icon and the menu
        // responsive while a first run ingests a large transcript history (issue #125).
        host.Start(new AvaloniaTimerFactory());

        // The icon reports IsVisible = true whether or not anything can display it, so a
        // missing host has to be discovered from the bus and said out loud. Notifications
        // are a different service from the tray, so this reaches the user even on the one
        // configuration where the icon cannot (ADR-0013 decision 2).
        if (_hostState is not TrayHostState.Present)
        {
            _log?.Write($"no tray host: {_hostState}");
            _ = _notifier.ShowAsync(NoticeText.NoTrayHostTitle, SniHostProbe.Explain(_hostState));
            WatchForHostArriving();
        }
    }

    // ── what the shell pushes at this skin ───────────────────────────────────────────
    //
    // All four arrive on the UI thread without a Post of their own: the shell puts every push
    // through the dispatcher it was handed in Program, including the ones that were already on
    // this thread, so there is no per-callback rule for this head to get wrong (issue #125).

    /// <summary>A new snapshot to draw, in the icon and its tooltip.</summary>
    public void ShowSnapshot(UsageSnapshot snapshot) => Render(snapshot);

    /// <summary>
    /// A freedesktop notification.
    ///
    /// <para><see cref="AppNotification.Kind"/> is deliberately dropped here rather than
    /// mapped. A freedesktop notification already shows O-view's own app icon, so there is no
    /// severity glyph to pick; the only severity channel the spec offers is the urgency hint,
    /// and marking anything critical makes it persist on screen until dismissed — wrong for all
    /// three kinds, and already decided against in <see cref="FreedesktopNotifier"/>. The
    /// Windows head needs the kind because its balloon draws a glyph whether you choose one or
    /// not.</para>
    /// </summary>
    public void ShowNotification(AppNotification notification) =>
        _ = _notifier.ShowAsync(notification.Title, notification.Message);

    /// <summary>Time to look for a newer release. The HTTP and the notice stay on this side.</summary>
    public void UpdateCheckDue() => _ = _updateNotice?.CheckAsync();

    /// <summary>Time to compare the bundled rate card against the published one.</summary>
    public void RateCheckDue() => _ = _rates?.CheckAsync(ModelCatalog.Bundled);

    /// <summary>
    /// The tray icon's click handler, marshalled onto the UI thread (issue #143).
    ///
    /// <para>Its own method so the wiring is assertable without a session bus: the returned
    /// delegate's <c>Method.DeclaringType</c> is <see cref="BusCallback"/>, and a test can
    /// check that on the real delegate rather than on a re-implementation. It proves the
    /// marshalling is still in place; it cannot prove <see cref="StartTray"/> still uses this
    /// method, and nothing available on a machine with no Linux desktop can.</para>
    /// </summary>
    internal EventHandler TrayClickHandler(ISkinToShell shell) =>
        BusCallback.For(_dispatcher, () => ShowPanel(shell), _log);

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
    private void ShowPanel(ISkinToShell shell)
    {
        try
        {
            // Opens on the last polled snapshot — at most one poll old, and exactly what
            // the icon beside it is already showing. It used to force a synchronous
            // Refresh() here, on the assumption that "local reads are cheap"; they are not
            // when the machine holds hundreds of MB of transcripts, and that read ran on
            // the UI thread with the user's click waiting behind it (issue #125).
            RefreshStartupItem();   // the tick, which anything on the machine may have changed

            // Opening the panel is the moment freshness is observable, so it drives the
            // usage-cache refresh rather than a timer (issue #234). Forced, because a person
            // looking is not a poll and must not be held to the background floor.
            //
            // This is safe where the synchronous Refresh() above was not: the shell spawns
            // Claude Code off the UI thread and publishes back when it lands, so nothing waits
            // behind the user's click. The panel still opens on the last polled snapshot; the
            // refresh only affects what the next one carries.
            shell.RefreshUsageCache(force: true);

            _panel?.Close();
            _panel = new PanelWindow(new LinuxPanelTheme(_theme.IsPanelLight()), _log);

            // Both reports are inspected on open, not cached, so the banners describe the
            // machine as it is now rather than as it was at startup.
            _panel.ShowWith(
                shell.Latest,
                shell.BuildStatistics(),
                ClaudeAccount.TryRead(),
                PlanHistoryDiagnostics.Inspect(),
                TranscriptScopeReport.Inspect(),
                DateTimeOffset.UtcNow,
                // Read on open like the two reports above. The flag cache refreshes when
                // Claude Code starts, not when /usage runs, so it keeps its own clock and is
                // read on its own rather than folded into the poll (issue #254).
                BoostNotices.TryRead());

            // Which thread got here, not only that the panel came up. Taking BusCallback's
            // hand-off away leaves the window built on the D-Bus thread, and under a virtual
            // display that still opens — so "panel opened" alone cannot tell the #143 defect
            // from a healthy click, and the runtime click test has nothing to fail on.
            _log?.Write($"panel opened uiThread={(Dispatcher.UIThread.CheckAccess() ? "true" : "false")}");
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
                if (_tray is not null && _shell is not null)
                {
                    _tray.IsVisible = false;
                    _tray.IsVisible = true;
                    Render(_shell.Latest);
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

            // A native menu is drawn by the host and offers no opening hook, so the resume row's
            // visibility rides the render instead — which happens every poll, more often than a
            // user could open the menu.
            SyncResumeItem();

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

    private NativeMenu BuildMenu(ISkinToShell shell)
    {
        var startup = new NativeMenuItem(MenuText.RunAtStartupRow)
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

        var exit = new NativeMenuItem(MenuText.ExitRow);

        // The third. Shutdown() tears down the dispatcher, so calling it from the bus thread
        // races the UI thread it is dismantling.
        exit.Click += BusCallback.For(_dispatcher, () =>
        {
            _hostWatch?.Cancel();
            (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        }, _log);

        // Only present once the refresh has stopped itself (issue #234). The guard behind that
        // block errs toward reporting a charge — a Claude Code session started while a refresh
        // ran looks the same as a billed one — and that direction is only correct while the user
        // can reverse it, so this row is what makes the trade sound rather than a nicety.
        var resume = new NativeMenuItem(PanelText.UsageRefreshBlockedRow) { IsVisible = false };

        // Marshalled like every other menu click here: a DBusMenu event arrives on the bus
        // thread, and touching an AvaloniaObject from there is the SIGSEGV of issue #143, not an
        // exception any catch could reach.
        resume.Click += BusCallback.For(_dispatcher, () =>
        {
            if (shell.ResumeUsageRefresh())
            {
                // Immediately, not at the next background beat: someone who has just re-enabled
                // this is asking for it now.
                shell.RefreshUsageCache(force: true);
                _log?.Write("usage refresh resumed from menu");
            }

            SyncResumeItem();
        }, _log);

        _resumeItem = resume;

        // A closure, deliberately not invoked here. Building the menu must not dereference the
        // shell — LinuxHeadWiringTests constructs it with a null one to inspect the click
        // handlers without opening a store or touching this machine's paths. The row starts
        // hidden, which is the correct initial state anyway, and Render corrects it every poll.
        _usageRefreshBlocked = () => shell.UsageRefreshBlocked;

        _startupItem = startup;
        return [startup, resume, exit];
    }

    /// <summary>
    /// Shows the resume row only while there is something to undo.
    ///
    /// <para>Driven from <see cref="Render"/> rather than from the menu opening, because a native
    /// menu is drawn by the host and gives no hook for that. Render already runs on the UI thread
    /// every poll, which is more often than a user could open the menu.</para>
    /// </summary>
    private void SyncResumeItem()
    {
        if (_resumeItem is not null && _usageRefreshBlocked is not null)
        {
            _resumeItem.IsVisible = _usageRefreshBlocked() is not null;
        }
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
