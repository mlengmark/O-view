using Avalonia;
using OView.App.Platform;
using OView.App.Rendering;
using OView.Core.Storage;
using OView.Linux.Panel;

namespace OView.Linux.Tests;

/// <summary>
/// Dragging the Linux panel by its header: the gesture that starts a move, the window the
/// panel has to stay open through, what letting go stores, and the session where none of it
/// happens.
///
/// <para><b>What a test on this machine can and cannot reach.</b> Avalonia raises no pointer
/// events without a windowing subsystem and CI has none, so nothing here produces a real drag.
/// What it does reach is every decision the drag is made of: <see cref="PanelDrag"/> for the
/// threshold and the settling, <see cref="PanelWindow.Settle"/> for what a release stores and
/// where the panel then goes, and <see cref="ClientPlacement"/> for whether any of it is
/// offered at all. What is left over is the handful of event subscriptions in
/// <c>PanelWindow.MakeHeaderTheDragHandle</c> and the window manager itself — the same gap
/// <see cref="PanelDismissalTests"/> names, and the reason the <c>linux-runtime</c> job
/// exists.</para>
/// </summary>
public class PanelDragTests
{
    private const string DisplayId = "DP-1";

    /// <summary>The Ubuntu VM's own desktop, and what its work area leaves under a 56 DIP top bar.</summary>
    private static readonly DipBox WorkArea = new(0, 56, 1024, 712);

    private const double PanelWidth = 400;

    private const double PanelHeight = 600;

    // ── a press becomes a move, or stays a click ────────────────────────────────────

    /// <summary>
    /// The threshold is the whole reason the header keeps its own clicks: Avalonia's
    /// <c>BeginMoveDrag</c> hands the pointer to the window manager and never gives it back, so
    /// a press that moved it would swallow every click on the header.
    /// </summary>
    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(3.9, 0, false)]
    [InlineData(0, 3.9, false)]
    [InlineData(4, 0, true)]
    [InlineData(0, -4, true)]
    [InlineData(3, 3, true)]            // 4.24 away, diagonally — a distance, not an axis
    [InlineData(2.8, 2.8, false)]       // 3.96 away: just inside, so still a click
    public void APressBecomesAMoveOnlyOnceThePointerHasTravelledFarEnough(
        double dx, double dy, bool begins)
    {
        var drag = new PanelDrag();
        drag.Pressed(new Point(100, 20));

        Assert.Equal(begins, drag.ShouldBegin(new Point(100 + dx, 20 + dy), leftButtonDown: true));
    }

    [Fact]
    public void APressThatComesUpWithoutMovingLeavesNothingToDragLater()
    {
        var drag = new PanelDrag();
        drag.Pressed(new Point(100, 20));
        drag.PressSpent();

        Assert.False(drag.ShouldBegin(new Point(400, 300), leftButtonDown: true));
    }

    /// <summary>
    /// A pointer travelling with the button up spends the press rather than ignoring it.
    /// Without that, moving back over the header long after a click elsewhere would pick the
    /// old press up and the panel would leap.
    /// </summary>
    [Fact]
    public void APointerMovingWithTheButtonUpIsNotADragAndSpendsThePress()
    {
        var drag = new PanelDrag();
        drag.Pressed(new Point(100, 20));

        Assert.False(drag.ShouldBegin(new Point(400, 300), leftButtonDown: false));
        Assert.False(drag.ShouldBegin(new Point(400, 300), leftButtonDown: true));
    }

    /// <summary>One move at a time: the manager owns the gesture until it stops reporting.</summary>
    [Fact]
    public void AMoveAlreadyInFlightDoesNotStartASecondOne()
    {
        var drag = new PanelDrag();
        drag.Pressed(new Point(100, 20));
        Assert.True(drag.ShouldBegin(new Point(140, 20), leftButtonDown: true));

        drag.Began();

        Assert.True(drag.InFlight);
        Assert.False(drag.ShouldBegin(new Point(200, 20), leftButtonDown: true));
    }

    // ── it stays open while it is moved ─────────────────────────────────────────────

    /// <summary>
    /// The defect this guard exists for: a move takes the pointer, and can take the activation
    /// with it, so an unguarded deactivation dismisses the panel the instant it is dragged.
    /// </summary>
    [Fact]
    public void ADeactivationDuringTheMoveDoesNotDismissThePanel()
    {
        var drag = new PanelDrag();
        drag.Began();

        Assert.True(drag.SuppressesDismissal(DateTimeOffset.UnixEpoch.AddHours(3)));
    }

    /// <summary>
    /// And immediately after it: the click that ends a move lands wherever the panel now is,
    /// and arrives after the manager has given the activation back.
    /// </summary>
    [Fact]
    public void ADeactivationJustAfterTheMoveDoesNotDismissItEither()
    {
        var ended = DateTimeOffset.UnixEpoch.AddHours(3);
        var drag = new PanelDrag();
        drag.Began();
        drag.Ended(ended);

        Assert.False(drag.InFlight);
        Assert.True(drag.SuppressesDismissal(ended + PanelDrag.SettleGrace - TimeSpan.FromMilliseconds(1)));
    }

    /// <summary>
    /// Once the move has settled, clicking away closes the panel as it always did — the guard
    /// is a window, not a switch. A panel that stopped closing on click-away would be the
    /// feature breaking dismissal.
    /// </summary>
    [Fact]
    public void OnceTheMoveHasSettledClickingAwayDismissesThePanelAgain()
    {
        var ended = DateTimeOffset.UnixEpoch.AddHours(3);
        var drag = new PanelDrag();
        drag.Began();
        drag.Ended(ended);

        Assert.False(drag.SuppressesDismissal(ended + PanelDrag.SettleGrace));
    }

    /// <summary>A panel nobody has dragged suppresses nothing at all.</summary>
    [Fact]
    public void APanelThatWasNeverDraggedDismissesNormally()
    {
        Assert.False(new PanelDrag().SuppressesDismissal(DateTimeOffset.UtcNow));
    }

    // ── what letting go stores ──────────────────────────────────────────────────────

    /// <summary>
    /// A release in open space: stored as an offset from the nearest corner, and the panel stays
    /// exactly where it was let go.
    /// </summary>
    [Fact]
    public void AReleaseGoesThroughTheSharedRuleAndIsSaved()
    {
        var store = new FakePositions();
        var memory = new PanelPlacementMemory(store);

        var settled = PanelWindow.Settle(memory, DisplayId, WorkArea, 300, 100, PanelWidth, PanelHeight);

        var place = store.Read(DisplayId);
        Assert.NotNull(place);
        Assert.Equal(WorkAreaCorner.TopLeft, place!.Value.Corner);
        Assert.Equal(300, place.Value.OffsetXDip, precision: 3);
        Assert.Equal(100 - WorkArea.Y, place.Value.OffsetYDip, precision: 3);

        Assert.NotNull(settled);
        Assert.Equal(300, settled!.Value.LeftDip, precision: 3);
        Assert.Equal(100, settled.Value.TopDip, precision: 3);
    }

    /// <summary>
    /// Released near an edge, the shared rule snaps it flush — and because the release reads the
    /// place straight back, the panel moves to the snap rather than only the file knowing about
    /// it.
    /// </summary>
    [Fact]
    public void AReleaseWithinTheSnapDistanceOfAnEdgeEndsUpFlushAgainstIt()
    {
        var memory = new PanelPlacementMemory(new FakePositions());

        var settled = PanelWindow.Settle(
            memory, DisplayId, WorkArea, leftDip: 9, topDip: WorkArea.Y + 4, PanelWidth, PanelHeight);

        Assert.NotNull(settled);
        Assert.Equal(WorkArea.X, settled!.Value.LeftDip, precision: 3);
        Assert.Equal(WorkArea.Y, settled.Value.TopDip, precision: 3);
    }

    /// <summary>
    /// A release the rule has to pull back inside the work area. Nothing a user does with the
    /// pointer can leave the panel partly off-screen.
    /// </summary>
    [Fact]
    public void AReleaseHangingOffTheBottomIsBroughtBackInside()
    {
        var memory = new PanelPlacementMemory(new FakePositions());

        var settled = PanelWindow.Settle(
            memory, DisplayId, WorkArea, leftDip: 500, topDip: 700, PanelWidth, PanelHeight);

        Assert.NotNull(settled);
        Assert.Equal(WorkArea.Bottom - PanelHeight, settled!.Value.TopDip, precision: 3);
        Assert.True(settled.Value.TopDip >= WorkArea.Y, "the panel starts above the work area");
    }

    // ── and what the next open does with it ─────────────────────────────────────────

    /// <summary>
    /// The restore, including the case the per-corner anchoring exists for: the top bar grew, so
    /// the same stored place resolves to a different absolute spot and stays off the bar.
    /// </summary>
    [Fact]
    public void TheNextOpenRestoresThePlaceReClampedToTheWorkAreaAsItStandsThen()
    {
        var store = new FakePositions();
        var memory = new PanelPlacementMemory(store);
        PanelWindow.Settle(memory, DisplayId, WorkArea, 600, 400, PanelWidth, PanelHeight);

        // A dock appeared at the bottom and the bar is taller: 1024x640 of work area, offset 80.
        var shrunk = new DipBox(0, 80, 1024, 640);
        var restored = memory.Resolve(DisplayId, shrunk, PanelWidth, PanelHeight);

        Assert.NotNull(restored);
        Assert.Equal(600, restored!.Value.LeftDip, precision: 3);
        Assert.True(restored.Value.TopDip >= shrunk.Y, "the panel is under the taller top bar");
        Assert.True(
            restored.Value.TopDip + PanelHeight <= shrunk.Bottom, "the panel runs under the dock");
    }

    /// <summary>
    /// A display that is no longer there, and a panel that no longer fits: both resolve to
    /// nothing, which is what sends <c>PanelWindow.Place</c> to the corner rule it used before
    /// a place could be remembered at all.
    /// </summary>
    [Fact]
    public void APlaceThatCannotBeUsedResolvesToNothingSoTheCornerRuleAnswers()
    {
        var memory = new PanelPlacementMemory(new FakePositions());
        PanelWindow.Settle(memory, DisplayId, WorkArea, 600, 400, PanelWidth, PanelHeight);

        Assert.Null(memory.Resolve("a-display-that-left", WorkArea, PanelWidth, PanelHeight));
        Assert.Null(memory.Resolve(DisplayId, WorkArea, PanelWidth, heightDip: 2000));
    }

    // ── the store this head keeps it in ─────────────────────────────────────────────

    /// <summary>
    /// The file store round-trips a place, and forgetting one brings the panel's open back to
    /// the corner — the two halves of the menu's reset row.
    /// </summary>
    [Fact]
    public void TheFileStoreRoundTripsAPlaceAndForgetsIt()
    {
        using var temp = new TempFile();
        var store = new LinuxPanelPositionStore(new PanelPositionFile(temp.Path));

        store.Save(DisplayId, new PanelPlace(WorkAreaCorner.BottomRight, 24, 8));

        var read = store.Read(DisplayId);
        Assert.NotNull(read);
        Assert.Equal(WorkAreaCorner.BottomRight, read!.Value.Corner);
        Assert.Equal(24, read.Value.OffsetXDip, precision: 3);
        Assert.Equal(8, read.Value.OffsetYDip, precision: 3);

        store.Forget(DisplayId);
        Assert.Null(store.Read(DisplayId));
    }

    /// <summary>
    /// A corner the file does not name is "nothing remembered", not a guess. A bare number is
    /// the case worth pinning: <c>Enum.TryParse</c> accepts one, so <c>"7"</c> would otherwise
    /// hand back a corner that does not exist and the panel would be placed against it.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("middle")]
    [InlineData("7")]
    public void ACornerTheFileDoesNotNameIsNotAPlace(string corner)
    {
        using var temp = new TempFile();
        var file = new PanelPositionFile(temp.Path);
        file.Save(DisplayId, new PanelPositionFile.Place(corner, 10, 10));

        Assert.Null(new LinuxPanelPositionStore(file).Read(DisplayId));
    }

    // ── the session where none of this happens ──────────────────────────────────────

    /// <summary>
    /// Native Wayland: a client can neither set nor read its own position, so the panel is
    /// handed no store, nothing is ever written, and the menu row is absent
    /// (<c>LinuxHeadWiringTests</c> asserts the row itself).
    /// </summary>
    [Fact]
    public void NativeWaylandRemembersNothing()
    {
        var placement = ClientPlacement.From("wayland", "wayland-0", x11Display: null);

        Assert.False(placement.Allowed);
        Assert.Null(PanelWindow.StoreFor(placement, new LinuxPanelPositionStore()));
        Assert.Contains("native Wayland", placement.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// XWayland is the other one, and the one Maximilian's Ubuntu VM actually runs: a Wayland
    /// session with an X server behind it, which is what this head's windows talk to. The log
    /// has to say which of the two it decided, because they are indistinguishable from the
    /// user's side and only one of them can remember a place.
    /// </summary>
    [Theory]
    [InlineData("wayland", "wayland-0", ":0", "XWayland")]
    [InlineData(null, "wayland-0", ":1", "XWayland")]
    [InlineData("x11", null, ":0", "X11")]
    [InlineData("tty", null, null, "X11")]
    public void EverySessionWithAnXServerCanRememberAPlaceAndSaysWhyInTheLog(
        string? sessionType, string? waylandDisplay, string? x11Display, string named)
    {
        var store = new LinuxPanelPositionStore();
        var placement = ClientPlacement.From(sessionType, waylandDisplay, x11Display);

        Assert.True(placement.Allowed);
        Assert.Same(store, PanelWindow.StoreFor(placement, store));
        Assert.StartsWith(named, placement.Reason, StringComparison.Ordinal);
    }

    /// <summary>An in-memory store, so the rule's arithmetic is asserted without file I/O.</summary>
    private sealed class FakePositions : IPanelPositionStore
    {
        private readonly Dictionary<string, PanelPlace> _places = [];

        public PanelPlace? Read(string displayId) =>
            _places.TryGetValue(displayId, out var place) ? place : null;

        public void Save(string displayId, PanelPlace place) => _places[displayId] = place;
    }

    /// <summary>A path in a directory of its own, removed with it.</summary>
    private sealed class TempFile : IDisposable
    {
        private readonly string _directory =
            Directory.CreateTempSubdirectory("oview-panel-place-").FullName;

        public string Path => System.IO.Path.Combine(_directory, "panel-position.json");

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
