using System.Diagnostics;
using OView.App;
using OView.App.Platform;
using OView.Core.Models;
using OView.Presentation.Text;
using OView.Tray.Tray;

namespace OView.Tray;

/// <summary>
/// The Windows half of the refresh cycle: snapshot → rasterise → tray.
///
/// <para>The polling itself, its cadence and every decision about what the numbers
/// <em>mean</em> live on the other side of the seam, which is platform-neutral and tested.
/// What is left here is the part that genuinely cannot leave Windows: rendering the gauge with
/// System.Drawing, reading the taskbar theme from the registry, and handing an <c>HICON</c> to
/// <c>NotifyIcon</c>.</para>
///
/// <para>This is the Windows skin: the shell pushes at it through <see cref="IShellToSkin"/>,
/// always on this thread, and it asks back through <see cref="ISkinToShell"/>. The three
/// pushes it does not draw itself arrive as plain callbacks, so the controller holds neither
/// the update flow nor the menu — it stays the thing that paints the icon.</para>
///
/// <para>A rendering failure must not break the poll loop, so it is caught here and logged
/// rather than allowed to escape back into the shell.</para>
/// </summary>
public sealed class TrayController : IShellToSkin
{
    private readonly ITrayHost _host;
    private readonly ISkinToShell _shell;
    private readonly IThemeSource _theme;
    private readonly IAppLog? _log;
    private readonly Action _afterSnapshot;
    private readonly Action _updateCheckDue;
    private readonly Action _rateCheckDue;

    public TrayController(
        ITrayHost host,
        ISkinToShell shell,
        IThemeSource theme,
        IAppLog? log,
        Action afterSnapshot,
        Action updateCheckDue,
        Action rateCheckDue)
    {
        _host = host;
        _shell = shell;
        _theme = theme;
        _log = log;
        _afterSnapshot = afterSnapshot;
        _updateCheckDue = updateCheckDue;
        _rateCheckDue = rateCheckDue;
    }

    /// <summary>Most recent snapshot — what the popup opens with.</summary>
    public UsageSnapshot Latest => _shell.Latest;

    /// <summary>
    /// A new snapshot: the icon first, then whatever else rides this signal. That order is the
    /// one the tray has always had — the gauge reflects the new number before anything talks
    /// about it.
    /// </summary>
    public void ShowSnapshot(UsageSnapshot snapshot)
    {
        Render(snapshot);
        _afterSnapshot();
    }

    public void ShowNotification(AppNotification notification) =>
        _host.ShowNotification(notification.Title, notification.Message, notification.Kind);

    public void UpdateCheckDue() => _updateCheckDue();

    public void RateCheckDue() => _rateCheckDue();

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
    /// <para>Stays here rather than on the shell: <c>Bitmap.GetHicon()</c> allocates an
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
            _shell.Refresh();

            // Rendered from here rather than waited for: the shell now pushes a snapshot
            // through the UI-thread hop, so every posted render would land after the count
            // below and a leak of any size would measure as zero.
            Render(_shell.Latest);
        }

        var after = NativeMethods.GetGuiResources(process.Handle, NativeMethods.GR_GDIOBJECTS);
        _log?.Write($"stress end gdiAfter={after} delta={(long)after - before}");
    }
}
