using Avalonia;
using Avalonia.Input;
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

    /// <summary>
    /// The same VM on the fourth run: a 1024×768 screen whose work area is 958×736+66+32, which
    /// is a dock down the left and a top bar. Spelled out separately from
    /// <see cref="WorkArea"/> because every figure B15 reported is in it, and a drop on it
    /// settles to a different place from a drop on a work area starting at the left edge.
    /// </summary>
    private static readonly DipBox FourthRunWorkArea = new(66, 32, 958, 736);

    /// <summary>The panel as that run had it: capped to the work area's height, so it scrolls.</summary>
    private const double FourthRunPanelHeight = 712;

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

    // ── and the open panel goes there, not only the file ────────────────────────────

    /// <summary>
    /// The defect B15 is: the settled place reached the log and the file, and the panel stayed
    /// where it was dropped until the next open. These are the three drops that run reported,
    /// against a window manager that does as it is told — the panel ends at the settled place
    /// with no reopen.
    /// </summary>
    [Theory]
    [InlineData(791, 571, 624, 56)]     // dropped past the bottom-right corner, mostly off-screen
    [InlineData(76, 56, 66, 56)]        // dropped 10 dip from the dock, inside the snap distance
    [InlineData(300, 700, 300, 56)]     // dropped below the bottom edge
    public void ADropEndsAtTheSettledPlaceWhileThePanelIsStillOpen(
        double droppedX, double droppedY, int endsX, int endsY)
    {
        var manager = new StandInWindowManager(new PixelPoint((int)droppedX, (int)droppedY));

        var settled = Drop(manager, droppedX, droppedY);

        Assert.NotNull(settled);
        Assert.Equal(new PixelPoint(endsX, endsY), manager.Position);

        // And it is told once, which is the answer being read back rather than assumed.
        Assert.Equal(1, manager.Requests);
        Assert.Equal(1, manager.LetTheGraceElapse());
        Assert.Equal(new PixelPoint(endsX, endsY), manager.Position);
    }

    /// <summary>
    /// A request made while the window manager still owns the move is discarded in silence — no
    /// move, and no position change to notice the refusal by. That is why the place is read back:
    /// a second request a grace later, once the button is up, lands.
    /// </summary>
    [Fact]
    public void ADropTheWindowManagerRefusesIsAskedForOnceMoreAndThenLands()
    {
        var manager = new StandInWindowManager(new PixelPoint(791, 571), refusals: 1);

        Drop(manager, 791, 571);

        Assert.Equal(new PixelPoint(791, 571), manager.Position);   // the first request vanished
        Assert.Equal(2, manager.LetTheGraceElapse());

        Assert.Equal(new PixelPoint(624, 56), manager.Position);
        Assert.Equal(2, manager.Requests);
    }

    /// <summary>
    /// Every request says in the log what became of it, word for word as a keyboard nudge does —
    /// a reader comparing a drop with a nudge is comparing the same question, and B15 reached a
    /// VM precisely because a drop said nothing.
    /// </summary>
    [Fact]
    public void EveryRequestSaysWhatTheWindowManagerDidWithIt()
    {
        var manager = new StandInWindowManager(new PixelPoint(791, 571), refusals: 1);

        Drop(manager, 791, 571);
        manager.LetTheGraceElapse();

        Assert.Equal(
            [
                "panel position -> 791,571 (requested 624,56; OVERRIDDEN by the window manager)",
                "panel position -> 624,56 (requested 624,56; honoured)",
            ],
            manager.Log);
    }

    /// <summary>
    /// A manager that will not be told is asked twice and then left alone, with the outcome named.
    /// The bound is the point: a panel asking to be moved for as long as it is open would be a
    /// worse fault than one left where the user dropped it, and the place is still stored, so the
    /// next open uses it.
    /// </summary>
    [Fact]
    public void AWindowManagerThatWillNotBeToldIsAskedTwiceAndThenSaidSoAbout()
    {
        var manager = new StandInWindowManager(new PixelPoint(791, 571), refusals: int.MaxValue);

        Drop(manager, 791, 571);
        manager.LetTheGraceElapse();

        Assert.Equal(PanelMoveSettling.MostRequests, manager.Requests);
        Assert.Equal(new PixelPoint(791, 571), manager.Position);
        Assert.False(manager.Settling.AwaitingAnswer);

        Assert.Equal(
            "panel left at 791,571 — 624,56 was asked for 2 times and refused; the next open "
            + "will use it instead",
            manager.Log[^1]);
    }

    /// <summary>
    /// A new move drops a request still waiting on an answer: the panel is being taken somewhere
    /// else, and asking again for where the last drop belonged would fight the gesture in flight.
    /// </summary>
    [Fact]
    public void ANewMoveDropsAPlaceStillWaitingOnAnAnswer()
    {
        var manager = new StandInWindowManager(new PixelPoint(791, 571), refusals: int.MaxValue);
        Drop(manager, 791, 571);

        manager.Settling.Abandon();

        Assert.False(manager.Settling.AwaitingAnswer);
        Assert.False(manager.Settling.ReadBack());
        Assert.Equal(1, manager.Requests);
        Assert.Empty(manager.Log);
    }

    /// <summary>
    /// A release on the fourth run's desktop, asked of <paramref name="manager"/> — the pair of
    /// calls <c>PanelWindow.FinishMove</c> makes, through the one method it makes them with.
    /// </summary>
    private static PanelPlacementMemory.Resolved? Drop(
        StandInWindowManager manager, double droppedX, double droppedY) =>
        PanelWindow.Settle(
            new PanelPlacementMemory(new FakePositions()), DisplayId, FourthRunWorkArea,
            droppedX, droppedY, PanelWidth, FourthRunPanelHeight, manager.Settling);

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

    // ── moving it from the keyboard ─────────────────────────────────────────────────

    /// <summary>
    /// Which keys move the panel: Alt and an arrow key, and Alt with Shift for the fine step.
    /// Nothing else is this panel's to take — a bare arrow key belongs to whatever is scrolling,
    /// and a press this does not claim is left unhandled for the content.
    /// </summary>
    [Theory]
    [InlineData(Key.Left, KeyModifiers.Alt, NudgeDirection.Left, false)]
    [InlineData(Key.Right, KeyModifiers.Alt, NudgeDirection.Right, false)]
    [InlineData(Key.Up, KeyModifiers.Alt, NudgeDirection.Up, false)]
    [InlineData(Key.Down, KeyModifiers.Alt, NudgeDirection.Down, false)]
    [InlineData(Key.Left, KeyModifiers.Alt | KeyModifiers.Shift, NudgeDirection.Left, true)]
    [InlineData(Key.Down, KeyModifiers.Alt | KeyModifiers.Shift, NudgeDirection.Down, true)]
    public void AltAndAnArrowKeyAsksForAStepAndShiftMakesItFine(
        Key key, KeyModifiers modifiers, NudgeDirection direction, bool fine)
    {
        var nudge = PanelWindow.NudgeOf(key, modifiers);

        Assert.NotNull(nudge);
        Assert.Equal(direction, nudge!.Value.Direction);
        Assert.Equal(fine, nudge.Value.Fine);
    }

    [Theory]
    [InlineData(Key.Left, KeyModifiers.None)]
    [InlineData(Key.Left, KeyModifiers.Shift)]
    [InlineData(Key.Left, KeyModifiers.Control)]
    [InlineData(Key.Escape, KeyModifiers.Alt)]
    [InlineData(Key.Tab, KeyModifiers.Alt)]
    public void EveryOtherPressIsNotAStep(Key key, KeyModifiers modifiers) =>
        Assert.Null(PanelWindow.NudgeOf(key, modifiers));

    /// <summary>
    /// A step on this head's own desktop: ten DIP, through the shared rule, saved. The figures
    /// are <c>PanelPlacementMemory</c>'s for both heads — what is asserted here is that this
    /// head asks it rather than working out dips of its own.
    /// </summary>
    [Theory]
    [InlineData(NudgeDirection.Left, false, 290, 120)]
    [InlineData(NudgeDirection.Right, false, 310, 120)]
    [InlineData(NudgeDirection.Up, false, 300, 110)]
    [InlineData(NudgeDirection.Down, false, 300, 130)]
    [InlineData(NudgeDirection.Up, true, 300, 119)]
    [InlineData(NudgeDirection.Down, true, 300, 121)]
    public void AStepGoesThroughTheSharedRuleAndIsSaved(
        NudgeDirection direction, bool fine, double leftDip, double topDip)
    {
        var store = new FakePositions();
        var memory = new PanelPlacementMemory(store);

        // 120 dip down, which is where a 600 dip panel still fits inside a 712 dip work area.
        var nudged = memory.Nudge(
            DisplayId, WorkArea, 300, 120, PanelWidth, PanelHeight, direction, fine);

        Assert.NotNull(nudged);
        Assert.Equal(leftDip, nudged!.Value.LeftDip, precision: 3);
        Assert.Equal(topDip, nudged.Value.TopDip, precision: 3);
        Assert.NotNull(store.Read(DisplayId));
    }

    /// <summary>
    /// The top bar is the edge that matters on this desktop: stepping up into it leaves the
    /// panel flush under the bar rather than behind it, because the step goes through the same
    /// clamp a release does.
    /// </summary>
    [Fact]
    public void AStepUpIntoTheTopBarStopsBeneathIt()
    {
        var memory = new PanelPlacementMemory(new FakePositions());

        var nudged = memory.Nudge(
            DisplayId, WorkArea, 300, WorkArea.Y, PanelWidth, PanelHeight, NudgeDirection.Up,
            fine: false);

        Assert.NotNull(nudged);
        Assert.Equal(WorkArea.Y, nudged!.Value.TopDip, precision: 3);
    }

    /// <summary>
    /// Native Wayland takes the keyboard move away with the drag, and by the same gate: no
    /// store means no <c>PanelPlacementMemory</c>, which is what both the header handle and the
    /// arrow keys are hung on — see <see cref="NativeWaylandRemembersNothing"/> for the store
    /// itself. <see cref="PanelWindow.NudgeOf"/> still names the key, since what a press means
    /// does not depend on the session; what the panel does with it does.
    /// </summary>
    [Fact]
    public void NativeWaylandHasNoKeyboardMoveEither()
    {
        var placement = ClientPlacement.From("wayland", "wayland-0", x11Display: null);

        Assert.Null(PanelWindow.StoreFor(placement, new LinuxPanelPositionStore()));
        Assert.NotNull(PanelWindow.NudgeOf(Key.Left, KeyModifiers.Alt));
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

    /// <summary>
    /// A window manager holding a window, which may be told where to put it and may refuse — the
    /// stand-in for the only part of a drop that needs a real one.
    ///
    /// <para>Refusing in silence is the behaviour worth standing in for: that is what the VM's
    /// manager did to a request made inside its own move grab, and it is why the panel has to
    /// read its position back rather than assume a request took.</para>
    /// </summary>
    private sealed class StandInWindowManager
    {
        private int _refusals;

        public StandInWindowManager(PixelPoint holding, int refusals = 0)
        {
            Position = holding;
            _refusals = refusals;
            Settling = new PanelMoveSettling(() => Position, Request, Log.Add);
        }

        /// <summary>Where the manager is holding the window — what the panel reads back.</summary>
        public PixelPoint Position { get; private set; }

        /// <summary>How many times it has been told, refusals included.</summary>
        public int Requests { get; private set; }

        public List<string> Log { get; } = [];

        public PanelMoveSettling Settling { get; }

        /// <summary>
        /// Ticks the panel's grace until the settling is finished, exactly as
        /// <c>PanelWindow.GraceElapsed</c> does, and answers how many ticks that took.
        ///
        /// <para>Refuses to tick past <see cref="PanelMoveSettling.MostRequests"/>, so a settling
        /// that had come to loop fails here rather than spinning the test run.</para>
        /// </summary>
        public int LetTheGraceElapse()
        {
            var ticks = 0;
            do
            {
                ticks++;
                Assert.True(ticks <= PanelMoveSettling.MostRequests,
                    $"the settling asked for more than {PanelMoveSettling.MostRequests} "
                    + $"read-backs, so it loops: {string.Join(" / ", Log)}");
            }
            while (Settling.ReadBack());

            return ticks;
        }

        private void Request(PixelPoint place)
        {
            Requests++;

            if (_refusals > 0)
            {
                _refusals--;
                return;
            }

            Position = place;
        }
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
