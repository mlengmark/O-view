using OView.App.Platform;
using OView.App.Rendering;

namespace OView.App.Tests;

/// <summary>
/// The rule for where a dragged panel goes back to. Everything here is device-independent
/// units, since what is stored is a physical location rather than a pixel count — see
/// <see cref="DipBox"/> for why.
/// </summary>
public class PanelPlacementMemoryTests
{
    private const string DisplayId = "display-1";

    private static readonly DipBox FullHd = new(0, 0, 1920, 1080);

    // ── nothing to resolve from ─────────────────────────────────────────────────────

    [Fact]
    public void NothingRemembered_FallsBackToTodaysPlacement()
    {
        var memory = new PanelPlacementMemory(new FakePanelPositionStore());

        Assert.Null(memory.Resolve(DisplayId, FullHd, 300, 500));
    }

    /// <summary>
    /// A laptop's external monitor is unplugged and a different display is now asked about.
    /// The store was never saved to for that id, which is indistinguishable from "gone" at
    /// this layer — the caller supplies whichever ids it currently knows about.
    /// </summary>
    [Fact]
    public void AnUnpluggedDisplay_FallsBackToTodaysPlacement()
    {
        var store = new FakePanelPositionStore();
        var memory = new PanelPlacementMemory(store);
        memory.Remember("the-monitor-that-left", 100, 100, 300, 500, FullHd);

        Assert.Null(memory.Resolve("the-laptop-panel", FullHd, 300, 500));
    }

    /// <summary>
    /// An absent or unreadable file degrades to "nothing stored" inside the store itself
    /// (<c>PanelPositionFileTests</c> covers that), not inside this rule — a store that fails
    /// outright is a defect this class must not hide behind a plausible-looking fallback.
    /// </summary>
    [Fact]
    public void AStoreFailure_IsNotSwallowed()
    {
        var memory = new PanelPlacementMemory(new ThrowingReadStore());

        Assert.Throws<InvalidOperationException>(() => memory.Resolve(DisplayId, FullHd, 300, 500));
    }

    /// <summary>A panel bigger than the whole work area: <see cref="WorkAreaPlacement"/> already has the right answer for this, so remembering nothing lets the caller reach it.</summary>
    [Theory]
    [InlineData(2000, 500)]
    [InlineData(300, 1200)]
    public void APanelThatNoLongerFitsAtAll_FallsBackToTodaysPlacement(double widthDip, double heightDip)
    {
        var store = new FakePanelPositionStore();
        var memory = new PanelPlacementMemory(store);
        memory.Remember(DisplayId, 100, 100, 300, 500, FullHd);

        Assert.Null(memory.Resolve(DisplayId, FullHd, widthDip, heightDip));
    }

    // ── round trip, all four corners ────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 0, WorkAreaCorner.TopLeft)]
    [InlineData(1620, 0, WorkAreaCorner.TopRight)]
    [InlineData(0, 580, WorkAreaCorner.BottomLeft)]
    [InlineData(1620, 580, WorkAreaCorner.BottomRight)]
    public void ARoundTripRemembersTheNearestCorner(double leftDip, double topDip, WorkAreaCorner expected)
    {
        var store = new FakePanelPositionStore();
        var memory = new PanelPlacementMemory(store);

        memory.Remember(DisplayId, leftDip, topDip, 300, 500, FullHd);

        Assert.Equal(expected, store.Read(DisplayId)!.Value.Corner);

        var resolved = memory.Resolve(DisplayId, FullHd, 300, 500);
        Assert.Equal(leftDip, resolved!.Value.LeftDip, precision: 3);
        Assert.Equal(topDip, resolved!.Value.TopDip, precision: 3);
    }

    // ── re-clamping on read (Done-when: resolution change, moved taskbar) ──────────

    /// <summary>The display got smaller after the place was saved; the panel must still be entirely inside it.</summary>
    [Fact]
    public void AResolutionChange_ReClampsInsideTheSmallerWorkArea()
    {
        var store = new FakePanelPositionStore();
        var memory = new PanelPlacementMemory(store);
        memory.Remember(DisplayId, 1620, 580, 300, 500, FullHd);   // bottom-right, on 1920×1080

        var shrunk = new DipBox(0, 0, 1000, 700);
        var resolved = memory.Resolve(DisplayId, shrunk, 300, 500);

        Assert.NotNull(resolved);
        Assert.True(resolved!.Value.LeftDip + 300 <= shrunk.Right, "panel runs off the right edge");
        Assert.True(resolved!.Value.TopDip + 500 <= shrunk.Bottom, "panel runs off the bottom edge");
        Assert.True(resolved!.Value.LeftDip >= shrunk.X && resolved!.Value.TopDip >= shrunk.Y);
    }

    /// <summary>A taskbar now runs along the top, shrinking the work area from above instead of changing its size.</summary>
    [Fact]
    public void ATaskbarThatMoved_ReClampsAgainstTheNewTopEdge()
    {
        var store = new FakePanelPositionStore();
        var memory = new PanelPlacementMemory(store);
        memory.Remember(DisplayId, 100, 0, 300, 500, FullHd);   // flush against the top edge

        var withTopBar = new DipBox(0, 48, 1920, 1032);
        var resolved = memory.Resolve(DisplayId, withTopBar, 300, 500);

        Assert.Equal(48, resolved!.Value.TopDip);
    }

    /// <summary>
    /// The exact Done-when case: a saved place near the bottom of a 768 px work area, where
    /// the whole panel must still show.
    /// </summary>
    [Fact]
    public void ASavedPlaceNearTheBottomOfA768PxWorkArea_StillShowsTheWholePanel()
    {
        var work = new DipBox(0, 0, 1366, 768);
        var store = new FakePanelPositionStore();
        var memory = new PanelPlacementMemory(store);
        memory.Remember(DisplayId, 500, 260, 300, 500, work);   // bottom edge 8 px from the work area's

        var resolved = memory.Resolve(DisplayId, work, 300, 500);

        Assert.NotNull(resolved);
        Assert.True(resolved!.Value.TopDip >= work.Y);
        Assert.True(resolved!.Value.TopDip + 500 <= work.Bottom, "bottom of the panel is cut off");
    }

    // ── snapping on save (Done-when: within 16 px of an edge or corner) ────────────

    [Fact]
    public void ReleasedWithin16Px_SnapsFlushToTheEdge()
    {
        var store = new FakePanelPositionStore();
        var memory = new PanelPlacementMemory(store);

        // 10 px from the left edge, comfortably inside the work area on every other side.
        memory.Remember(DisplayId, 10, 300, 300, 500, FullHd);

        var resolved = memory.Resolve(DisplayId, FullHd, 300, 500);
        Assert.Equal(0, resolved!.Value.LeftDip);
    }

    [Fact]
    public void ReleasedBeyond16Px_DoesNotSnap()
    {
        var store = new FakePanelPositionStore();
        var memory = new PanelPlacementMemory(store);

        memory.Remember(DisplayId, 40, 300, 300, 500, FullHd);

        var resolved = memory.Resolve(DisplayId, FullHd, 300, 500);
        Assert.Equal(40, resolved!.Value.LeftDip, precision: 3);
    }

    /// <summary>Within 16 px of both edges at once snaps flush into the corner itself.</summary>
    [Fact]
    public void ReleasedNearACorner_SnapsFlushOnBothAxes()
    {
        var store = new FakePanelPositionStore();
        var memory = new PanelPlacementMemory(store);

        memory.Remember(DisplayId, 12, 9, 300, 500, FullHd);

        var resolved = memory.Resolve(DisplayId, FullHd, 300, 500);
        Assert.Equal(0, resolved!.Value.LeftDip);
        Assert.Equal(0, resolved!.Value.TopDip);
    }

    // ── moved from the keyboard (Done-when: 10 dip, 1 dip, and the same save) ──────

    /// <summary>
    /// One press of Alt and an arrow key, in each direction. Ten DIP, decided here so neither
    /// head works the figure out for itself.
    /// </summary>
    [Theory]
    [InlineData(NudgeDirection.Left, 490, 300)]
    [InlineData(NudgeDirection.Right, 510, 300)]
    [InlineData(NudgeDirection.Up, 500, 290)]
    [InlineData(NudgeDirection.Down, 500, 310)]
    public void OneStepMovesThePanelTenDip(NudgeDirection direction, double leftDip, double topDip)
    {
        var memory = new PanelPlacementMemory(new FakePanelPositionStore());

        var nudged = memory.Nudge(DisplayId, FullHd, 500, 300, 300, 500, direction, fine: false);

        Assert.NotNull(nudged);
        Assert.Equal(leftDip, nudged!.Value.LeftDip, precision: 3);
        Assert.Equal(topDip, nudged.Value.TopDip, precision: 3);
    }

    /// <summary>And with Shift held, one DIP — the step for placing it against something seen.</summary>
    [Theory]
    [InlineData(NudgeDirection.Left, 499, 300)]
    [InlineData(NudgeDirection.Right, 501, 300)]
    [InlineData(NudgeDirection.Up, 500, 299)]
    [InlineData(NudgeDirection.Down, 500, 301)]
    public void OneFineStepMovesThePanelASingleDip(
        NudgeDirection direction, double leftDip, double topDip)
    {
        var memory = new PanelPlacementMemory(new FakePanelPositionStore());

        var nudged = memory.Nudge(DisplayId, FullHd, 500, 300, 300, 500, direction, fine: true);

        Assert.NotNull(nudged);
        Assert.Equal(leftDip, nudged!.Value.LeftDip, precision: 3);
        Assert.Equal(topDip, nudged.Value.TopDip, precision: 3);
    }

    /// <summary>
    /// Held against an edge, the keyboard cannot push the panel off the work area — the same
    /// clamp a drag release goes through, reached by the same call. Pressing again from where it
    /// now stands leaves it exactly there, so holding the key down does not walk the stored
    /// offset off into the distance.
    /// </summary>
    [Theory]
    [InlineData(0, 300, NudgeDirection.Left)]       // flush against the left edge already
    [InlineData(500, 0, NudgeDirection.Up)]         // and against the top
    public void AStepIntoAnEdgeLeavesThePanelFlushAgainstIt(
        double leftDip, double topDip, NudgeDirection direction)
    {
        var memory = new PanelPlacementMemory(new FakePanelPositionStore());

        var first = memory.Nudge(DisplayId, FullHd, leftDip, topDip, 300, 500, direction, fine: false);
        Assert.NotNull(first);
        Assert.Equal(leftDip, first!.Value.LeftDip, precision: 3);
        Assert.Equal(topDip, first.Value.TopDip, precision: 3);

        var again = memory.Nudge(
            DisplayId, FullHd, first.Value.LeftDip, first.Value.TopDip, 300, 500, direction, fine: false);
        Assert.Equal(leftDip, again!.Value.LeftDip, precision: 3);
        Assert.Equal(topDip, again.Value.TopDip, precision: 3);
    }

    /// <summary>
    /// The other edge, where the clamp has the panel's own size to subtract: a step down from
    /// the bottom of the work area keeps the whole panel on screen.
    /// </summary>
    [Fact]
    public void AStepOffTheBottomKeepsTheWholePanelInsideTheWorkArea()
    {
        var memory = new PanelPlacementMemory(new FakePanelPositionStore());

        var nudged = memory.Nudge(
            DisplayId, FullHd, 500, FullHd.Bottom - 500, 300, 500, NudgeDirection.Down, fine: false);

        Assert.NotNull(nudged);
        Assert.Equal(FullHd.Bottom - 500, nudged!.Value.TopDip, precision: 3);
    }

    /// <summary>
    /// The Done-when in one assertion: what a nudge stores is what a release at the same place
    /// stores. Not two rules that agree today — <c>Nudge</c> adds the step and then makes the
    /// release's own two calls, so a nudge cannot reach a place a drag could not.
    /// </summary>
    [Theory]
    [InlineData(500, 300)]       // open space
    [InlineData(500, 8)]         // inside the snap distance of the top edge
    [InlineData(4, 300)]         // and of the left edge
    public void ANudgeSavesTheSameShapeAReleaseSaves(double leftDip, double topDip)
    {
        var byKeyboard = new FakePanelPositionStore();
        var byPointer = new FakePanelPositionStore();

        var nudged = new PanelPlacementMemory(byKeyboard)
            .Nudge(DisplayId, FullHd, leftDip, topDip, 300, 500, NudgeDirection.Right, fine: false);

        var released = new PanelPlacementMemory(byPointer);
        released.Remember(
            DisplayId, leftDip + PanelPlacementMemory.NudgeStepDip, topDip, 300, 500, FullHd);

        Assert.Equal(byPointer.Read(DisplayId), byKeyboard.Read(DisplayId));
        Assert.Equal(released.Resolve(DisplayId, FullHd, 300, 500), nudged);
    }

    /// <summary>
    /// A panel that no longer fits resolves to nothing, exactly as a remembered place does —
    /// the caller leaves it where it is rather than being handed a position off the screen.
    /// </summary>
    [Fact]
    public void AStepOnAPanelThatNoLongerFits_AnswersNothing()
    {
        var memory = new PanelPlacementMemory(new FakePanelPositionStore());

        Assert.Null(memory.Nudge(DisplayId, FullHd, 0, 0, 300, 1200, NudgeDirection.Down, fine: false));
    }

    /// <summary>A stand-in whose read always throws, so a test can assert <see cref="PanelPlacementMemory"/> does not swallow a failure it has not been told to.</summary>
    private sealed class ThrowingReadStore : IPanelPositionStore
    {
        public PanelPlace? Read(string displayId) => throw new InvalidOperationException("read failed");

        public void Save(string displayId, PanelPlace place) { }
    }
}
