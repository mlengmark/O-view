using System.Runtime.InteropServices;

namespace OView.Tray.Popup;

/// <summary>
/// Manual popup placement (ADR-0003 item 2: no NSPopover equivalent exists). Anchors
/// near a point — the cursor at click time, which sits on the tray icon — and clamps
/// fully into the work area of the monitor containing that point. Clamping handles
/// all four taskbar edges and secondary monitors by construction: the work area
/// excludes the taskbar wherever it is docked. DIP conversion uses the target
/// monitor's effective DPI (PerMonitorV2).
/// </summary>
internal static class PopupPositioner
{
    /// <summary>Anchor point plus placement for a popup of the given DIP size.</summary>
    public static (double LeftDip, double TopDip) Place(double widthDip, double heightDip)
    {
        GetCursorPos(out var anchor);
        var monitor = MonitorFromPoint(anchor, MONITOR_DEFAULTTONEAREST);

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        var work = GetMonitorInfoW(monitor, ref info)
            ? info.rcWork
            : new RECT { Right = 1920, Bottom = 1040 };

        var scale = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0
            ? dpiX / 96.0
            : 1.0;

        var widthPx = widthDip * scale;
        var heightPx = heightDip * scale;
        const int margin = 8;

        // Horizontal: centred on the anchor, clamped inside the work area.
        var left = Clamp(anchor.X - widthPx / 2, work.Left + margin, work.Right - margin - widthPx);

        // Vertical: open away from the taskbar — above the anchor when it sits in the
        // bottom half (bottom-docked taskbar), below it otherwise. Clamped regardless.
        var workCentre = (work.Top + work.Bottom) / 2.0;
        var top = anchor.Y >= workCentre
            ? anchor.Y - heightPx - 12
            : anchor.Y + 12;
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
