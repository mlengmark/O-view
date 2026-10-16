using System.Runtime.InteropServices;
using OView.App.Platform;
using OView.App.Rendering;
using OView.Core.Storage;
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
///
/// <para><b>Every position this head gives a window comes out of <see cref="Place"/>, and
/// <see cref="Place"/> has exactly two sources: <see cref="PanelPlacementMemory"/> for a panel
/// the user has dragged, and <see cref="WorkAreaPlacement"/> for one that has not.</b> Both are
/// shared with the Linux head, so neither the clamping a remembered place needs nor the snapping
/// a released one needs can be reasoned about twice. <c>WindowsPanelDragWiringTests</c> reads
/// this head's source and fails if a position reaches a window from anywhere else — no test
/// project references this head, so that scan is the only automatic proof there is.</para>
/// </summary>
internal static class PopupPositioner
{
    /// <summary>
    /// Placement for a popup of the given DIP size, plus the corner it ends up anchored to as a
    /// RenderTransformOrigin.
    ///
    /// The corner is returned because the open/close animation has to grow FROM the
    /// docked edge to read as a flyout rather than as a window fading in the middle of
    /// nowhere — and only this method knows which edge that is.
    /// </summary>
    /// <param name="memory">
    /// Where the user last dragged this surface, or null for a surface that cannot be dragged —
    /// the tray menu. Given one, a remembered place for this display wins over the tray corner,
    /// re-clamped by the rule into the work area as it stands now; with nothing remembered the
    /// rule answers null and the corner placement below runs exactly as it did before dragging
    /// existed.
    /// </param>
    public static (double LeftDip, double TopDip, Point Origin) Place(
        PanelPlacementMemory? memory, double widthDip, double heightDip)
    {
        var (mon, work, scale, displayId) = CurrentMonitor();

        if (memory is not null && displayId is not null
            && memory.Resolve(displayId, ToDipBox(work, scale), widthDip, heightDip) is { } remembered)
        {
            return (remembered.LeftDip, remembered.TopDip,
                RememberedOrigin(remembered, widthDip, heightDip, ToDipBox(work, scale)));
        }

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
    /// Records where a surface was let go, so the next <see cref="Place"/> on that display puts
    /// it back there. The rule decides the corner and the snap; this only supplies the display
    /// and its work area, which is the half only Win32 can answer.
    ///
    /// <para>A display the monitor query cannot name is not remembered under a made-up key: the
    /// place would then be restored onto whichever display next failed the same way. Losing the
    /// place is the smaller cost, and the panel opens at the tray corner as it always did.</para>
    /// </summary>
    public static void Remember(
        PanelPlacementMemory memory, double leftDip, double topDip, double widthDip, double heightDip)
    {
        var (_, work, scale, displayId) = CurrentMonitor();
        if (displayId is null)
        {
            return;
        }

        memory.Remember(displayId, leftDip, topDip, widthDip, heightDip, ToDipBox(work, scale));
    }

    /// <summary>
    /// The display the pointer is on, as the remembered place is keyed — or null when the
    /// monitor query fails, which <see cref="Remember"/> treats as "do not remember".
    /// </summary>
    public static string? CurrentDisplayId() => CurrentMonitor().DisplayId;

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
        var (_, work, scale, _) = CurrentMonitor();
        return WorkAreaPlacement.AvailableHeightPx(ToBox(work)) / scale;
    }

    /// <summary>
    /// The monitor the pointer is on, its work area, its effective scale, and the name the
    /// remembered place is keyed by.
    ///
    /// <para>The cursor selects the monitor and nothing else — an undragged flyout docks to a
    /// corner rather than following the pointer, and a dragged one is placed by where it was
    /// left. Falls back to a plausible 1080p work area when the query fails, because a flyout
    /// placed slightly wrong beats one not shown; the display id has no such fallback, because
    /// a wrong key restores someone else's place.</para>
    ///
    /// <para><c>szDevice</c> is the id rather than the monitor rectangle: a rectangle changes
    /// with the resolution, and that is exactly the case a remembered place is supposed to
    /// survive by being re-clamped rather than forgotten.</para>
    /// </summary>
    private static (RECT Monitor, RECT Work, double Scale, string? DisplayId) CurrentMonitor()
    {
        GetCursorPos(out var anchor);
        var monitor = MonitorFromPoint(anchor, MONITOR_DEFAULTTONEAREST);

        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        RECT mon, work;
        string? displayId = null;
        if (GetMonitorInfoW(monitor, ref info))
        {
            mon = info.rcMonitor;
            work = info.rcWork;
            displayId = info.szDevice is { Length: > 0 } device ? device : null;
        }
        else
        {
            mon = work = new RECT { Right = 1920, Bottom = 1040 };
        }

        var scale = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0
            ? dpiX / 96.0
            : 1.0;

        return (mon, work, scale, displayId);
    }

    private static PixelBox ToBox(RECT r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);

    /// <summary>
    /// The same rectangle in device-independent units — what the placement rule works in, since
    /// a remembered place is a physical spot on a display rather than a pixel count
    /// (<see cref="DipBox"/>).
    /// </summary>
    private static DipBox ToDipBox(RECT r, double scale) => new(
        r.Left / scale, r.Top / scale, (r.Right - r.Left) / scale, (r.Bottom - r.Top) / scale);

    /// <summary>
    /// Growth origin for a restored place: the work-area half the panel sits in. A panel dragged
    /// to the top-left has to grow down and right, or the open transition travels away from
    /// where it was left — the same reason the docked corner is returned at all.
    /// </summary>
    private static Point RememberedOrigin(
        PanelPlacementMemory.Resolved place, double widthDip, double heightDip, DipBox work) => new(
        place.LeftDip + widthDip / 2 >= work.X + work.Width / 2 ? 1 : 0,
        place.TopDip + heightDip / 2 >= work.Y + work.Height / 2 ? 1 : 0);

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

    /// <summary>
    /// The EX form, for <c>szDevice</c> — the display name the remembered place is keyed by.
    /// <c>GetMonitorInfoW</c> fills whichever of the two <c>cbSize</c> names, so there is no
    /// reason to keep the shorter one beside it.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(POINT pt, int dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(nint hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint hmonitor, int dpiType, out uint dpiX, out uint dpiY);
}

/// <summary>
/// The file-backed <see cref="IPanelPositionStore"/> the Windows head hands the placement rule:
/// <see cref="PanelPositionFile"/> plus the one translation Core cannot do for itself, between
/// its bare-string corner and <see cref="WorkAreaCorner"/>.
///
/// <para>It sits beside <see cref="PopupPositioner"/> because the two are the same job — the
/// pair of translations that let a shared rule speak to this platform, pixels and DIPs in one
/// and a corner name in the other. A corner the file does not recognise degrades to "nothing
/// remembered", exactly as an unreadable file already does: a place O-view cannot interpret is
/// not a place, and guessing a corner would move the panel somewhere the user never put
/// it.</para>
/// </summary>
internal sealed class FilePanelPositionStore(PanelPositionFile file) : IPanelPositionStore
{
    public FilePanelPositionStore() : this(new PanelPositionFile()) { }

    public PanelPlace? Read(string displayId) =>
        file.Read(displayId) is { } place && Enum.TryParse<WorkAreaCorner>(place.Corner, out var corner)
            ? new PanelPlace(corner, place.OffsetXDip, place.OffsetYDip)
            : null;

    public void Save(string displayId, PanelPlace place) =>
        file.Save(displayId, new PanelPositionFile.Place(
            place.Corner.ToString(), place.OffsetXDip, place.OffsetYDip));

    /// <summary>
    /// Forgets this display's place, so the panel opens at the tray corner again — the menu's
    /// "Reset panel position" row.
    ///
    /// <para>Written as an entry with no corner rather than deleted, because
    /// <see cref="PanelPositionFile"/> has no delete and is not this slice's to change. An empty
    /// corner is already the file's own reading of "nothing remembered" — its <c>Read</c> wants a
    /// non-empty one — so this is the file saying what it already says about a damaged entry,
    /// deliberately, and it survives a restart where a flag in memory would not. A <c>Forget</c>
    /// on the file itself is the better shape; see the PR's Noticed.</para>
    /// </summary>
    public void Forget(string displayId) =>
        file.Save(displayId, new PanelPositionFile.Place("", 0, 0));
}
