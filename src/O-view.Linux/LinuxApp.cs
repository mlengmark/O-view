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
    /// Where a dragged panel's place is kept. One per app rather than one per panel, because
    /// the panel is rebuilt on every open and the file it reads is a fact about the machine
    /// either way.
    ///
    /// <para>Constructed whatever the session says; whether the panel is <i>given</i> it is
    /// <see cref="_placement"/>'s decision, so nothing is read or written on a session that
    /// could not read a place back.</para>
    /// </summary>
    private readonly LinuxPanelPositionStore _panelPositions = new();

    /// <summary>
    /// Whether this session lets the panel be put back where the user left it. Read once: a
    /// session does not change underneath a running process.
    /// </summary>
    private readonly ClientPlacement _placement = ClientPlacement.Detect();

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

    /// <summary>
    /// Set by the reset row when there is no panel yet to forget a place for — see
    /// <see cref="ResetPanelPosition"/>.
    /// </summary>
    private bool _forgetPanelPlaceOnNextOpen;

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

        // Which of the two Wayland cases this is, and why — the one decision behind both the
        // panel keeping its corner and the reset row being absent, so a report from a session
        // where dragging does nothing says in one line that it was meant to.
        _log?.Write($"remembered panel position: {(_placement.Allowed ? "available" : "unavailable")} "
                    + $"— {_placement.Reason}");

        _tray = new TrayIcon
        {
            Icon = ToWindowIcon(UsageSnapshot.None),
            ToolTipText = "O-view",
            IsVisible = true,
            Menu = BuildMenu(host.Shell, _placement),
        };
        _tray.Clicked += OpenPanelHandler(host.Shell);

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
        //
        // The reading is logged here and the notice is decided later: the probe runs before
        // the toolkit does, which on a stock GNOME login is before the session has registered
        // its own watcher, so what it read at startup is worth a log line and is not yet worth
        // saying to anyone (see TrayHostNotice).
        if (_hostState is not TrayHostState.Present)
        {
            _log?.Write($"no tray host: {_hostState}");
            StartHostNotice(_hostState);
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
    /// The one way into the panel, marshalled onto the UI thread (issue #143). Both the tray
    /// icon's click and the menu's first row are built from this, so there is a single
    /// marshalling, a single panel path and a single log line behind either gesture.
    ///
    /// <para>Its own method so the wiring is assertable without a session bus: the returned
    /// delegate's <c>Method.DeclaringType</c> is <see cref="BusCallback"/>, and a test can
    /// check that on the real delegate rather than on a re-implementation. It proves the
    /// marshalling is still in place; it cannot prove <see cref="StartTray"/> still uses this
    /// method, and nothing available on a machine with no Linux desktop can.</para>
    /// </summary>
    internal EventHandler OpenPanelHandler(ISkinToShell shell) =>
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

            // The store only where a place can be read back again; on a native Wayland session
            // the panel is handed none and behaves exactly as it did before dragging existed
            // (PanelWindow.StoreFor).
            _panel = new PanelWindow(
                new LinuxPanelTheme(_theme.IsPanelLight()),
                _log,
                PanelWindow.StoreFor(_placement, _panelPositions))
            {
                // Carried onto the panel the row could not reach, and spent here: the reset is
                // one request, not a standing setting.
                ForgetPlaceOnOpen = _forgetPanelPlaceOnNextOpen,
            };

            _forgetPanelPlaceOnNextOpen = false;

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
                BoostNotices.TryRead(),
                // Taken from the shell, not read here: the refresh forced above publishes
                // into it, so this is the freshest block either skin has. Without it the
                // off-plan banner can only say the billing setting is unknown.
                shell.CachedUsage);

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
    /// Starts the notice about a missing notification area, and the icon's recovery, which are
    /// one decision rather than two: the notice is only true while no host has appeared, and
    /// the icon only needs re-showing once one has. Two independent watches could disagree,
    /// and a pair that disagreed would leave the notice on screen beside a working icon.
    ///
    /// <para>Off the UI thread, because everything it waits on is a D-Bus round trip and
    /// blocking the dispatcher on one deadlocks the app (issue #124).</para>
    /// </summary>
    private void StartHostNotice(TrayHostState state)
    {
        _hostWatch = new CancellationTokenSource();
        var token = _hostWatch.Token;
        var notice = BuildHostNotice();

        _ = Task.Run(() => notice.RunAsync(state, token), token);
    }

    /// <summary>
    /// The notice as this head wires it, built but not started — its own method so the wiring
    /// is inspectable without a session bus, the way <see cref="OpenPanelHandler"/> is.
    /// </summary>
    internal TrayHostNotice BuildHostNotice() =>
        new(
            (title, body) => _notifier.ShowAsync(title, body),
            id => _notifier.CloseAsync(id),
            ReShowIcon,
            _log);

    /// <summary>
    /// A user can install an AppIndicator extension while O-view is running. Re-showing the
    /// icon when a host appears is the Linux <c>TaskbarCreated</c> (ADR-0003 item 5) — and
    /// without it they would have to know to restart, which nothing tells them.
    ///
    /// <para>Posted, because the host turns up on a bus thread and everything below touches
    /// the toolkit — the SIGSEGV of issue #143, not an exception any catch could reach.</para>
    /// </summary>
    private void ReShowIcon() =>
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

    private void Render(UsageSnapshot snapshot)
    {
        try
        {
            if (_tray is null)
            {
                return;
            }

            _tray.Icon = ToWindowIcon(snapshot);

            // Set every poll, and on this platform it does not become a tooltip. Avalonia's
            // tray backend publishes this text as the item's SNI Title and answers the SNI
            // ToolTip property with a hard-coded empty structure, so there is nothing here to
            // set differently; and the desktop the report came from never asks for ToolTip
            // anyway, having commented it out of its interface (issue #314, proved on the bus
            // by TrayTooltipRuntimeTests). The exact figures reach a GNOME user through the
            // panel instead, which is what the README's support matrix says.
            _tray.ToolTipText = TooltipFormatter.Format(snapshot);

            // A native menu is drawn by the host and offers no opening hook, so the resume row's
            // visibility rides the render instead — which happens every poll, more often than a
            // user could open the menu.
            SyncResumeItem();

            _log?.Write(
                $"icon rendered session={Describe(snapshot.SessionPercentValue)}");
        }
        catch (Exception ex)
        {
            // Keep the previous icon; a monitoring tool must not die on a bad render.
            _log?.Write($"render FAILED {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// The meter as a bug report needs it: the figure and why it is the figure. "unavailable"
    /// and a missing reading used to print the same <c>null</c>, which left the most common
    /// Linux report — the icon showing nothing — unable to say which had happened.
    /// </summary>
    private static string Describe(UsagePercent percent) => percent.Status switch
    {
        UsageValueStatus.Unavailable => "unavailable",
        _ => percent.Value is { } value
            ? $"{value}{(percent.Status == UsageValueStatus.Estimated ? " (estimated)" : "")}"
            : "unknown",
    };

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

    /// <param name="placement">
    /// Whether this session can remember where the panel was put, which decides whether the
    /// reset row is there at all. Taken as an argument rather than read from the field so both
    /// the row's presence and its absence can be asserted on a machine in neither session.
    /// </param>
    private NativeMenu BuildMenu(ISkinToShell shell, ClientPlacement placement)
    {
        // First, because on Ubuntu's AppIndicators tray this menu is everything a single click
        // gets you and the panel had no gesture at all: Activate arrives only on a double click,
        // which nothing on screen suggests (issue #315). A row reaches the panel on every SNI
        // host whatever that host does with clicks.
        //
        // Deliberately the tray click's own handler rather than a second call to ShowPanel: one
        // marshalling, one panel path, one "panel opened" line in the log, so a row that opens
        // the panel differently from a click is not a state this file can get into.
        var showUsage = new NativeMenuItem(MenuText.ShowUsageRow);
        showUsage.Click += OpenPanelHandler(shell);

        // Directly below Show usage, as Maximilian's F52 note of 2026-10-16 asks: someone
        // opening the menu for this has a panel sitting somewhere they cannot get it back from,
        // so it belongs beside the row that opens it rather than at the end of a list.
        //
        // Absent where no place can be remembered, unlike the command rows below it: on a
        // native Wayland session there is nothing to reset, and a row offering it would be
        // O-view claiming a thing it cannot do (rule 6, and ClientPlacement).
        NativeMenuItem? reset = null;
        if (placement.Allowed)
        {
            reset = new NativeMenuItem(MenuText.ResetPanelPositionRow);

            // Marshalled like every other row here: a DBusMenu event arrives on the bus thread,
            // and moving a window from there is the SIGSEGV of issue #143.
            reset.Click += BusCallback.For(_dispatcher, ResetPanelPosition, _log);
        }

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

        return reset is null
            ? [showUsage, startup, resume, exit]
            : [showUsage, reset, startup, resume, exit];
    }

    /// <summary>
    /// The menu's "Reset panel position" row: forget wherever the panel was dragged to, and put
    /// an open one back at the work-area corner.
    ///
    /// <para>Deferred to the next open when no panel exists yet. A place is stored per display,
    /// and which display a panel will open on is not knowable until there is a panel to ask —
    /// this head has no screen geometry of its own, only a window's. The next open knows it, and
    /// is also the first moment a reset would be visible to anyone, so nothing is lost by
    /// waiting for it.</para>
    /// </summary>
    private void ResetPanelPosition()
    {
        if (_panel is { } panel)
        {
            panel.ReturnToCorner();
            return;
        }

        _forgetPanelPlaceOnNextOpen = true;
        _log?.Write("reset panel position — deferred to the next open, the first panel there is");
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

/// <summary>
/// Holds the "nowhere to show its icon" notice back until the desktop has had a moment to
/// finish starting, and withdraws it if a notification area turns up after all.
///
/// <para><b>Why it waits.</b> The probe has to run before the toolkit starts (a D-Bus round
/// trip on the dispatcher deadlocks the app — issue #124), and at a stock GNOME login that is
/// before the session's own watcher has registered. The reading is honest and the notice was
/// true when it was written; then the icon appeared, and every login opened with a sentence
/// the desktop contradicted a moment later. Waiting costs a user who really has no
/// notification area ten seconds, and costs everyone else nothing.</para>
///
/// <para><b>Why it is also what brings the icon back.</b> The wait and the icon's recovery
/// (ADR-0003 item 5) watch for the same thing — the watcher name being owned — so they are one
/// watch. Kept apart they could disagree, and the way they would disagree is a notice left on
/// screen beside an icon that works.</para>
///
/// <para>Everything it waits on is injectable, so the tests for it neither sleep nor need a
/// bus; what is not injectable is <see cref="NoticeText.NoTrayHostTitle"/> and the sentence
/// under it, which stay exactly the ones a desktop with no notification area was always
/// told.</para>
/// </summary>
public sealed class TrayHostNotice
{
    /// <summary>
    /// How long a missing host is given to turn up. Measured against the gap this exists for —
    /// tens of milliseconds at a GNOME login — with room to spare for a slower machine, and
    /// short enough that someone who genuinely has no notification area is told while they are
    /// still wondering where the icon went.
    /// </summary>
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromSeconds(10);

    private readonly Func<string, string, Task<uint?>> _show;
    private readonly Func<uint, Task> _close;
    private readonly Action _onHostArrived;
    private readonly IAppLog? _log;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<CancellationToken, Task<bool>> _hostArrives;

    public TrayHostNotice(
        Func<string, string, Task<uint?>> show,
        Func<uint, Task> close,
        Action onHostArrived,
        IAppLog? log = null,
        TimeSpan? grace = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<CancellationToken, Task<bool>>? hostArrives = null)
    {
        _show = show;
        _close = close;
        _onHostArrived = onHostArrived;
        _log = log;
        Grace = grace ?? DefaultGrace;
        _delay = delay ?? (static (wait, token) => Task.Delay(wait, token));
        _hostArrives = hostArrives ?? SniHostProbe.WaitForHostAsync;
    }

    /// <summary>How long this one holds the notice back. Readable so the head's wiring can be
    /// asserted to be the real wait rather than a test's.</summary>
    public TimeSpan Grace { get; }

    /// <summary>
    /// Runs the whole of it: wait, then speak if nothing turned up, then — whenever a host
    /// does turn up — withdraw whatever was said and bring the icon back.
    ///
    /// <para>Never throws, and returns on cancellation: the exit row cancels this, and an app
    /// on its way out has nobody to tell.</para>
    /// </summary>
    public async Task RunAsync(TrayHostState state, CancellationToken cancellation = default)
    {
        var arrived = _hostArrives(cancellation);
        var waited = _delay(Grace, cancellation);
        uint? shown = null;

        try
        {
            await Task.WhenAny(arrived, waited).ConfigureAwait(false);

            if (HostIsThere(arrived))
            {
                _log?.Write("tray host appeared during the notice wait — notice not shown");
            }
            else
            {
                // The watch finishing is not the same as a host existing: it also completes
                // false the moment the bus cannot be watched at all, and "I could not ask" is
                // not evidence of a notification area (CLAUDE.md rule 6). So the notice waits
                // its full turn either way before it says anything.
                await waited.ConfigureAwait(false);

                shown = await _show(NoticeText.NoTrayHostTitle, SniHostProbe.Explain(state))
                    .ConfigureAwait(false);
                _log?.Write($"no tray host notice shown after {Grace.TotalSeconds:0}s: id={Describe(shown)}");
            }

            if (!await arrived.ConfigureAwait(false))
            {
                // No host, and nothing left that would tell us one arrived. The icon cannot
                // recover on its own from here; the notice is what the user has.
                return;
            }

            if (shown is { } id)
            {
                _log?.Write($"tray host appeared — closing notice {id}");
                await _close(id).ConfigureAwait(false);
            }

            if (cancellation.IsCancellationRequested)
            {
                return;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _onHostArrived();
    }

    /// <summary>
    /// Whether the watch has already reported a host. Completion alone will not do: it also
    /// completes false when the bus cannot be watched, which says nothing about whether a
    /// notification area is there.
    /// </summary>
    private static bool HostIsThere(Task<bool> arrived) =>
        arrived is { IsCompletedSuccessfully: true, Result: true };

    /// <summary>
    /// A notification the desktop refused and one it filed under an id print differently in
    /// the log — the first is why a later "closing notice" line is missing, and the log is the
    /// only account of this anyone reporting it can send.
    /// </summary>
    private static string Describe(uint? id) => id?.ToString() ?? "(not shown)";
}
