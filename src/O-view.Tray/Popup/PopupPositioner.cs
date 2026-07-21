using System.Runtime.InteropServices;

namespace OView.Tray.Popup;

/// <summary>
/// Manual popup placement (ADR-0003 item 2: no NSPopover equivalent exists). The
/// popup docks to a FIXED corner of the work area adjacent to the taskbar — the
/// same placement model as the system flyouts (volume, network, calendar) — so it
/// always opens in the same place regardless of exact click position. The cursor
/// is used only to select the monitor (the one whose tray was clicked); the
/// taskbar edge is derived from where the work area is inset relative to the
/// monitor rect. DIP conversion uses that monitor's effective DPI (PerMonitorV2).
/// </summary>
internal static class PopupPositioner
{
    /// <summary>Placement for a popup of the given DIP size, docked at the tray corner.</summary>
    public static (double LeftDip, double TopDip) Place(double widthDip, double heightDip)
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

        var widthPx = widthDip * scale;
        var heightPx = heightDip * scale;
        const int margin = 12;

        // The tray sits at the right (horizontal taskbars) or bottom (vertical ones)
        // end of the bar, so dock to the work-area corner nearest it. Auto-hide
        // taskbars leave no inset; bottom-right is the Windows 11 default.
        double left, top;
        if (work.Top > mon.Top)                 // taskbar at top
        {
            left = work.Right - margin - widthPx;
            top = work.Top + margin;
        }
        else if (work.Left > mon.Left)          // taskbar at left
        {
            left = work.Left + margin;
            top = work.Bottom - margin - heightPx;
        }
        else                                    // bottom (default), right, or auto-hide
        {
            left = work.Right - margin - widthPx;
            top = work.Bottom - margin - heightPx;
        }

        left = Clamp(left, work.Left + margin, work.Right - margin - widthPx);
        top = Clamp(top, work.Top + margin, work.Bottom - margin - heightPx);

        return (left / scale, top / scale);
    }

    private static double Clamp(double value, double min, double max) =>
        max < min ? min : Math.Max(min, Math.Min(max, value));

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
