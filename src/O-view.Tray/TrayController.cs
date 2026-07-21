using System.Diagnostics;
using System.Windows.Threading;
using OView.Core.Models;
using OView.Core.Providers;
using OView.Tray.Diagnostics;
using OView.Tray.Tray;

namespace OView.Tray;

/// <summary>
/// The polling loop: snapshot → rasterise → tray. Default 60 s — local file reads
/// are cheap (build-plan Phase 3), and the underlying plan-history file only updates
/// every ~300 s anyway. A refresh failure keeps the previous icon; it never crashes
/// the tray.
/// </summary>
public sealed class TrayController : IDisposable
{
    private readonly ITrayHost _host;
    private readonly IUsageProvider _provider;
    private readonly FileLog? _log;
    private readonly DispatcherTimer _timer;

    /// <summary>Most recent snapshot — what the popup opens with.</summary>
    public UsageSnapshot Latest { get; private set; } = UsageSnapshot.None;

    public TrayController(ITrayHost host, IUsageProvider provider, TimeSpan interval, FileLog? log)
    {
        _host = host;
        _provider = provider;
        _log = log;
        _timer = new DispatcherTimer { Interval = interval };
        _timer.Tick += (_, _) => Refresh();
    }

    public void Start()
    {
        Refresh();
        _timer.Start();
    }

    public void Refresh()
    {
        try
        {
            var utcNow = DateTimeOffset.UtcNow;
            var snapshot = _provider.GetSnapshot(utcNow);
            Latest = snapshot;
            var tooltip = TooltipFormatter.Format(snapshot);
            var size = IconRenderer.CurrentIconSize();

            using var bitmap = IconRenderer.Render(size, snapshot, TaskbarTheme.IsLight());
            _host.Update(bitmap, tooltip);

            _log?.Write($"refresh source={snapshot.Source} session={snapshot.SessionPercent?.ToString() ?? "null"} size={size} tooltip=\"{tooltip}\"");
        }
        catch (Exception ex)
        {
            // Keep the previous icon; a monitoring tool must not die on a bad poll.
            _log?.Write($"refresh FAILED {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Phase 3 acceptance self-check: N refreshes with GDI object counts sampled
    /// before and after. A leak shows as growth ≈ N (one handle per refresh).
    /// </summary>
    public void StressTest(int iterations)
    {
        var process = Process.GetCurrentProcess();
        var before = NativeMethods.GetGuiResources(process.Handle, NativeMethods.GR_GDIOBJECTS);
        _log?.Write($"stress start iterations={iterations} gdiBefore={before}");

        for (var i = 0; i < iterations; i++)
        {
            Refresh();
        }

        var after = NativeMethods.GetGuiResources(process.Handle, NativeMethods.GR_GDIOBJECTS);
        _log?.Write($"stress end gdiAfter={after} delta={(long)after - before}");
    }

    public void Dispose() => _timer.Stop();
}
