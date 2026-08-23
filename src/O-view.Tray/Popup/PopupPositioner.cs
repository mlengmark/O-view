using System.Runtime.InteropServices;
using OView.App.Rendering;
using Point = System.Windows.Point;

namespace OView.Tray.Popup;

/// <summary>
/// Manual popup placement (ADR-0003 item 2: no NSPopover equivalent exists). The
/// popup docks to a FIXED corner of the work area adjacent to the taskbar — the
/// same placement model as the system flyouts (volume, network, calendar) — so it
/// always opens in the same place regardless of exact click position. The cursor
/// is used only to select the monitor (the one whose tray was clicked); the
/// taskbar edge is derived from where the work area is inset relative to the
/// monitor rect. DIP conversion uses that monitor's effective DPI (PerMonitorV2).
///
/// <para><b>Which corner, and the margin, now live in
/// <see cref="WorkAreaPlacement"/></b> — shared with the Linux head, which needed the same
/// answer for its panel (issue #144). What stays here is the Windows-only half: asking
/// <c>GetMonitorInfoW</c> for the two rectangles and converting between pixels and DIPs.
/// The arithmetic moved so there is one copy of it rather than two that drift (issues #55,
/// #56), and it gained the unit tests it could never have while it was welded to
/// P/Invoke.</para>
///
/// <para>The placement is unchanged bar one deliberate difference: the surface's size is
/// rounded to whole device pixels before the corner is computed, where it used to be carried
/// as a fraction. It moves a flyout by at most half a pixel on a fractional-scale display,
/// and a window lands on a pixel boundary regardless — the shared rule works in whole pixels
/// because a work area is only ever reported in them.</para>
/// </summary>
internal static class PopupPositioner
{
    /// <summary>
    /// Placement for a popup of the given DIP size, docked at the tray corner, plus the
    /// corner it ended up docked to as a RenderTransformOrigin.
    ///
    /// The corner is returned because the open/close animation has to grow FROM the
    /// docked edge to read as a flyout rather than as a window fading in the middle of
    /// nowhere — and only this method knows which edge that is.
    /// </summary>
    public static (double LeftDip, double TopDip, Point Origin) Place(double widthDip, double heightDip)
    {
        var (mon, work, scale) = CurrentMonitor();

        // The tray sits at the right (horizontal taskbars) or bottom (vertical ones)
        // end of the bar, so the shared rule docks to the work-area corner nearest it.
        // Auto-hide taskbars leave no inset; bottom-right is the Windows 11 default.
        var (left, top, corner) = WorkAreaPlacement.Place(
            ToBox(mon), ToBox(work),
            (int)Math.Round(widthDip * scale),
            (int)Math.Round(heightDip * scale));

        return (left / scale, top / scale, RenderTransformOrigin(corner));
    }

    /// <summary>
    /// Height available to a flyout on the monitor it is about to open on, in DIPs — the
    /// figure <see cref="PanelDensity.For"/> measures the panel against.
    ///
    /// <para>Per-monitor, not per-desktop: a laptop beside an external display can differ in
    /// both work area and scale factor, and the panel opens on whichever one the tray was
    /// clicked on.</para>
    /// </summary>
    public static double AvailableHeightDip()
    {
        var (_, work, scale) = CurrentMonitor();
        return WorkAreaPlacement.AvailableHeightPx(ToBox(work)) / scale;
    }

    /// <summary>
    /// The monitor whose tray was clicked, its work area, and its effective scale.
    ///
    /// <para>The cursor selects the monitor and nothing else — the flyout docks to a corner
    /// rather than following the pointer. Falls back to a plausible 1080p work area when the
    /// query fails, because a flyout placed slightly wrong beats one not shown.</para>
    /// </summary>
    private static (RECT Monitor, RECT Work, double Scale) CurrentMonitor()
    {
        GetCursorPos(out var anchor);
        var monitor = MonitorFromPoint(anchor, MONITOR_DEFAULTTONEAREST);

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        RECT mon, work;
        if (GetMonitorInfoW(monitor, ref info))
        {
            mon = info.rcMonitor;
            work = info.rcWork;
        }
        else
        {
            mon = work = new RECT { Right = 1920, Bottom = 1040 };
        }

        var scale = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0
            ? dpiX / 96.0
            : 1.0;

        return (mon, work, scale);
    }

    private static PixelBox ToBox(RECT r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);

    /// <summary>
    /// The docked corner as a WPF <c>RenderTransformOrigin</c>, so the open/close animation
    /// grows from the docked edge rather than from the middle of nowhere.
    /// </summary>
    private static Point RenderTransformOrigin(FlyoutCorner corner) => corner switch
    {
        FlyoutCorner.TopRight => new Point(1, 0),      // grows down from the top-right
        FlyoutCorner.BottomLeft => new Point(0, 1),    // grows up from the bottom-left
        _ => new Point(1, 1),                          // grows up from the bottom-right
    };

    private const int MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(POINT pt, int dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(nint hMonitor, ref MONITORINFO lpmi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint hmonitor, int dpiType, out uint dpiX, out uint dpiY);
}
