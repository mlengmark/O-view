using OView.App.Platform;

namespace OView.App.Rendering;

/// <summary>
/// Any of the four corners of a work area — unlike <see cref="FlyoutCorner"/>'s three, which
/// only names the corners a flyout auto-docks adjacent to a taskbar. A panel the user dragged
/// can end up nearest any corner, including top-left, so the two types answer different
/// questions and neither should be widened to cover the other.
/// </summary>
public enum WorkAreaCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

/// <summary>
/// A rectangle in device-independent units — <see cref="PixelBox"/>'s counterpart for the one
/// place in <c>Rendering</c> where that matters. A remembered place is a physical location on
/// a display, not a pixel count, and the scale factor can change between the save and the
/// read (a display swapped for one at a different DPI, or Windows' own per-monitor scaling);
/// storing pixels would silently drift the panel on exactly that change.
/// </summary>
public readonly record struct DipBox(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;
}

/// <summary>
/// Which way a keyboard nudge moves the panel. Four directions and nothing else: this is the
/// arrow keys, not a vector, and a head that wanted to move the panel diagonally would be
/// inventing a gesture neither platform offers.
/// </summary>
public enum NudgeDirection
{
    Left,
    Right,
    Up,
    Down,
}

/// <summary>
/// Where a panel the user has dragged is allowed to sit, and the one place both heads ask
/// that question through — the drag-position counterpart to <see cref="WorkAreaPlacement"/>'s
/// auto-docked corner, decided for both heads together for the same reason WorkAreaPlacement
/// states: a Windows user and a Linux user would expect dragging to behave identically.
///
/// <para><b>Two separate moments, two separate rules, and they never run on the same value.</b>
/// <see cref="Resolve"/> answers "where does it go when the panel opens" and always re-clamps
/// the remembered place into the work area as it stands right now, so the whole panel is
/// never left partly off-screen on a display that has since changed resolution or grown a
/// second taskbar. <see cref="Remember"/> answers "what should be stored when the user let go"
/// and snaps the released position flush to an edge or corner it landed within
/// <see cref="SnapThresholdDip"/> of. Because clamping only ever touches a value read back and
/// snapping only ever touches a value about to be saved, a saved place and the current work
/// area cannot actually disagree about which rule applies — there is nothing to order.</para>
///
/// <para><b>Falls back to null, not to a guess.</b> Nothing remembered, a display
/// <paramref name="displayId"/> was never saved for, or a panel that no longer fits the work
/// area at all — <see cref="Resolve"/> returns null in each case, and the caller uses
/// <see cref="WorkAreaPlacement.Place"/> exactly as it did before this existed. That rule
/// already has the right answer for "does not fit"; re-deriving a second one here would be
/// the two rules disagreeing for real.</para>
/// </summary>
public sealed class PanelPlacementMemory(IPanelPositionStore store)
{
    /// <summary>How close to an edge or corner, in DIP, a released place snaps flush to it.</summary>
    public const double SnapThresholdDip = 16;

    /// <summary>
    /// How far one arrow key moves the panel, in DIP — far enough to be worth a keystroke, and
    /// a figure both heads are given rather than work out, so Alt and an arrow key cannot come
    /// to mean two different distances.
    ///
    /// <para>Under <see cref="SnapThresholdDip"/> on purpose: a nudge that could jump the snap
    /// distance in one press would step over the flush position rather than landing on it.</para>
    /// </summary>
    public const double NudgeStepDip = 10;

    /// <summary>
    /// The same key with Shift held: one unit, for placing the panel against something the eye
    /// can see but <see cref="NudgeStepDip"/> steps past.
    /// </summary>
    public const double FineNudgeStepDip = 1;

    /// <summary>Where the panel's top-left corner goes, once the remembered place has been clamped into today's work area.</summary>
    public readonly record struct Resolved(double LeftDip, double TopDip);

    /// <summary>
    /// Where to put a <paramref name="widthDip"/> × <paramref name="heightDip"/> panel on
    /// <paramref name="displayId"/>, or null when the caller should fall back to today's
    /// corner placement instead.
    /// </summary>
    public Resolved? Resolve(string displayId, DipBox workArea, double widthDip, double heightDip)
    {
        if (widthDip > workArea.Width || heightDip > workArea.Height)
        {
            return null;
        }

        if (store.Read(displayId) is not { } place)
        {
            return null;
        }

        var fromRight = place.Corner is WorkAreaCorner.TopRight or WorkAreaCorner.BottomRight;
        var fromBottom = place.Corner is WorkAreaCorner.BottomLeft or WorkAreaCorner.BottomRight;

        // The anchor is the panel's own corner nearest the work area's — e.g. for
        // BottomRight that is the panel's bottom-right point, not its top-left — so the
        // panel's width/height only come off the anchor when that anchor is on the far side.
        var anchorX = fromRight ? workArea.Right - place.OffsetXDip : workArea.X + place.OffsetXDip;
        var anchorY = fromBottom ? workArea.Bottom - place.OffsetYDip : workArea.Y + place.OffsetYDip;

        var left = fromRight ? anchorX - widthDip : anchorX;
        var top = fromBottom ? anchorY - heightDip : anchorY;

        return new Resolved(
            Clamp(left, workArea.X, workArea.Right - widthDip),
            Clamp(top, workArea.Y, workArea.Bottom - heightDip));
    }

    /// <summary>
    /// Records where the panel was released, so the next <see cref="Resolve"/> for
    /// <paramref name="displayId"/> answers with it. The nearest corner is whichever the
    /// panel's own edges sit closest to; the offset from it snaps to flush when that distance
    /// is within <see cref="SnapThresholdDip"/>.
    /// </summary>
    public void Remember(
        string displayId, double leftDip, double topDip, double widthDip, double heightDip, DipBox workArea)
    {
        var distLeft = leftDip - workArea.X;
        var distRight = workArea.Right - (leftDip + widthDip);
        var distTop = topDip - workArea.Y;
        var distBottom = workArea.Bottom - (topDip + heightDip);

        var nearestLeft = distLeft <= distRight;
        var nearestTop = distTop <= distBottom;

        var corner = (nearestLeft, nearestTop) switch
        {
            (true, true) => WorkAreaCorner.TopLeft,
            (false, true) => WorkAreaCorner.TopRight,
            (true, false) => WorkAreaCorner.BottomLeft,
            (false, false) => WorkAreaCorner.BottomRight,
        };

        var offsetX = Snap(nearestLeft ? distLeft : distRight);
        var offsetY = Snap(nearestTop ? distTop : distBottom);

        store.Save(displayId, new PanelPlace(corner, offsetX, offsetY));
    }

    /// <summary>
    /// Moves a panel standing at <paramref name="leftDip"/>, <paramref name="topDip"/> one step
    /// <paramref name="direction"/> — <see cref="NudgeStepDip"/>, or
    /// <see cref="FineNudgeStepDip"/> when <paramref name="fine"/> — and answers where it now
    /// goes, having stored that place.
    ///
    /// <para><b>A nudge is a release at the next place along.</b> The step is added to where the
    /// panel is standing and the result goes through <see cref="Remember"/> and
    /// <see cref="Resolve"/>, which is precisely what letting go of a drag does. So the keyboard
    /// snaps to an edge, clamps into the work area and saves exactly as the pointer does, and
    /// cannot put the panel anywhere a drag could not — not because the two agree today, but
    /// because there is one path and this is it.</para>
    ///
    /// <para>Null means the same as everywhere else here: nothing to place against, so the
    /// caller leaves the panel where it is. Only a panel that no longer fits the work area can
    /// answer that, since the step has just been stored.</para>
    /// </summary>
    public Resolved? Nudge(
        string displayId,
        DipBox workArea,
        double leftDip,
        double topDip,
        double widthDip,
        double heightDip,
        NudgeDirection direction,
        bool fine)
    {
        var step = fine ? FineNudgeStepDip : NudgeStepDip;

        var (dx, dy) = direction switch
        {
            NudgeDirection.Left => (-step, 0.0),
            NudgeDirection.Right => (step, 0.0),
            NudgeDirection.Up => (0.0, -step),
            _ => (0.0, step),
        };

        Remember(displayId, leftDip + dx, topDip + dy, widthDip, heightDip, workArea);
        return Resolve(displayId, workArea, widthDip, heightDip);
    }

    private static double Snap(double distanceDip) =>
        Math.Max(0, distanceDip) <= SnapThresholdDip ? 0 : distanceDip;

    /// <summary>Clamp that yields to <paramref name="min"/> when the range inverts — see <see cref="WorkAreaPlacement"/>'s own copy for why.</summary>
    private static double Clamp(double value, double min, double max) =>
        max < min ? min : Math.Max(min, Math.Min(max, value));
}
