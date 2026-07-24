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
///
/// On a fresh launch it polls on a short warm-up cadence until authoritative data
/// arrives, then settles to the normal interval — so a launch that beats Claude Desktop
/// to the punch fills the plan bars within seconds instead of up to a minute
/// (<see cref="PollingCadence"/>).
/// </summary>
public sealed class TrayController : IDisposable
{
    /// <summary>Fast retry used while warming up before Desktop has produced data.</summary>
    public static readonly TimeSpan DefaultWarmupInterval = TimeSpan.FromSeconds(3);

    private readonly ITrayHost _host;
    private readonly IUsageProvider _provider;
    private readonly FileLog? _log;
    private readonly DispatcherTimer _timer;
    private readonly TimeSpan _normalInterval;
    private readonly TimeSpan _warmupInterval;
    private DateTimeOffset _startedAt;

    /// <summary>Most recent snapshot — what the popup opens with.</summary>
    public UsageSnapshot Latest { get; private set; } = UsageSnapshot.None;

    /// <summary>Raised after every successful refresh — the notification hook.</summary>
    public event Action<UsageSnapshot>? SnapshotUpdated;

    public TrayController(ITrayHost host, IUsageProvider provider, TimeSpan interval, FileLog? log,
        TimeSpan? warmupInterval = null)
    {
        _host = host;
        _provider = provider;
        _log = log;
        _normalInterval = interval;
        // Never let the warm-up interval exceed the normal one (e.g. a sub-3 s diagnostic
        // --interval-ms), which would make "warming up" slower than steady state.
        _warmupInterval = Min(warmupInterval ?? DefaultWarmupInterval, interval);
        _timer = new DispatcherTimer { Interval = _normalInterval };
        _timer.Tick += (_, _) => Refresh();
    }

    public void Start()
    {
        _startedAt = DateTimeOffset.UtcNow;
        Refresh();  // sets the initial cadence from the first result
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
            SnapshotUpdated?.Invoke(snapshot);
            AdjustCadence(snapshot.Source, utcNow);
        }
        catch (Exception ex)
        {
            // Keep the previous icon; a monitoring tool must not die on a bad poll.
            _log?.Write($"refresh FAILED {ex.GetType().Name}: {ex.Message}");
            // A failed poll produced no authoritative data — keep warming up if we still can.
            AdjustCadence(DataSource.None, DateTimeOffset.UtcNow);
        }
    }

    /// <summary>
    /// Re-times the poll timer for the current data state: fast while still waiting for
    /// authoritative data early in the run, normal once it arrives or the warm-up window
    /// has passed. Only touches the timer when the interval actually changes.
    /// </summary>
    private void AdjustCadence(DataSource source, DateTimeOffset utcNow)
    {
        var next = PollingCadence.Next(source, utcNow - _startedAt, _warmupInterval, _normalInterval);
        if (_timer.Interval != next)
        {
            _timer.Interval = next;
            _log?.Write($"cadence -> {next.TotalSeconds:0.##}s (source={source})");
        }
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a <= b ? a : b;

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
