using System.Diagnostics;
using OView.App;
using OView.App.Platform;
using OView.Core.Models;
using OView.Tray.Tray;

namespace OView.Tray;

/// <summary>
/// The Windows half of the refresh cycle: snapshot → rasterise → tray.
///
/// <para>The polling itself, its cadence and every decision about what the numbers
/// <em>mean</em> now live in <see cref="UsageEngine"/>, which is platform-neutral and
/// tested. What is left here is the part that genuinely cannot leave Windows: rendering the
/// gauge with System.Drawing, reading the taskbar theme from the registry, and handing an
/// <c>HICON</c> to <c>NotifyIcon</c>.</para>
///
/// <para>A rendering failure must not break the poll loop, so it is caught here and logged
/// rather than allowed to escape into the engine's refresh.</para>
/// </summary>
public sealed class TrayController : IDisposable
{
    private readonly ITrayHost _host;
    private readonly UsageEngine _engine;
    private readonly IThemeSource _theme;
    private readonly IAppLog? _log;

    public TrayController(ITrayHost host, UsageEngine engine, IThemeSource theme, IAppLog? log)
    {
        _host = host;
        _engine = engine;
        _theme = theme;
        _log = log;

        _engine.SnapshotUpdated += Render;
        _engine.NotificationRequested += n => _host.ShowNotification(n.Title, n.Message);
    }

    /// <summary>Most recent snapshot — what the popup opens with.</summary>
    public UsageSnapshot Latest => _engine.Latest;

    private void Render(UsageSnapshot snapshot)
    {
        try
        {
            var tooltip = TooltipFormatter.Format(snapshot);
            var size = IconRenderer.CurrentIconSize();

            using var bitmap = IconRenderer.Render(size, snapshot, _theme.IsTrayLight());
            _host.Update(bitmap, tooltip);

            _log?.Write($"icon size={size} tooltip=\"{tooltip}\"");
        }
        catch (Exception ex)
        {
            // Keep the previous icon; a monitoring tool must not die on a bad render.
            _log?.Write($"render FAILED {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Phase 3 acceptance self-check: N refreshes with GDI object counts sampled
    /// before and after. A leak shows as growth ≈ N (one handle per refresh).
    ///
    /// <para>Stays here rather than in the engine: <c>Bitmap.GetHicon()</c> allocates an
    /// unmanaged GDI handle that <c>Icon</c> does not own, and this measures exactly that
    /// (CLAUDE.md rule 5). There is nothing to count on a platform without GDI.</para>
    /// </summary>
    public void StressTest(int iterations)
    {
        var process = Process.GetCurrentProcess();
        var before = NativeMethods.GetGuiResources(process.Handle, NativeMethods.GR_GDIOBJECTS);
        _log?.Write($"stress start iterations={iterations} gdiBefore={before}");

        for (var i = 0; i < iterations; i++)
        {
            _engine.Refresh();
        }

        var after = NativeMethods.GetGuiResources(process.Handle, NativeMethods.GR_GDIOBJECTS);
        _log?.Write($"stress end gdiAfter={after} delta={(long)after - before}");
    }

    public void Dispose() => _engine.SnapshotUpdated -= Render;
}
