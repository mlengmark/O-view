using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace TraySpike;

// SPIKE — throwaway. Answers, for O-view issue #75:
//   1. Does Avalonia's TrayIcon accept a LIVE-RENDERED icon (not a themed name),
//      and can it be replaced on a timer? O-view renders a ring gauge every 60 s.
//   2. What happens on a session bus with NO StatusNotifierWatcher — i.e. the
//      GNOME-without-an-extension case, from the app's point of view?
//   3. Does the process stay alive and healthy in that case, or does it fault?
//
// Everything it learns is printed to stdout so a CI job can capture it.

internal static class Program
{
    private static TrayIcon? _tray;
    private static int _ticks;

    public static int Main(string[] args)
    {
        Log($"spike start: OS={Environment.OSVersion} rid-arch={System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}");
        Log($"DISPLAY={Environment.GetEnvironmentVariable("DISPLAY") ?? "(unset)"} " +
            $"WAYLAND_DISPLAY={Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") ?? "(unset)"} " +
            $"DBUS_SESSION_BUS_ADDRESS={(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS") is null ? "(unset)" : "(set)")}");

        // Probed BEFORE Avalonia starts. Doing it from OnFrameworkInitialized deadlocked:
        // blocking the UI thread on a D-Bus round trip hangs the app outright, which is a
        // real constraint for #77 — the host check must not be synchronous on the dispatcher.
        var watcher = HasStatusNotifierWatcherAsync().GetAwaiter().GetResult();
        Log($"PROBE org.kde.StatusNotifierWatcher present = {(watcher?.ToString() ?? "unknown")}");

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log($"FATAL {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<SpikeApp>()
            .UsePlatformDetect()
            .LogToTrace();

    /// <summary>
    /// Can the app tell, itself, whether anything is there to draw its icon? If
    /// TrayIcon reports success either way, this is the only honest signal available —
    /// and rule 6 says O-view must not claim an icon exists when it cannot know.
    /// </summary>
    private static async Task<bool?> HasStatusNotifierWatcherAsync()
    {
        try
        {
            var connection = Tmds.DBus.Protocol.DBusConnection.Session;
            await connection.ConnectAsync();

            var services = await connection.ListServicesAsync();
            var present = services.Contains("org.kde.StatusNotifierWatcher", StringComparer.Ordinal);
            Log($"bus probe: {services.Length} name(s) on the session bus");
            return present;
        }
        catch (Exception ex)
        {
            Log($"bus probe FAILED {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    internal static void OnFrameworkInitialized()
    {
        try
        {
            _tray = new TrayIcon
            {
                Icon = RenderGauge(24, 0.2),
                ToolTipText = "O-view spike 20%",
                IsVisible = true,
            };
            _tray.Clicked += (_, _) => Log("EVENT tray clicked");
            Log("tray icon constructed and IsVisible=true");
        }
        catch (Exception ex)
        {
            // The result that matters: does constructing a tray icon THROW when no
            // StatusNotifierWatcher exists, or does it degrade quietly?
            Log($"TRAY CONSTRUCTION FAILED {ex.GetType().Name}: {ex.Message}");
        }

        // Replace the icon on a timer — O-view re-renders every poll, so a host that
        // caches the first pixmap would be a blocking problem.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        timer.Tick += (_, _) =>
        {
            _ticks++;
            try
            {
                if (_tray is not null)
                {
                    _tray.Icon = RenderGauge(24, 0.2 + (_ticks * 0.1));
                    _tray.ToolTipText = $"O-view spike tick {_ticks}";
                }
                Log($"icon replaced tick={_ticks}");
            }
            catch (Exception ex)
            {
                Log($"ICON REPLACE FAILED tick={_ticks} {ex.GetType().Name}: {ex.Message}");
            }

            if (_ticks >= 5)
            {
                timer.Stop();
                Log($"spike end: survived {_ticks} icon replacements, process healthy");
                (Application.Current?.ApplicationLifetime
                    as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown();
            }
        };
        timer.Start();
    }

    /// <summary>
    /// A ring gauge drawn at runtime into a bitmap — the same shape O-view needs, and the
    /// case that matters: SNI can take a themed icon *name*, which would be useless here.
    /// </summary>
    private static WindowIcon RenderGauge(int size, double fraction)
    {
        var pixel = new PixelSize(size, size);
        using var rtb = new RenderTargetBitmap(pixel, new Vector(96, 96));
        using (var ctx = rtb.CreateDrawingContext())
        {
            var pen = new Pen(Brushes.LimeGreen, size / 8.0);
            var r = (size / 2.0) - (size / 16.0);
            ctx.DrawEllipse(null, pen, new Point(size / 2.0, size / 2.0), r * fraction, r * fraction);
            ctx.DrawEllipse(Brushes.LimeGreen, null, new Point(size / 2.0, size / 2.0), size / 8.0, size / 8.0);
        }

        using var ms = new MemoryStream();
        rtb.Save(ms);
        ms.Position = 0;
        return new WindowIcon(ms);
    }

    internal static void Log(string message) =>
        Console.WriteLine($"[spike] {message}");
}

internal sealed class SpikeApp : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        Program.OnFrameworkInitialized();
        base.OnFrameworkInitializationCompleted();
    }
}
