namespace OView.App.Rendering;

/// <summary>
/// A rectangle in device pixels, so the shared placement math owes nothing to a toolkit.
/// Win32's <c>RECT</c> and Avalonia's <c>PixelRect</c> both convert to this in a line.
/// </summary>
public readonly record struct PixelBox(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;
}

/// <summary>Which corner of the work area a flyout ended up docked to.</summary>
public enum FlyoutCorner
{
    TopRight,
    BottomLeft,
    BottomRight,
}

/// <summary>
/// Where a tray flyout goes: a fixed corner of the work area adjacent to the taskbar or
/// panel, the placement model Windows' own volume, network and calendar flyouts use.
///
/// <para><b>This is a shared decision, not a per-platform detail.</b> Which corner, how much
/// margin, and how a too-large surface is clamped are answers a Windows user and a Linux user
/// would expect to be the same — the rule CLAUDE.md's *Intended structure* section states, and
/// the one issues #55 and #56 exist to enforce. It was Win32-only arithmetic inside
/// <c>PopupPositioner</c>; the Linux head needed the same answer (issue #144), and copying it
/// is exactly how the two would drift.</para>
///
/// <para>What stays per-platform is <b>gathering the rectangles</b>: Windows asks
/// <c>GetMonitorInfoW</c>, Linux asks Avalonia's <c>Screens</c>. Neither knows anything about
/// corners.</para>
///
/// <para><b>Everything here is device pixels.</b> Work areas arrive in pixels on both
/// platforms, and mixing them with DIPs is the kind of error that only shows on a HiDPI
/// display nobody testing has. Callers convert their surface's size to pixels first and
/// convert the result back if they need DIPs.</para>
/// </summary>
public static class WorkAreaPlacement
{
    /// <summary>
    /// Gap between the flyout and the work-area edges, in device pixels.
    ///
    /// <para>Pixels rather than DIPs, matching the Windows head's long-standing behaviour: the
    /// gap is therefore visually tighter on a HiDPI display. That is pre-existing and shared
    /// deliberately — changing it here would silently retune the shipped Windows flyouts,
    /// which is a separate decision from giving Linux a corner at all.</para>
    /// </summary>
    public const int DefaultMarginPx = 12;

    /// <summary>
    /// How much height a flyout actually has, once both margins are taken — what
    /// <see cref="PanelDensity.For"/> measures the panel against.
    ///
    /// <para>It does not cap anything. <see cref="Place"/> already handles a surface too
    /// large by pinning it to the top, which keeps the header on screen and is the right
    /// answer to "where"; this is the input to the separate question of whether the surface
    /// should have been that size at all.</para>
    ///
    /// <para>Never returns less than 1: a non-positive height is not a smaller window, it is
    /// a layout pass with no solution.</para>
    /// </summary>
    public static int AvailableHeightPx(PixelBox work, int marginPx = DefaultMarginPx) =>
        Math.Max(1, work.Height - (2 * marginPx));

    /// <summary>
    /// Top-left corner for a flyout of <paramref name="widthPx"/> × <paramref name="heightPx"/>,
    /// plus the corner it docked to.
    ///
    /// <para>The corner is returned because an open/close animation has to grow <i>from</i> the
    /// docked edge to read as a flyout rather than as a window fading in the middle of nowhere,
    /// and only this knows which edge that is.</para>
    ///
    /// <para><paramref name="monitor"/> is the whole display and <paramref name="work"/> is
    /// what is left after the taskbar or panel. The bar's edge is inferred from where the two
    /// differ, because <b>neither platform will say where the tray icon was drawn</b> — Windows
    /// has <c>Shell_NotifyIconGetRect</c> but the flyout deliberately does not follow the
    /// cursor, and StatusNotifierItem has no equivalent at all.</para>
    /// </summary>
    public static (int Left, int Top, FlyoutCorner Corner) Place(
        PixelBox monitor, PixelBox work, int widthPx, int heightPx, int marginPx = DefaultMarginPx)
    {
        int left, top;
        FlyoutCorner corner;

        if (work.Y > monitor.Y)                     // bar along the top
        {
            left = work.Right - marginPx - widthPx;
            top = work.Y + marginPx;
            corner = FlyoutCorner.TopRight;
        }
        else if (work.X > monitor.X)                // bar down the left
        {
            left = work.X + marginPx;
            top = work.Bottom - marginPx - heightPx;
            corner = FlyoutCorner.BottomLeft;
        }
        else                                        // bottom (the default), right, or auto-hidden
        {
            left = work.Right - marginPx - widthPx;
            top = work.Bottom - marginPx - heightPx;
            corner = FlyoutCorner.BottomRight;
        }

        return (
            Clamp(left, work.X + marginPx, work.Right - marginPx - widthPx),
            Clamp(top, work.Y + marginPx, work.Bottom - marginPx - heightPx),
            corner);
    }

    /// <summary>
    /// Clamp that yields to <paramref name="min"/> when the range inverts.
    ///
    /// <para>It inverts whenever the flyout is taller or wider than the work area allows — a
    /// short screen, a large panel, or a scale factor that grew both. Pinning to the
    /// top-left corner keeps the surface's <i>start</i> on screen, which is where the header
    /// and the numbers are; the alternative pushes those off the top and leaves the user
    /// looking at the footer.</para>
    /// </summary>
    private static int Clamp(int value, int min, int max) =>
        max < min ? min : Math.Max(min, Math.Min(max, value));
}
