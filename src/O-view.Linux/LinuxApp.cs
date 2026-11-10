using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using OView.App;
using OView.App.Diagnostics;
using OView.Core.Models;
using OView.App.Platform;
using OView.Core.Providers.CachedUsage;
using OView.Core.Providers.Jsonl;
using OView.Core.Providers.PlanHistory;
using OView.App.Pricing;
using OView.App.Updates;
using OView.Linux.Dialogs;
using OView.Linux.Notifications;
using OView.Linux.Updates;
using OView.Linux.Panel;
using OView.Linux.Platform;
using OView.Linux.Rendering;
using OView.Linux.Tray;
using OView.Presentation.Panel;
using OView.Presentation.Text;
using Tmds.DBus.Protocol;

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
    private readonly FilePanelPositionStore _panelPositions = new();

    /// <summary>
    /// Whether this session lets the panel be put back where the user left it. Read once: a
    /// session does not change underneath a running process.
    /// </summary>
    private readonly ClientPlacement _placement = ClientPlacement.Detect();

    /// <summary>
    /// This head's one hop onto the UI thread. <see cref="Program"/> hands the same object to
    /// the shell, so a push and a bus callback arrive by exactly the same route, and there is
    /// one place to look when either turns out not to.
    /// </summary>
    private readonly IUiDispatcher _dispatcher = new AvaloniaUiDispatcher();

    /// <summary>The shell's lifetime: this head starts it, <see cref="Program"/> disposes it.</summary>
    private ShellHost? _host;

    /// <summary>What this head asks of the shell, the same object for the shell's whole life.</summary>
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

    /// <summary>The threshold submenu's offered percentages, re-ticked after a choice and on every render.</summary>
    private (int Percent, NativeMenuItem Item)[] _thresholdItems = [];

    /// <summary>
    /// A threshold none of the offers names, a hand-edited settings.json, shown as it is and
    /// hidden otherwise. Ticking none of the three would leave the setting in effect unsaid.
    /// </summary>
    private NativeMenuItem? _unofferedThresholdItem;

    private NativeMenuItem? _notifyOffItem;

    /// <summary>The notification setting as the shell holds it, read on every render like the resume row.</summary>
    private Func<(bool On, int Percent)>? _notifySetting;

    /// <summary>The weekly-reset row, whose label carries the entry in effect.</summary>
    private NativeMenuItem? _weeklyResetItem;

    /// <summary>The entered weekly reset as the shell holds it, read on every render like the resume row.</summary>
    private Func<ManualWeeklyReset?>? _weeklyResetSetting;

    /// <summary>The weekly-reset dialog while one is open, so the row brings it forward rather than opening a second.</summary>
    private WeeklyResetDialog? _weeklyResetDialog;
    private TrayIcon? _tray;
    private PanelWindow? _panel;
    private CancellationTokenSource? _hostWatch;

    /// <summary>What keeps the icon off the bus until there is a reading to put in it.</summary>
    private FirstReadingHold? _firstReading;

    /// <summary>
    /// How long a host that has just appeared is given to list the icon before this head
    /// registers it itself. The toolkit's own registration lands within milliseconds of the
    /// name changing hands; the rest is room for a host slow to add what it was given, and
    /// costs only how late the icon appears on the one path where that registration never
    /// arrived.
    /// </summary>
    private static readonly TimeSpan HostListingPatience = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Set by the reset row when there is no panel yet to forget a place for, see
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
    /// Handed the shell once it exists, Avalonia constructs this type itself, so this cannot
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
            // Offscreen render and exit, no tray, no bus, no shell. The panel is
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
        // The mode the lifetime actually has, not the one Program asked for, the two are
        // different facts, and the wrong one here used to end the app the second time anyone
        // opened the panel without writing a word about it (issue #312). A line naming it is the
        // only way a log that stops mid-session can be told from one that was shut down.
        _log?.Write(
            "shutdown mode="
            + ((ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.ShutdownMode.ToString()
               ?? "(no desktop lifetime)"));

        // Which of the two Wayland cases this is, and why, the one decision behind both the
        // panel keeping its corner and the reset row being absent, so a report from a session
        // where dragging does nothing says in one line that it was meant to.
        _log?.Write($"remembered panel position: {(_placement.Allowed ? "available" : "unavailable")}"
                    + $", {_placement.Reason}");

        _tray = new TrayIcon
        {
            Icon = ToWindowIcon(UsageSnapshot.None),
            ToolTipText = PanelHeaderView.AppName,
            IsVisible = true,
            Menu = BuildMenu(host.Shell, _placement),
        };
        _tray.Clicked += OpenPanelHandler(host.Shell);

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
        // installed, see the Windows head, which wires it identically because there is
        // nothing platform-specific about asking a public page what it says.
        _rates = new RateCardFeed(_log);

        var timers = new AvaloniaTimerFactory();

        // The icon is built above and published once the first poll has been drawn into it, so
        // the first thing a notification area reads is the reading (see FirstReadingHold).
        _firstReading = new FirstReadingHold(PublishTray, timers, _log);
        _firstReading.Start();

        // Last, so the four pushes below cannot arrive before what answers them exists. Ticks
        // arrive on the UI thread; the reading happens off it and comes back through the
        // dispatcher this head handed the shell. The pair is what keeps the icon and the menu
        // responsive while a first run ingests a large transcript history (issue #125).
        host.Start(timers);

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

    /// <summary>
    /// What actually publishes the icon, and the line whose absence meant this head never
    /// reached a notification area on any desktop. A constructed TrayIcon has no platform
    /// implementation behind it: the toolkit builds one only for an icon in the application's
    /// own collection, and until then every property set on it is dropped by a null-conditional
    /// call that reports nothing and returns nothing to check. So the gauge was drawn every
    /// poll, the icon reported itself visible, and the session bus never heard a word, which is
    /// also why the toolkit's log has nothing to say about it (see Program.AvaloniaLog): the
    /// backend that would have complained was never constructed.
    /// </summary>
    private void PublishTray() => TrayIcon.SetIcons(this, new TrayIcons { _tray! });

    // ── what the shell pushes at this skin ───────────────────────────────────────────
    //
    // All four arrive on the UI thread without a Post of their own: the shell puts every push
    // through the dispatcher it was handed in Program, including the ones that were already on
    // this thread, so there is no per-callback rule for this head to get wrong (issue #125).

    /// <summary>A new snapshot to draw, in the icon and its tooltip.</summary>
    public void ShowSnapshot(UsageSnapshot snapshot)
    {
        Render(snapshot);

        // Outside Render, whose catch keeps a bad frame from killing the app: a failure to
        // publish is not a bad frame, and swallowing it would leave no icon and no reason.
        _firstReading?.ReadingArrived();

        // Rides the snapshot, as on Windows, because a poll is when the conflict is found.
        if (_shell is { } shell)
        {
            NotifyWeeklyResetConflict(shell);
        }
    }

    /// <summary>
    /// Tells the user once when the reset Claude reported disproves the one they entered (issue
    /// #186). The shell has already set the entry aside for the reported one, so this explains a
    /// change that has happened; silence would leave them believing the time they typed.
    ///
    /// <para>Once per conflicting observation, not once per poll: a warning that came back every
    /// poll is one people learn to dismiss. Same words and the same rule as the Windows head.</para>
    /// </summary>
    private void NotifyWeeklyResetConflict(ISkinToShell shell)
    {
        try
        {
            if (WeeklyResetConflictNotice(shell) is { } notice)
            {
                _log?.Write("weekly reset conflict noticed");
                _ = _notifier.ShowAsync(notice.Title, notice.Body);
            }
        }
        catch (Exception ex)
        {
            // A settings write that fails here must not take the snapshot's caller with it.
            _log?.Write($"weekly reset conflict notice FAILED {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// The notice for a conflict the user has not been told about, marked told, or null. Its own
    /// method so the rule is assertable without a notification service.
    /// </summary>
    private static (string Title, string Body)? WeeklyResetConflictNotice(ISkinToShell shell)
    {
        if (shell.WeeklyResetConflict is not { } conflict || !shell.IsWeeklyResetConflictUnseen(conflict))
        {
            return null;
        }

        shell.MarkWeeklyResetConflictNoticed(conflict);
        return (NoticeText.WeeklyResetConflictTitle, PanelText.WeeklyResetConflict(conflict, TimeZoneInfo.Local));
    }

    /// <summary>
    /// A freedesktop notification.
    ///
    /// <para><see cref="AppNotification.Kind"/> is deliberately dropped here rather than
    /// mapped. A freedesktop notification already shows O-view's own app icon, so there is no
    /// severity glyph to pick; the only severity channel the spec offers is the urgency hint,
    /// and marking anything critical makes it persist on screen until dismissed, wrong for all
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
    /// light and dark never needs a restart, the same rule the WPF panel follows.
    ///
    /// <para><b>UI thread only.</b> Reached through <see cref="BusCallback"/>, because the
    /// tray click that calls it arrives on the D-Bus thread and everything below here builds
    /// and shows an Avalonia window. Called directly from a bus callback, this segfaulted
    /// (issue #143), and note that the <c>catch</c> below cannot help with that: a SIGSEGV
    /// is not an exception, which is why the report was a core dump and an empty log.</para>
    /// </summary>
    private void ShowPanel(ISkinToShell shell)
    {
        try
        {
            // Opens on the last polled snapshot, at most one poll old, and exactly what
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

                // Read at every open, so the panel shows the view it was left in, and a notice
                // the shell has un-hidden since then is back.
                ViewChoice = shell.PanelView,
                SetViewChoice = shell.SetPanelView,
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
            // display that still opens, so "panel opened" alone cannot tell the #143 defect
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
    /// The notice as this head wires it, built but not started, its own method so the wiring
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
    /// icon when a host appears is the Linux <c>TaskbarCreated</c> (ADR-0003 item 5), and
    /// without it they would have to know to restart, which nothing tells them.
    ///
    /// <para><b>Only if the host does not already list it.</b> The toolkit's tray backend
    /// registers the icon again by itself whenever the watcher's name changes hands, so by the
    /// time a host has appeared it usually has the icon already. Hiding and showing it on top
    /// of that was a second registration, and the desktop the report came from drew both: two
    /// O-view icons, each with a working menu, until exit (issue #356). So the host's own list
    /// is read first, and the icon is re-shown only when nothing in it belongs to this
    /// process.</para>
    ///
    /// <para>The list is read off the UI thread, because every read is a D-Bus round trip
    /// (issue #124); what follows is posted, because it touches the toolkit, the SIGSEGV of
    /// issue #143, not an exception any catch could reach.</para>
    /// </summary>
    private void ReShowIcon() => _ = ReShowIconUnlessListedAsync(_hostWatch?.Token ?? CancellationToken.None);

    private async Task ReShowIconUnlessListedAsync(CancellationToken cancellation)
    {
        var listing = await TrayHostListing
            .WaitForProcessAsync(Environment.ProcessId, HostListingPatience, cancellation)
            .ConfigureAwait(false);

        if (cancellation.IsCancellationRequested)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_tray is null || _shell is null)
            {
                return;
            }

            switch (listing)
            {
                case TrayHostListingState.NotListed:
                    _log?.Write($"tray host appeared, icon not listed after {HostListingPatience.TotalSeconds:0}s, re-showing icon");
                    _tray.IsVisible = false;
                    _tray.IsVisible = true;
                    break;

                case TrayHostListingState.Listed:
                    _log?.Write("tray host appeared, icon already listed, not registered again");
                    break;

                default:
                    // "I could not ask" is not evidence that the icon is missing (rule 6), and
                    // the toolkit has registered it on its own either way. A second icon is the
                    // failure that was seen; a missing one has not been.
                    _log?.Write("tray host appeared, its list could not be read, icon left as the toolkit registered it");
                    break;
            }

            Render(_shell.Latest);
        });
    }

    private void Render(UsageSnapshot snapshot)
    {
        try
        {
            if (_tray is null)
            {
                return;
            }

            _tray.Icon = ToWindowIcon(snapshot, out var diagnostics);

            // Set every poll, and on this platform it does not become a tooltip. Avalonia's
            // tray backend publishes this text as the item's SNI Title and answers the SNI
            // ToolTip property with a hard-coded empty structure, so there is nothing here to
            // set differently; and the desktop the report came from never asks for ToolTip
            // anyway, having commented it out of its interface (issue #314, proved on the bus
            // by TrayTooltipRuntimeTests). The exact figures reach a GNOME user through the
            // panel instead, which is what the README's support matrix says.
            _tray.ToolTipText = TooltipFormatter.Format(snapshot);

            // A native menu is drawn by the host and offers no opening hook, so the resume row's
            // visibility rides the render instead, which happens every poll, more often than a
            // user could open the menu.
            SyncResumeItem();
            SyncNotifyItems();
            SyncWeeklyResetItem();

            _log?.Write(
                $"icon rendered session={Describe(snapshot.SessionPercentValue)} "
                + $"pixmap={diagnostics.Width}x{diagnostics.Height} painted={diagnostics.PaintedPixelCount}");
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
    /// Linux report, the icon showing nothing, unable to say which had happened.
    /// </summary>
    private static string Describe(UsagePercent percent) => percent.Status switch
    {
        UsageValueStatus.Unavailable => "unavailable",
        _ => percent.Value is { } value
            ? $"{value}{(percent.Status == UsageValueStatus.Estimated ? " (estimated)" : "")}"
            : "unknown",
    };

    /// <summary>
    /// SNI wants pixels, not a themed icon name, the gauge changes every poll, so there is
    /// no theme entry it could refer to.
    /// </summary>
    private static WindowIcon ToWindowIcon(UsageSnapshot snapshot) => ToWindowIcon(snapshot, out _);

    private static WindowIcon ToWindowIcon(UsageSnapshot snapshot, out IconRenderDiagnostics diagnostics)
    {
        // 24 px is the size Linux panels most commonly request; hosts scale from what they
        // are given. HiDPI sizing is one of the things still to confirm on real hardware.
        using var stream = new MemoryStream(SkiaIconRenderer.RenderPng(24, snapshot, lightPanel: false, out diagnostics));
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
        // that was requested. Writing the autostart file can fail, a read-only home, a full
        // disk, and a tick claiming otherwise would be a fabricated fact about the user's
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

        var notify = BuildNotifyMenu(shell);

        // Starts on "derived" and is corrected by the first render, before the menu is published:
        // building it must not read the shell, for the reason given at _usageRefreshBlocked.
        var weeklyReset = new NativeMenuItem(WeeklyResetRowHeader(null));

        // Marshalled like every other row: the dialog is a window, and building one on the bus
        // thread is the SIGSEGV of issue #143.
        weeklyReset.Click += BusCallback.For(_dispatcher, () => EditWeeklyReset(shell), _log);
        _weeklyResetItem = weeklyReset;
        _weeklyResetSetting = () => shell.Settings.WeeklyReset;

        var diagnostics = new NativeMenuItem(MenuText.CopyDiagnosticsRow);

        // Marshalled like every other row: the bundle reads the store through the shell, and the
        // clipboard belongs to the toolkit, which is the SIGSEGV of issue #143 off the UI thread.
        diagnostics.Click += BusCallback.For(_dispatcher, () => _ = CopyDiagnosticsAsync(shell), _log);

        var exit = new NativeMenuItem(MenuText.ExitRow);

        // The third. Shutdown() tears down the dispatcher, so calling it from the bus thread
        // races the UI thread it is dismantling.
        exit.Click += BusCallback.For(_dispatcher, () =>
        {
            _hostWatch?.Cancel();
            (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        }, _log);

        // Only present once the refresh has stopped itself (issue #234). The guard behind that
        // block errs toward reporting a charge, a Claude Code session started while a refresh
        // ran looks the same as a billed one, and that direction is only correct while the user
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
        // shell, LinuxHeadWiringTests constructs it with a null one to inspect the click
        // handlers without opening a store or touching this machine's paths. The row starts
        // hidden, which is the correct initial state anyway, and Render corrects it every poll.
        _usageRefreshBlocked = () => shell.UsageRefreshBlocked;

        _startupItem = startup;

        // In the Windows flyout's order: the settings, then the resume row, then the actions.
        return reset is null
            ? [showUsage, startup, notify, weeklyReset, resume, diagnostics, exit]
            : [showUsage, reset, startup, notify, weeklyReset, resume, diagnostics, exit];
    }

    /// <summary>
    /// The weekly-reset row's label: the row's words, then the entry in effect or "derived",
    /// both from Presentation. The Windows row sets the value in a column of its own; a row the
    /// desktop draws has one label, so the gap stands in for that column.
    /// </summary>
    private static string WeeklyResetRowHeader(ManualWeeklyReset? entry) =>
        MenuText.WeeklyResetRow + "  " + (entry is { } set
            ? MenuText.WeeklyResetValue(set.DayText, set.TimeText)
            : MenuText.WeeklyResetDerived);

    private void SyncWeeklyResetItem()
    {
        if (_weeklyResetSetting is not null)
        {
            SyncWeeklyResetItem(_weeklyResetSetting());
        }
    }

    private void SyncWeeklyResetItem(ManualWeeklyReset? entry)
    {
        if (_weeklyResetItem is not null)
        {
            _weeklyResetItem.Header = WeeklyResetRowHeader(entry);
        }
    }

    /// <summary>
    /// Opens the weekly-reset dialog on the entry in effect, or brings forward the one already
    /// open: two dialogs editing one setting could each save over the other.
    /// </summary>
    private void EditWeeklyReset(ISkinToShell shell)
    {
        try
        {
            if (_weeklyResetDialog is { } open)
            {
                open.Activate();
                return;
            }

            var dialog = new WeeklyResetDialog(
                new WeeklyResetForm(shell.Settings.WeeklyReset),
                new LinuxPanelTheme(_theme.IsPanelLight()));
            dialog.Chosen += choice => ApplyWeeklyReset(shell, choice.Entry);
            dialog.Closed += (_, _) => _weeklyResetDialog = null;

            _weeklyResetDialog = dialog;
            dialog.Show();
            _log?.Write("weekly reset dialog opened");
        }
        catch (Exception ex)
        {
            // A dialog that fails to open must not take the tray icon down with it.
            _log?.Write($"weekly reset dialog FAILED {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Saves an entry, or clears it with null, through the shell, labels the row with what the
    /// shell <i>returned</i> rather than what was asked (a settings write can fail, rule 6), and
    /// polls once so the panel and the chart's week lines move with it, as the Windows head does.
    /// The shell decides everything else, including that a reset Claude reported outranks the
    /// entry.
    /// </summary>
    private void ApplyWeeklyReset(ISkinToShell shell, ManualWeeklyReset? entry)
    {
        var actual = shell.SetWeeklyReset(entry);
        SyncWeeklyResetItem(actual);
        _log?.Write($"weekly reset -> {(actual is null ? "derived" : $"{actual.DayText} {actual.TimeText}")}");
        shell.Refresh();
    }

    /// <summary>
    /// The "Notify at" row: a submenu of the percentages the Windows flyout offers, the one in
    /// effect ticked, and "Off" last.
    ///
    /// <para>A submenu rather than the Windows row's tick and pill, because a desktop-rendered
    /// menu has neither: the row that opens a submenu cannot also carry a tick. So the radio
    /// group holds both settings, and choosing a percentage switches the notification back on,
    /// which is what someone choosing one from a list headed "Notify at" is asking for.</para>
    ///
    /// <para>Like the rest of <see cref="BuildMenu"/>, this never dereferences the shell while
    /// building; the ticks start clear and the first render sets them, before the icon, and so
    /// the menu, is published.</para>
    /// </summary>
    private NativeMenuItem BuildNotifyMenu(ISkinToShell shell)
    {
        var submenu = new NativeMenu();

        _unofferedThresholdItem = new NativeMenuItem { ToggleType = MenuItemToggleType.Radio, IsVisible = false };

        // Re-applies the value it shows: choosing it from a list that is off still means "on, at this".
        _unofferedThresholdItem.Click += BusCallback.For(
            _dispatcher, () => ChooseThreshold(shell, shell.Settings.ThresholdPercent), _log);
        submenu.Items.Add(_unofferedThresholdItem);

        _thresholdItems = [.. MenuText.ThresholdChoices.Select(percent =>
        {
            var item = new NativeMenuItem(MenuText.ThresholdPercent(percent)) { ToggleType = MenuItemToggleType.Radio };
            item.Click += BusCallback.For(_dispatcher, () => ChooseThreshold(shell, percent), _log);
            submenu.Items.Add(item);
            return (percent, item);
        })];

        _notifyOffItem = new NativeMenuItem(MenuText.NotifyOffChoice) { ToggleType = MenuItemToggleType.Radio };
        _notifyOffItem.Click += BusCallback.For(_dispatcher, () =>
        {
            var on = shell.SetNotifyOnThreshold(false);
            SyncNotifyItems(on, shell.Settings.ThresholdPercent);
            _log?.Write($"notify on threshold -> {on}");
        }, _log);
        submenu.Items.Add(_notifyOffItem);

        // A closure, not a read, for the reason _usageRefreshBlocked is one.
        _notifySetting = () => (shell.Settings.NotifyOnThreshold, shell.Settings.ThresholdPercent);

        return new NativeMenuItem(MenuText.NotifyPrefix) { Menu = submenu };
    }

    /// <summary>
    /// Applies a chosen percentage and switches the notification on, then ticks what the shell
    /// <i>returned</i>, never what was asked: a settings write can fail, and a tick claiming a
    /// value that was never saved would be the menu asserting something untrue (rule 6).
    ///
    /// <para>The percentage goes first so that switching on, which re-arms the watcher, arms it
    /// at the new threshold rather than the old one.</para>
    /// </summary>
    private void ChooseThreshold(ISkinToShell shell, int percent)
    {
        var actual = shell.SetThresholdPercent(percent);
        var on = shell.SetNotifyOnThreshold(true);
        SyncNotifyItems(on, actual);
        _log?.Write($"notify on threshold -> {on}, threshold -> {actual}");
    }

    private void SyncNotifyItems()
    {
        if (_notifySetting is not null)
        {
            var (on, percent) = _notifySetting();
            SyncNotifyItems(on, percent);
        }
    }

    /// <summary>
    /// Ticks exactly one choice: the percentage in effect while the notification is on, "Off"
    /// while it is not. A hand-edited threshold none of the offers names gets its own row,
    /// worded the way the Windows pill shows it, rather than being snapped to the nearest offer.
    /// </summary>
    private void SyncNotifyItems(bool on, int percent)
    {
        foreach (var (choice, item) in _thresholdItems)
        {
            item.IsChecked = on && choice == percent;
        }

        var unoffered = on && !MenuText.ThresholdChoices.Contains(percent);
        if (_unofferedThresholdItem is { } row)
        {
            row.Header = MenuText.ThresholdPercent(percent);
            row.IsVisible = unoffered;
            row.IsChecked = unoffered;
        }

        if (_notifyOffItem is not null)
        {
            _notifyOffItem.IsChecked = !on;
        }
    }

    /// <summary>
    /// The "Copy diagnostics" row: the same bundle the Windows row copies, from the shared
    /// builder, with what only this head can say about the machine.
    ///
    /// <para><b>No window is opened for it.</b> The clipboard is the one the toolkit's X11
    /// platform registers for the whole application, which owns the selection through a hidden
    /// window of its own; a window's <c>Clipboard</c> property hands back that same object. The
    /// head is X11 only, under XWayland on a Wayland session, so this is the clipboard on every
    /// desktop it runs on.</para>
    ///
    /// <para>The tray host is asked again here rather than taken from startup, because "my icon
    /// is missing" is the report this most often goes with, and is awaited rather than waited
    /// on: blocking the UI thread on a D-Bus round trip deadlocks the app (issue #124).</para>
    /// </summary>
    private async Task CopyDiagnosticsAsync(ISkinToShell shell)
    {
        try
        {
            var environment = new DiagnosticsEnvironment(
                Version: ReleaseFeed.VersionOf(typeof(LinuxApp).Assembly),
                InstallKind: Program.DetectInstallKind().ToString(),
                Desktop: Program.DesktopEnvironment(),
                SessionType: Program.SessionType(),
                TrayHost: (await SniHostProbe.CheckAsync()).ToString());

            if (ApplicationClipboard() is not { } clipboard)
            {
                throw new InvalidOperationException("the toolkit registered no clipboard");
            }

            await clipboard.SetTextAsync(DiagnosticsBundle.Build(environment, shell.InspectStore()));
            _log?.Write("diagnostics copied");
            await _notifier.ShowAsync(NoticeText.DiagnosticsCopiedTitle, NoticeText.DiagnosticsCopiedBody);
        }
        catch (Exception ex)
        {
            // Whatever failed, the user asked for something and is told it did not happen; a
            // monitoring tool must not die on a support request.
            _log?.Write($"diagnostics copy FAILED {ex.GetType().Name}: {ex.Message}");
            await _notifier.ShowAsync(NoticeText.DiagnosticsCopyFailedTitle, NoticeText.DiagnosticsCopyFailedBody);
        }
    }

    /// <summary>
    /// The clipboard the toolkit registers for the whole application, or null where none is
    /// registered (no platform started, as in a unit test).
    ///
    /// <para><b>Through reflection, deliberately.</b> The toolkit's only public way to the
    /// clipboard is a window's <c>Clipboard</c> property, and that property only asks this same
    /// registry; the registry itself is left out of the reference assemblies the build compiles
    /// against, though the assembly that ships has it. The alternative was opening a window
    /// just to copy. <c>LinuxHeadWiringTests</c> fails if a toolkit update takes either member
    /// away, so that arrives as a red build rather than a row that only ever says it failed.</para>
    /// </summary>
    internal static IClipboard? ApplicationClipboard()
    {
        var registry = typeof(AvaloniaLocator)
            .GetProperty("Current", BindingFlags.Public | BindingFlags.Static)?
            .GetValue(null);

        return registry?.GetType()
            .GetMethod("GetService", BindingFlags.Public | BindingFlags.Instance, [typeof(Type)])?
            .Invoke(registry, [typeof(IClipboard)]) as IClipboard;
    }

    /// <summary>
    /// The menu's "Reset panel position" row: forget wherever the panel was dragged to, and put
    /// an open one back at the work-area corner.
    ///
    /// <para>Deferred to the next open when no panel exists yet. A place is stored per display,
    /// and which display a panel will open on is not knowable until there is a panel to ask,
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
        _log?.Write("reset panel position, deferred to the next open, the first panel there is");
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
    /// truth and anything may have changed it, the desktop's own startup-applications
    /// settings, or <c>o-view --startup-off</c> in a terminal, so a menu built once at
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
/// trip on the dispatcher deadlocks the app, issue #124), and at a stock GNOME login that is
/// before the session's own watcher has registered. The reading is honest and the notice was
/// true when it was written; then the icon appeared, and every login opened with a sentence
/// the desktop contradicted a moment later. Waiting costs a user who really has no
/// notification area ten seconds, and costs everyone else nothing.</para>
///
/// <para><b>Why it is also what brings the icon back.</b> The wait and the icon's recovery
/// (ADR-0003 item 5) watch for the same thing, the watcher name being owned, so they are one
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
    /// How long a missing host is given to turn up. Measured against the gap this exists for,
    /// tens of milliseconds at a GNOME login, with room to spare for a slower machine, and
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
    /// Runs the whole of it: wait, then speak if nothing turned up, then, whenever a host
    /// does turn up, withdraw whatever was said and bring the icon back.
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
                _log?.Write("tray host appeared during the notice wait, notice not shown");
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
                _log?.Write($"tray host appeared, closing notice {id}");
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
    /// the log, the first is why a later "closing notice" line is missing, and the log is the
    /// only account of this anyone reporting it can send.
    /// </summary>
    private static string Describe(uint? id) => id?.ToString() ?? "(not shown)";
}

/// <summary>What a notification area's own list says about one process's icon.</summary>
public enum TrayHostListingState
{
    /// <summary>An entry in the list belongs to the process.</summary>
    Listed,

    /// <summary>The list was read, and nothing in it belongs to the process.</summary>
    NotListed,

    /// <summary>The list could not be read. Distinct from NotListed, never claim more than was observed.</summary>
    Unknown,
}

/// <summary>
/// Reads the watcher's <c>RegisteredStatusNotifierItems</c> and says whether any entry in it
/// belongs to a given process.
///
/// <para><b>By process, not by name.</b> Watchers do not agree on how they list an item: one
/// lists the name the item registered under, another the connection behind it, either one
/// followed by an object path. The bus can say which process owns either, and the process is
/// the one thing this head knows about its icon without reaching into the toolkit, which picks
/// the item's name itself.</para>
///
/// <para><b>Its own connection.</b> The shared session connection carries the app's
/// notifications and theme reads, and a reply this misreads must not be able to take those
/// down with it; one connection per check, dropped afterwards, is the price of that.</para>
/// </summary>
public static class TrayHostListing
{
    private const string WatcherPath = "/StatusNotifierWatcher";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";
    private const string BusService = "org.freedesktop.DBus";

    /// <summary>
    /// How often the list is read again while the toolkit's registration is on its way. Fine
    /// enough that a check usually ends on its first or second read.
    /// </summary>
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Reads the list until an entry belongs to <paramref name="processId"/> or
    /// <paramref name="patience"/> runs out, and says what the last read found.
    ///
    /// <para>Never throws, and returns what it last read on cancellation: the exit row cancels
    /// this, and an app on its way out registers nothing.</para>
    /// </summary>
    public static async Task<TrayHostListingState> WaitForProcessAsync(
        int processId,
        TimeSpan patience,
        CancellationToken cancellation = default)
    {
        if (Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS") is not { Length: > 0 } address)
        {
            return TrayHostListingState.Unknown;
        }

        var deadline = DateTime.UtcNow + patience;
        var last = TrayHostListingState.Unknown;

        try
        {
            using var connection = new DBusConnection(address);
            await connection.ConnectAsync().ConfigureAwait(false);

            while (true)
            {
                last = await ReadAsync(connection, processId).ConfigureAwait(false);

                if (last == TrayHostListingState.Listed || DateTime.UtcNow >= deadline)
                {
                    return last;
                }

                await Task.Delay(Poll, cancellation).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return last;
        }
        catch (Exception ex) when (ex is DBusExceptionBase or IOException or InvalidOperationException)
        {
            return TrayHostListingState.Unknown;
        }
    }

    /// <summary>
    /// The bus name an entry starts with: the whole entry, or the part before its object path.
    /// Null for an entry that is only a path, which names no connection to ask about.
    /// </summary>
    public static string? BusNameOf(string entry)
    {
        var slash = entry.IndexOf('/');
        var name = slash < 0 ? entry : entry[..slash];
        return name.Length > 0 ? name : null;
    }

    private static async Task<TrayHostListingState> ReadAsync(DBusConnection connection, int processId)
    {
        IReadOnlyList<string> entries;
        try
        {
            entries = await ReadEntriesAsync(connection).ConfigureAwait(false);
        }
        catch (DBusExceptionBase)
        {
            // The watcher not answering yet, or already gone again: unread, not empty.
            return TrayHostListingState.Unknown;
        }

        foreach (var entry in entries)
        {
            if (BusNameOf(entry) is not { } name)
            {
                continue;
            }

            try
            {
                if (await ProcessOfAsync(connection, name).ConfigureAwait(false) == processId)
                {
                    return TrayHostListingState.Listed;
                }
            }
            catch (DBusExceptionBase)
            {
                // The name lost its owner between the two calls, so it is nobody's icon now.
            }
        }

        return TrayHostListingState.NotListed;
    }

    private static Task<IReadOnlyList<string>> ReadEntriesAsync(DBusConnection connection)
    {
        var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: SniHostProbe.WatcherService,
            path: WatcherPath,
            @interface: PropertiesInterface,
            member: "Get",
            signature: "ss",
            flags: MessageFlags.None);
        writer.WriteString(SniHostProbe.WatcherService);
        writer.WriteString("RegisteredStatusNotifierItems");

        return connection.CallMethodAsync(writer.CreateMessage(), static (m, _) => ReadStrings(m));
    }

    private static Task<uint> ProcessOfAsync(DBusConnection connection, string busName)
    {
        var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: BusService,
            path: "/org/freedesktop/DBus",
            @interface: BusService,
            member: "GetConnectionUnixProcessID",
            signature: "s",
            flags: MessageFlags.None);
        writer.WriteString(busName);

        return connection.CallMethodAsync(writer.CreateMessage(), static (m, _) => m.GetBodyReader().ReadUInt32());
    }

    /// <summary>
    /// The <c>as</c> inside the property's variant. Anything else reads as an empty list rather
    /// than throwing: a watcher that publishes the property in another shape lists nothing this
    /// head can recognise as its own.
    /// </summary>
    private static IReadOnlyList<string> ReadStrings(Message message)
    {
        var value = message.GetBodyReader().ReadVariantValue();
        while (value.Type == VariantValueType.Variant)
        {
            value = value.GetVariantValue();
        }

        if (value.Type != VariantValueType.Array)
        {
            return [];
        }

        var entries = new List<string>(value.Count);
        for (var i = 0; i < value.Count; i++)
        {
            if (value.GetItem(i) is { Type: VariantValueType.String } entry)
            {
                entries.Add(entry.GetString());
            }
        }

        return entries;
    }
}
