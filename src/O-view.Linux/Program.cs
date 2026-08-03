using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using OView.App;
using OView.App.Diagnostics;
using OView.App.Platform;
using OView.App.Updates;
using OView.Core.Models;
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
/// </summary>
internal static class Program
{
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
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime([]);
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

        // The full support bundle, same shape as the Windows head's so a report is
        // comparable across platforms. Writes to stdout by default — unlike Windows, where
        // a windowed app has no console and must be given a path.
        if (parsed.TryGetValue("--diagnose", out var diagnosePath))
        {
            var bundle = DiagnosticsBundle.Build(DescribeThisMachine());
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

        // Probed BEFORE the toolkit starts. Blocking the UI thread on a D-Bus round trip
        // deadlocks the app outright — measured during the spike, not guessed
        // (docs/findings/linux-tray-spike.md).
        var hostState = SniHostProbe.CheckAsync().GetAwaiter().GetResult();

        var guard = new FileLockSingleInstanceGuard();
        if (!guard.TryAcquire())
        {
            return 0;   // already running; two icons and double polling is the thing to avoid
        }

        var log = parsed.TryGetValue("--log", out var logPath) && logPath is { Length: > 0 }
            ? new FileLog(logPath)
            : null;

        log?.Write($"startup desktop={DesktopEnvironment()} session={SessionType()} trayHost={hostState}");

        var interval = parsed.TryGetValue("--interval-ms", out var ms)
                       && int.TryParse(ms, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMs)
                       && parsedMs >= 50
            ? TimeSpan.FromMilliseconds(parsedMs)
            : TimeSpan.FromSeconds(60);

        using var engine = new UsageEngine(new UsageEngineOptions { PollInterval = interval, Log = log });

        try
        {
            return BuildAvaloniaApp()
                .AfterSetup(_ => LinuxApp.Configure(engine, hostState, log))
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            log?.Write($"FATAL {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            guard.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<LinuxApp>().UsePlatformDetect().LogToTrace();

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
            ("live-06", new(DataSource.Live, 6, 1, null, DateTimeOffset.UtcNow)),
            ("live-47", new(DataSource.Live, 47, 20, null, DateTimeOffset.UtcNow)),
            ("live-58", new(DataSource.Live, 58, 30, null, DateTimeOffset.UtcNow)),
            ("live-72", new(DataSource.Live, 72, 40, null, DateTimeOffset.UtcNow)),
            ("live-91", new(DataSource.Live, 91, 60, null, DateTimeOffset.UtcNow)),
            ("live-100", new(DataSource.Live, 100, 80, null, DateTimeOffset.UtcNow)),
            ("estimate", new(DataSource.Estimate, null, null, null, DateTimeOffset.UtcNow)),
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
    private static InstallKind DetectInstallKind() =>
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
