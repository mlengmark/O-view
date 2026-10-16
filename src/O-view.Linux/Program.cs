using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Logging;
using OView.App;
using OView.App.Diagnostics;
using OView.App.Platform;
using OView.App.Updates;
using OView.Core.Models;
using OView.Core.Providers.CachedUsage;
using OView.Linux.Platform;
using OView.Linux.Rendering;
using OView.Linux.Tray;

namespace OView.Linux;

/// <summary>
/// The Linux head's entry point (ADR-0013).
///
/// <para>Diagnostic args, deliberately mirroring the Windows head's so a bug report reads
/// the same on either platform: <c>--log path</c>, <c>--interval-ms N</c>,
/// <c>--samples dir</c> (render the tray icon at every size an SNI host might request and
/// exit), <c>--probe</c> (report what the session bus says and exit).</para>
///
/// <para>Plus <c>--startup-on</c> / <c>--startup-off</c> / <c>--startup-status</c>, which are
/// not merely diagnostic here: on a desktop with no notification-area host there is no menu,
/// so these are the <i>only</i> way to control run-at-startup. Handled before the
/// single-instance guard so they work while O-view is running.</para>
/// </summary>
internal static class Program
{
    /// <summary>
    /// When this process is allowed to end: only when something asks it to.
    ///
    /// <para><b>Why not the lifetime's default.</b> <c>OnLastWindowClose</c> is wrong for a tray
    /// app, and it took the app down the second time anyone looked at the panel. A panel
    /// dismissed with Esc or a click away is hidden, not closed, so it is still one of the
    /// application's windows; the next open closes it before building its replacement; that was
    /// the last window, so the shutdown ran while the new panel was coming up. The panel flashed,
    /// the icon disappeared, and because nothing had failed the log simply ended — no shutdown
    /// line, no exception, no core dump (issue #312).</para>
    ///
    /// <para>Nothing depended on the default. Both ways this process is meant to end ask for it:
    /// Exit O-view calls <c>Shutdown()</c>, and so does the <c>--panel-samples</c> render once it
    /// has written its PNGs. <see cref="LinuxApp"/> logs the mode the lifetime actually ends up
    /// with, because what this file asked for and what is in force are different facts.</para>
    /// </summary>
    private const ShutdownMode EndsOnlyWhenAsked = ShutdownMode.OnExplicitShutdown;

    [STAThread]
    public static int Main(string[] args)
    {
        var parsed = CommandLine.Parse(args);

        // What the panel's explanatory copy should tell a stuck user to do. This head has no
        // Copy-diagnostics menu item — the SNI menu carries Exit only — so the shared copy
        // must name the command instead of a menu the user does not have.
        DiagnosticsHint.Use("Run o-view --diagnose in a terminal");

        // Offscreen hooks first: they need no bus, no display and no single-instance
        // claim, so they can be run against a machine already running O-view. That is what
        // makes a remote bug report a single round trip rather than a conversation.
        if (parsed.TryGetValue("--samples", out var samplesDir))
        {
            RenderSamples(samplesDir ?? "samples");
            return 0;
        }

        if (parsed.TryGetValue("--panel-samples", out var panelDir))
        {
            LinuxApp.SampleDirectory = panelDir ?? "panel-samples";
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime([], EndsOnlyWhenAsked);
        }

        if (parsed.ContainsKey("--probe"))
        {
            var state = SniHostProbe.CheckAsync().GetAwaiter().GetResult();
            Console.WriteLine($"tray host   : {state}");
            Console.WriteLine($"             {SniHostProbe.Explain(state)}");
            Console.WriteLine($"desktop     : {DesktopEnvironment()}");
            Console.WriteLine($"session     : {SessionType()}");
            return 0;
        }

        // Run-at-startup, from a terminal. Deliberately handled BEFORE the single-instance
        // guard — unlike the Windows head's equivalent pair, which run inside the live
        // instance — so these work whether or not O-view is already running, which is the
        // normal case for someone who has just installed it and wants it to come back after
        // a reboot.
        //
        // They also exist because the menu item beside them cannot be relied on: how
        // faithfully an SNI host renders a checkable entry varies by host, and on GNOME
        // without an AppIndicator extension there is no menu at all. A flag works on every
        // desktop, including one with no notification area.
        if (parsed.ContainsKey("--startup-on") || parsed.ContainsKey("--startup-off")
            || parsed.ContainsKey("--startup-status"))
        {
            var startup = new XdgAutostartRegistration();

            // Apply is a default interface method, so it is reached through the interface.
            // Using it rather than Enable/Disable directly is the point: it reports the state
            // as it stands AFTER the write, not the one requested. Writing the file can fail
            // — a read-only home, a full disk — and saying otherwise would be a fabricated
            // fact about the machine (rule 6). Shared with the Windows head so neither can
            // get it subtly different.
            IStartupRegistration registration = startup;

            var enabled = parsed.ContainsKey("--startup-status")
                ? registration.IsEnabled()
                : registration.Apply(parsed.ContainsKey("--startup-on"));

            Console.WriteLine($"run at startup : {(enabled ? "enabled" : "disabled")}");
            Console.WriteLine($"autostart file : {startup.FilePath}");

            // A request that did not take is worth a non-zero exit, so a script can tell.
            var requested = parsed.ContainsKey("--startup-status") || enabled == parsed.ContainsKey("--startup-on");
            if (!requested)
            {
                Console.Error.WriteLine("could not write the autostart file — check permissions on the directory above");
            }
            return requested ? 0 : 1;
        }

        // The full support bundle, same shape as the Windows head's so a report is
        // comparable across platforms. Writes to stdout by default — unlike Windows, where
        // a windowed app has no console and must be given a path.
        if (parsed.TryGetValue("--diagnose", out var diagnosePath))
        {
            // deepAudit, as on the Windows head: this is a deliberate command-line run with no
            // toolkit started and no engine to interrupt, so the transcript reconciliation can
            // afford the seconds it costs (issue #218).
            var bundle = DiagnosticsBundle.Build(DescribeThisMachine(), deepAudit: true);
            if (diagnosePath is { Length: > 0 })
            {
                File.WriteAllText(diagnosePath, bundle);
                Console.WriteLine($"wrote diagnostics to {Path.GetFullPath(diagnosePath)}");
            }
            else
            {
                Console.Write(bundle);
            }
            return 0;
        }

        // Both asked BEFORE the toolkit starts, and for the same reason: blocking the UI
        // thread on a D-Bus round trip deadlocks the app outright — measured during the
        // spike, not guessed (docs/findings/linux-tray-spike.md), and shipped anyway in
        // v0.6.1 by way of the theme read, which froze the app on the first left click
        // (issue #124). There is no dispatcher yet at this point, so this is the one place
        // in the process's life where waiting on the bus is safe.
        //
        // Started together rather than one after the other so a desktop that answers
        // neither costs one timeout, not two, before the icon appears.
        var probe = SniHostProbe.CheckAsync();
        var theme = PortalThemeSource.PrimeAsync();

        var hostState = probe.GetAwaiter().GetResult();
        theme.GetAwaiter().GetResult();

        var guard = new FileLockSingleInstanceGuard();
        if (!guard.TryAcquire())
        {
            return 0;   // already running; two icons and double polling is the thing to avoid
        }

        // On by default; --log only redirects it. Same reasoning as the Windows head, and
        // the reason it is written the same way in both: a flag nobody passed is not
        // instrumentation. See FileLog for the bound that makes always-on affordable.
        var log = new FileLog(
            parsed.TryGetValue("--log", out var logPath) && logPath is { Length: > 0 } ? logPath : null);

        log.WriteSessionHeader(
            typeof(Program).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0",
            DetectInstallKind().ToString());
        log.Write($"startup desktop={DesktopEnvironment()} session={SessionType()} trayHost={hostState}");

        // The toolkit's own log, into this file. Everything that can fail on this head fails
        // inside Avalonia's FreeDesktop backends — publishing the tray icon, reading the
        // portal theme, accessibility — and each of them reports itself through Avalonia's
        // logger and nowhere else. See AvaloniaLog for what was happening to those reports
        // until now.
        //
        // Written down as a line of its own because the absence of toolkit lines below is
        // itself a finding: it means the toolkit said nothing, rather than that nobody was
        // listening, and those two have been indistinguishable in every report so far.
        Logger.Sink = new AvaloniaLog(log);
        log.Write($"avalonia log into this file, {AvaloniaLog.Floor} and above");

        var interval = parsed.TryGetValue("--interval-ms", out var ms)
                       && int.TryParse(ms, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMs)
                       && parsedMs >= 50
            ? TimeSpan.FromMilliseconds(parsedMs)
            : TimeSpan.FromSeconds(60);

        // Everything about *what the numbers mean* — provider composition, the poll loop and
        // its cadence, the rollup store and weekly-reset log lifecycle, threshold and off-plan
        // decisions, settings, and the update schedule — lives on the other side of the seam,
        // which is platform-neutral and tested (ADR-0012). What stays on this head is the Linux
        // face, and these options are all it says about the rest.
        var shellOptions = new UsageEngineOptions
        {
            PollInterval = interval,
            Log = log,

            // Lets the shell ask Claude Code to refresh its own usage cache (issue #234).
            // Without it, a machine with no Claude Desktop shows "unknown" indefinitely: Claude
            // Code refreshes that block only when /usage runs, and a running session does not
            // maintain it. No credential is involved — Claude Code authenticates itself and
            // O-view reads the file it already reads (ADR-0015).
            UsageCacheRefresher = new ClaudeCliRefresher(),
        };

        // Built below rather than here, and so held in a variable the finally can reach: the
        // shell has to be closed around the skin it pushes at, and on this head Avalonia
        // constructs that object itself. Disposing on every exit path is what the engine's
        // `using` used to do, and the catch below is one of those paths.
        ShellHost? host = null;

        try
        {
            return BuildAvaloniaApp()
                .AfterSetup(builder =>
                {
                    // The first moment the application object exists. The Windows head builds
                    // its skin inside this factory; here the skin is the object Avalonia has
                    // just made, so the factory hands that one back.
                    var app = (LinuxApp)builder.Instance!;

                    // The seam (docs/architecture.md): the shell builds and owns everything
                    // behind it, and this file names none of it beyond the options above. It
                    // pushes through the skin's own hop onto the UI thread, which is the same
                    // object the tray and menu callbacks use (issue #143, issue #125).
                    host = new ShellHost(shellOptions, app.UiDispatcher, _ => app);
                    app.Attach(host, hostState, log);
                })
                .StartWithClassicDesktopLifetime(args, EndsOnlyWhenAsked);
        }
        catch (Exception ex)
        {
            log?.Write($"FATAL {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            host?.Dispose();
            guard.Dispose();
        }
    }

    /// <summary>
    /// Builds the toolkit. Deliberately without <c>LogToTrace</c>, which used to end this
    /// chain: see <see cref="AvaloniaLog"/>. Avalonia keeps one sink, so leaving it there
    /// would simply replace the one <see cref="Main"/> installs.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<LinuxApp>().UsePlatformDetect();

    /// <summary>
    /// Avalonia's own log, written into O-view's log file.
    ///
    /// <para><b>Why this exists.</b> The builder used to end in <c>LogToTrace</c>, which routes
    /// the toolkit's log into <see cref="System.Diagnostics.Trace"/>. A shipped build registers
    /// no trace listener, so every line Avalonia wrote was formatted and dropped — including the
    /// handful its FreeDesktop backends write when they give up, which are the only account
    /// anything gives of why an icon or a theme read did not happen.</para>
    ///
    /// <para><b>Warning and above only.</b> Below that the toolkit narrates layout and property
    /// changes by the thousand, which would roll a bounded log away before anything worth
    /// reading reached it. What is wanted is the few lines a backend writes when it stops.</para>
    ///
    /// <para>Each line is marked <c>avalonia</c> and carries the toolkit's own area, because
    /// this file is pasted whole into bug reports and "which of these did O-view write" is
    /// otherwise guesswork.</para>
    /// </summary>
    private sealed class AvaloniaLog(IAppLog log) : ILogSink
    {
        /// <summary>The lowest level passed through — named so the log can state it.</summary>
        public const LogEventLevel Floor = LogEventLevel.Warning;

        public bool IsEnabled(LogEventLevel level, string area) => level >= Floor;

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) =>
            Write(level, area, source, messageTemplate);

        public void Log(
            LogEventLevel level,
            string area,
            object? source,
            string messageTemplate,
            params object?[] propertyValues) =>
            Write(level, area, source, Fill(messageTemplate, propertyValues));

        private void Write(LogEventLevel level, string area, object? source, string message) =>
            log.Write($"avalonia {level} [{area}] {source?.GetType().Name ?? "-"}: {message}");

        /// <summary>
        /// Puts the values into the template's <c>{...}</c> holes in the order Avalonia passed
        /// them. A hole with no value left for it is kept as written rather than throwing: this
        /// runs on the toolkit's failure paths, where an exception would replace a reported
        /// failure with an unreported one.
        /// </summary>
        private static string Fill(string template, object?[] values)
        {
            var filled = new StringBuilder(template.Length);
            var next = 0;

            for (var i = 0; i < template.Length; i++)
            {
                if (template[i] != '{')
                {
                    filled.Append(template[i]);
                    continue;
                }

                var close = template.IndexOf('}', i);
                if (close < 0 || next >= values.Length)
                {
                    filled.Append(template.AsSpan(i));
                    break;
                }

                filled.Append(values[next++]);
                i = close;
            }

            return filled.ToString();
        }
    }

    /// <summary>
    /// Renders the tray icon at every size an SNI host might ask for, in both panel
    /// themes, so legibility can be judged from images rather than described in prose.
    ///
    /// <para>Linux hosts commonly request 22–24 px and 48 px on HiDPI — larger than the
    /// 16/20/24 the Windows evidence covers, so the original legibility finding does not
    /// carry over untested.</para>
    /// </summary>
    private static void RenderSamples(string directory)
    {
        Directory.CreateDirectory(directory);

        (string Name, UsageSnapshot Snapshot)[] states =
        [
            // A measured zero — the one state with no arc, where the pupil fades to the track
            // (issue #139). Rendered here as well as on the Windows head, because the fade is
            // a shared decision and this sheet is where a Linux drift would show.
            ("live-00", IconState(DataSource.Live, 0, 0)),
            ("live-06", IconState(DataSource.Live, 6, 1)),
            ("live-47", IconState(DataSource.Live, 47, 20)),
            ("live-58", IconState(DataSource.Live, 58, 30)),
            ("live-72", IconState(DataSource.Live, 72, 40)),
            ("live-91", IconState(DataSource.Live, 91, 60)),
            ("live-100", IconState(DataSource.Live, 100, 80)),
            ("estimate", IconState(DataSource.Estimate, null, null)),
            ("none", UsageSnapshot.None),
        ];

        foreach (var size in SkiaIconRenderer.CommonSizes)
        {
            foreach (var light in new[] { false, true })
            {
                foreach (var (name, snapshot) in states)
                {
                    var png = SkiaIconRenderer.RenderPng(size, snapshot, light);
                    File.WriteAllBytes(
                        Path.Combine(directory, $"{name}-{size}px-{(light ? "light" : "dark")}.png"),
                        png);
                }
            }
        }

        Console.WriteLine(
            $"wrote {SkiaIconRenderer.CommonSizes.Length * 2 * states.Length} PNGs to {Path.GetFullPath(directory)}");
    }

    /// <summary>
    /// One icon state: the two meters as a source reported them, with no reset times, which
    /// the icon does not draw.
    /// </summary>
    private static UsageSnapshot IconState(DataSource source, int? session, int? weekly) => new(
        source,
        UsageSnapshot.Percent(session),
        UsageSnapshot.Percent(weekly),
        UsageSnapshot.Instant(null),
        UsageSnapshot.Instant(DateTimeOffset.UtcNow),
        UsageSnapshot.Instant(null));

    /// <summary>
    /// What only this head can tell the shared bundle. The tray-host line is the
    /// highest-value field on Linux: "my icon doesn't appear" is the most likely report by
    /// a wide margin, and an Avalonia tray icon claims success whether or not a host
    /// exists — so the bus is asked rather than the toolkit.
    /// </summary>
    private static DiagnosticsEnvironment DescribeThisMachine() => new(
        Version: typeof(Program).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0",
        InstallKind: DetectInstallKind().ToString(),
        Desktop: DesktopEnvironment(),
        SessionType: SessionType(),
        TrayHost: SniHostProbe.CheckAsync().GetAwaiter().GetResult().ToString());

    /// <summary>
    /// Whether dpkg owns this build. An apt install must never self-update — overwriting
    /// files the package manager owns is silently reverted by the next upgrade (ADR-0009).
    /// </summary>
    internal static InstallKind DetectInstallKind() =>
        Environment.ProcessPath?.StartsWith("/usr/lib/o-view", StringComparison.Ordinal) == true
            ? InstallKind.LinuxPackage
            : InstallKind.LinuxTarball;

    internal static string DesktopEnvironment() =>
        Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP")
        ?? Environment.GetEnvironmentVariable("DESKTOP_SESSION")
        ?? "(unset)";

    internal static string SessionType() =>
        Environment.GetEnvironmentVariable("XDG_SESSION_TYPE")
        ?? (Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 } ? "wayland" : "(unset)");

    internal static string RuntimeDescription() =>
        $"{RuntimeInformation.OSDescription.Trim()} / {RuntimeInformation.OSArchitecture}";
}

/// <summary>
/// Whether this session lets O-view put its own window back where the user left it, and why it
/// decided that.
///
/// <para><b>Why it has to be asked at all.</b> A native Wayland client can neither set nor read
/// its own position on screen. The compositor will happily move a window for the user, but it
/// tells the client nothing about where — so a place dragged to there cannot be read back, and
/// offering to remember one would be O-view claiming a thing it cannot do (rule 6). On that
/// session the panel keeps the corner placement it has always had, nothing is stored, and the
/// menu's reset row is absent, because there is nothing for it to reset.</para>
///
/// <para><b>What tells it apart from XWayland.</b> This head's toolkit has exactly one Linux
/// backend — <c>Avalonia.Desktop</c> ships <c>Avalonia.X11</c> and no Wayland one — so every
/// window it opens talks to an X server: the session's own under Xorg, and XWayland under a
/// Wayland compositor. Position requests reach that server either way, which is why the Ubuntu
/// VM's Wayland session already places the panel in its corner and its log shows an X11 input
/// method (Maximilian's run of 2026-10-16). Native Wayland is therefore the case where the
/// session is Wayland and there is no X server to ask at all, which <c>DISPLAY</c> says; those
/// three variables are the whole of what Avalonia and the environment can offer, and they
/// answer it.</para>
///
/// <para>A session cannot change underneath a running process, so this is read once at startup
/// rather than per open.</para>
/// </summary>
/// <param name="Allowed">Whether a dragged place may be stored and restored.</param>
/// <param name="Reason">What was read and what it concluded — the sentence the log carries.</param>
public readonly record struct ClientPlacement(bool Allowed, string Reason)
{
    /// <summary>What this machine's session says.</summary>
    public static ClientPlacement Detect() => From(
        Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"),
        Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"),
        Environment.GetEnvironmentVariable("DISPLAY"));

    /// <summary>
    /// The decision itself, from the three variables rather than from the environment — its own
    /// method so each of the three branches can be driven on a machine that is in none of them.
    /// </summary>
    public static ClientPlacement From(string? sessionType, string? waylandDisplay, string? x11Display)
    {
        // Either one: a compositor may leave XDG_SESSION_TYPE to the login manager, and some
        // set it to "tty" for a session started by hand, but a Wayland compositor always names
        // its socket.
        var wayland = string.Equals(sessionType, "wayland", StringComparison.OrdinalIgnoreCase)
                      || waylandDisplay is { Length: > 0 };

        var xServer = x11Display is { Length: > 0 };

        return (wayland, xServer) switch
        {
            (true, true) => new ClientPlacement(true,
                $"XWayland — a Wayland session (XDG_SESSION_TYPE={Describe(sessionType)}, "
                + $"WAYLAND_DISPLAY={Describe(waylandDisplay)}) with an X server at "
                + $"DISPLAY={Describe(x11Display)}, which is what this head's windows talk to, "
                + "so a position can be requested and read back"),

            (true, false) => new ClientPlacement(false,
                $"native Wayland — a Wayland session (XDG_SESSION_TYPE={Describe(sessionType)}, "
                + $"WAYLAND_DISPLAY={Describe(waylandDisplay)}) with no X server (DISPLAY unset), "
                + "where a client can neither set nor read its own position, so the panel keeps "
                + "the work-area corner and no place is stored"),

            _ => new ClientPlacement(true,
                $"X11 — no Wayland session reported (XDG_SESSION_TYPE={Describe(sessionType)}, "
                + $"WAYLAND_DISPLAY unset), DISPLAY={Describe(x11Display)}"),
        };
    }

    private static string Describe(string? value) => value is { Length: > 0 } ? value : "(unset)";
}
