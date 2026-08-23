using OView.App.Rendering;

namespace OView.App.Tests;

/// <summary>
/// Where a tray flyout goes. This arithmetic shipped on Windows for months with no tests at
/// all — it was welded to <c>GetMonitorInfoW</c> and could not have any. It is now shared with
/// the Linux panel (issue #144), which makes both a regression here a two-platform regression
/// and the rule finally testable.
///
/// <para>Everything is device pixels; see <see cref="WorkAreaPlacement"/> for why.</para>
/// </summary>
public class WorkAreaPlacementTests
{
    private const int Margin = WorkAreaPlacement.DefaultMarginPx;

    private static readonly PixelBox Monitor = new(0, 0, 1920, 1080);

    /// <summary>1920×1080 with a 40 px bar on the named edge.</summary>
    private static PixelBox WorkAreaWithBarAt(string edge) => edge switch
    {
        "top" => new PixelBox(0, 40, 1920, 1040),
        "left" => new PixelBox(40, 0, 1880, 1080),
        "right" => new PixelBox(0, 0, 1880, 1080),
        "bottom" => new PixelBox(0, 0, 1920, 1040),
        _ => Monitor,
    };

    [Fact]
    public void ABottomBarDocksToTheBottomRight()
    {
        var work = WorkAreaWithBarAt("bottom");

        var (left, top, corner) = WorkAreaPlacement.Place(Monitor, work, 300, 500);

        Assert.Equal(FlyoutCorner.BottomRight, corner);
        Assert.Equal(1920 - Margin - 300, left);
        Assert.Equal(1040 - Margin - 500, top);
    }

    [Fact]
    public void ATopBarDocksToTheTopRight()
    {
        var work = WorkAreaWithBarAt("top");

        var (left, top, corner) = WorkAreaPlacement.Place(Monitor, work, 300, 500);

        Assert.Equal(FlyoutCorner.TopRight, corner);
        Assert.Equal(1920 - Margin - 300, left);
        Assert.Equal(40 + Margin, top);
    }

    [Fact]
    public void ALeftBarDocksToTheBottomLeft()
    {
        var work = WorkAreaWithBarAt("left");

        var (left, top, corner) = WorkAreaPlacement.Place(Monitor, work, 300, 500);

        Assert.Equal(FlyoutCorner.BottomLeft, corner);
        Assert.Equal(40 + Margin, left);
        Assert.Equal(1080 - Margin - 500, top);
    }

    /// <summary>
    /// A right-hand bar inset the work area on the right, which is neither of the two edges
    /// the rule tests for — so it takes the default. Correctly: the tray sits at the bottom of
    /// a vertical bar, and the bottom-right corner is the one beside it.
    /// </summary>
    [Fact]
    public void ARightBarTakesTheDefaultBottomRightCorner()
    {
        var work = WorkAreaWithBarAt("right");

        var (left, top, corner) = WorkAreaPlacement.Place(Monitor, work, 300, 500);

        Assert.Equal(FlyoutCorner.BottomRight, corner);
        Assert.Equal(1880 - Margin - 300, left);
        Assert.Equal(1080 - Margin - 500, top);
    }

    /// <summary>An auto-hidden bar leaves no inset at all, so there is nothing to infer from.</summary>
    [Fact]
    public void NoInsetAtAllStillLandsInTheBottomRight()
    {
        var (left, top, corner) = WorkAreaPlacement.Place(Monitor, Monitor, 300, 500);

        Assert.Equal(FlyoutCorner.BottomRight, corner);
        Assert.Equal(1920 - Margin - 300, left);
        Assert.Equal(1080 - Margin - 500, top);
    }

    /// <summary>
    /// The multi-monitor case a naive implementation gets wrong: a secondary display has a
    /// non-zero origin, and the corner has to be that display's, not the desktop's.
    /// </summary>
    [Fact]
    public void ASecondaryMonitorIsPlacedWithinItsOwnBounds()
    {
        var monitor = new PixelBox(1920, 0, 2560, 1440);
        var work = new PixelBox(1920, 0, 2560, 1400);   // 40 px bar along its bottom

        var (left, top, corner) = WorkAreaPlacement.Place(monitor, work, 300, 500);

        Assert.Equal(FlyoutCorner.BottomRight, corner);
        Assert.Equal(1920 + 2560 - Margin - 300, left);
        Assert.Equal(1400 - Margin - 500, top);
        Assert.True(left >= monitor.X, "placed left of its own monitor");
    }

    /// <summary>
    /// A monitor whose origin is negative — a display arranged to the left of the primary.
    /// Nothing here may assume the desktop starts at (0,0).
    /// </summary>
    [Fact]
    public void AMonitorLeftOfThePrimaryIsHandled()
    {
        var monitor = new PixelBox(-1920, -200, 1920, 1080);
        var work = new PixelBox(-1920, -200, 1920, 1040);

        var (left, top, _) = WorkAreaPlacement.Place(monitor, work, 300, 500);

        Assert.Equal(-1920 + 1920 - Margin - 300, left);
        Assert.Equal(-200 + 1040 - Margin - 500, top);
    }

    /// <summary>
    /// A flyout taller than the work area allows. Pinning to the top edge keeps the header and
    /// the numbers on screen; the naive clamp pushes them off the top and leaves the footer.
    /// </summary>
    [Fact]
    public void ASurfaceTallerThanTheWorkAreaPinsToTheTopEdge()
    {
        var work = new PixelBox(0, 0, 1920, 600);

        var (_, top, _) = WorkAreaPlacement.Place(Monitor, work, 300, 5000);

        Assert.Equal(Margin, top);
    }

    [Fact]
    public void ASurfaceWiderThanTheWorkAreaPinsToTheLeftEdge()
    {
        var work = new PixelBox(0, 0, 400, 1080);

        var (left, _, _) = WorkAreaPlacement.Place(Monitor, work, 5000, 300);

        Assert.Equal(Margin, left);
    }

    /// <summary>
    /// The margin is real on every edge the surface touches — the property that keeps a
    /// flyout off the screen edge and off the bar, rather than flush against either.
    /// </summary>
    [Theory]
    [InlineData("top")]
    [InlineData("left")]
    [InlineData("right")]
    [InlineData("bottom")]
    public void TheFlyoutNeverTouchesTheWorkAreaEdge(string edge)
    {
        var work = WorkAreaWithBarAt(edge);

        var (left, top, _) = WorkAreaPlacement.Place(Monitor, work, 300, 500);

        Assert.True(left >= work.X + Margin, $"left edge flush on a {edge} bar");
        Assert.True(top >= work.Y + Margin, $"top edge flush on a {edge} bar");
        Assert.True(left + 300 <= work.Right - Margin, $"right edge flush on a {edge} bar");
        Assert.True(top + 500 <= work.Bottom - Margin, $"bottom edge flush on a {edge} bar");
    }

    /// <summary>
    /// The margin is in device pixels and the caller has already scaled the surface, so a
    /// HiDPI display is just a bigger work area with a bigger surface — no implicit scaling
    /// happens in here. Mixing DIPs and pixels is the error that only shows on hardware
    /// nobody testing has.
    /// </summary>
    [Fact]
    public void NothingIsScaledInternally()
    {
        var monitor = new PixelBox(0, 0, 3840, 2160);
        var work = new PixelBox(0, 0, 3840, 2080);      // 80 px bar at 2x

        var (left, top, _) = WorkAreaPlacement.Place(monitor, work, 600, 1000);

        Assert.Equal(3840 - Margin - 600, left);
        Assert.Equal(2080 - Margin - 1000, top);
    }

    [Fact]
    public void TheMarginIsConfigurableForCallersThatNeedOne()
    {
        var (left, _, _) = WorkAreaPlacement.Place(Monitor, Monitor, 300, 500, marginPx: 40);

        Assert.Equal(1920 - 40 - 300, left);
    }

    // ── available height ────────────────────────────────────────────────────────────

    /// <summary>Both margins come out, so a surface sized to this lands inside the work area.</summary>
    [Fact]
    public void AvailableHeightLeavesRoomForTheMarginAtBothEnds()
    {
        Assert.Equal(1040 - (2 * Margin), WorkAreaPlacement.AvailableHeightPx(WorkAreaWithBarAt("bottom")));
    }

    /// <summary>
    /// The property that makes it the right input for a density decision: a surface of exactly
    /// this height is placed with its top AND its bottom inside the work area, on every bar
    /// edge. Anything taller is what <see cref="PanelDensity"/> exists to shrink.
    /// </summary>
    [Fact]
    public void ASurfaceOfTheAvailableHeightFitsEntirelyInsideTheWorkArea()
    {
        foreach (var edge in new[] { "top", "left", "right", "bottom" })
        {
            var work = WorkAreaWithBarAt(edge);
            var height = WorkAreaPlacement.AvailableHeightPx(work);

            var (_, top, _) = WorkAreaPlacement.Place(Monitor, work, 400, height);

            Assert.True(top >= work.Y, $"{edge}: top {top} above work area {work.Y}");
            Assert.True(top + height <= work.Bottom,
                $"{edge}: bottom {top + height} below work area {work.Bottom}");
        }
    }

    /// <summary>
    /// A work area no larger than its own margins still yields a usable height. A non-positive
    /// constraint is not a smaller window — on a SizeToContent surface it is a layout pass with
    /// no solution.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2 * WorkAreaPlacement.DefaultMarginPx)]
    public void AnAbsurdlyShortWorkAreaStillYieldsAPositiveHeight(int height)
    {
        Assert.True(WorkAreaPlacement.AvailableHeightPx(new PixelBox(0, 0, 1920, height)) >= 1);
    }
}
