using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Threading;
using OView.App;
using OView.App.Platform;
using OView.App.Rendering;
using OView.Core.Models;
using OView.Core.Providers.CachedUsage;
using OView.Core.Providers.Jsonl;
using OView.Core.Providers.PlanHistory;
using OView.Core.Storage;

namespace OView.Linux.Panel;

/// <summary>
/// The detail panel as a window, placed in the work area's corner nearest the panel bar.
///
/// <para><b>Still not a docked flyout, and it never can be.</b> StatusNotifierItem exposes no
/// way to ask where its own icon was drawn — there is no <c>Shell_NotifyIconGetRect</c>
/// equivalent — so anchoring the panel <i>under the icon</i> remains impossible and is not
/// attempted. What ADR-0013 decision 3 asks for is "whatever positioning is genuinely
/// achievable", and a fixed corner is: the work area already excludes the panel bar wherever
/// the desktop publishes it, so the same corner the Windows head docks to can be computed
/// from <c>Screens</c> alone (issue #144).</para>
///
/// <para>The corner rule itself is <see cref="WorkAreaPlacement"/>, shared with the Windows
/// head rather than copied — it is a decision a user would expect both platforms to make the
/// same way.</para>
///
/// <para><b>A request, not a guarantee.</b> Setting <c>Position</c> is an X11 request the
/// window manager may ignore, and a native Wayland compositor will not let a client place
/// itself at all. So the requested and actual positions are both logged: the point of
/// <c>--diagnose</c> is that one round trip answers the question, and "did the corner take?"
/// is exactly the question the next hardware report needs to answer. Where the screen
/// geometry cannot be read at all, this falls back to centring rather than guessing at
/// coordinates — ADR-0013's reasoning, unchanged: a panel half off-screen reads as broken
/// where a plainly-centred one does not.</para>
///
/// <para><b>And the corner is only where it starts.</b> Given somewhere to keep a place, the
/// header becomes a drag handle and the panel reopens where the user put it —
/// <see cref="PanelPlacementMemory"/> decides both the snap on release and the re-clamp on
/// open, shared with the Windows head so dragging cannot come to mean two different things.
/// Given nowhere, which is a native Wayland session (<see cref="ClientPlacement"/>), nothing
/// about this file changes: no handle, no cursor, no stored place, no keyboard move, the same
/// corner as before. The gesture itself is <see cref="PanelDrag"/>, and Alt with an arrow key is
/// <see cref="NudgeOf"/> — the same save through the same rule, without the pointer. What makes
/// the settled place stick once the manager lets go is <see cref="PanelMoveSettling"/>, which has
/// to ask twice.</para>
///
/// <para>Deactivation only dismisses once the panel has actually been focused — see
/// <see cref="PanelDismissal"/> for why an unguarded handler makes a refused activation look
/// exactly like a broken tray icon. A move suppresses it too, for a different reason the
/// handler names.</para>
/// </summary>
public sealed class PanelWindow : Window
{
    private readonly PanelContent _content;
    private readonly PanelDismissal _dismissal = new();
    private readonly PanelDrag _drag = new();
    private readonly IAppLog? _log;

    /// <summary>Where a dragged place is kept, or null for a session that cannot read one back.</summary>
    private readonly IPanelPositionStore? _positions;

    /// <summary>The shared rule over <see cref="_positions"/>. Null exactly when that is.</summary>
    private readonly PanelPlacementMemory? _memory;

    /// <summary>
    /// Waits for the window manager to stop moving the panel, which is the only way the end of
    /// a move is observable — see <see cref="PanelDrag"/>. The same wait then carries the
    /// settling's read-backs, since both are "give the manager a grace and look again".
    /// </summary>
    private readonly DispatcherTimer _settle;

    /// <summary>
    /// Makes the settled place stick, which asking for it once does not —
    /// <see cref="PanelMoveSettling"/> says why. Built for every session, since a head with no
    /// store never reaches a drop to settle.
    /// </summary>
    private readonly PanelMoveSettling _settling;

    /// <summary>The corner we asked for, until the compositor has answered. Null once logged.</summary>
    private PixelPoint? _requestedPosition;

    /// <summary>The press a move is begun from, held until the pointer has travelled far enough.</summary>
    private PointerPressedEventArgs? _press;

    /// <summary>
    /// The height the last fit decided. Kept because a re-placement after a drag or a reset has
    /// no new one to go on, and the window's own frame is not the panel's height on a short
    /// screen — <see cref="FitToScreen"/> says why.
    /// </summary>
    private double _placedHeightDip;

    public PanelWindow(LinuxPanelTheme theme, IAppLog? log = null, IPanelPositionStore? positions = null)
    {
        _content = new PanelContent(theme);
        _log = log;
        _positions = positions;
        _memory = positions is null ? null : new PanelPlacementMemory(positions);

        _settling = new PanelMoveSettling(
            () => Position, place => Position = place, line => _log?.Write(line));

        _settle = new DispatcherTimer { Interval = PanelDrag.SettleGrace };
        _settle.Tick += (_, _) => GraceElapsed();

        Title = "O-view";
        Content = _content;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = false;
        WindowDecorations = WindowDecorations.BorderOnly;
        // Overridden per-open by ShowWith, which centres instead when there is no usable
        // screen geometry to compute a corner from. Manual with no position set lands at
        // (0,0), which is worse than either.
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = theme["PanelBg"];

        // Says whether the corner actually took. A window manager is free to ignore a
        // position request and a native Wayland compositor always will, so the difference
        // between "we asked" and "it happened" has to reach a bug report — no test here can
        // reach a compositor.
        PositionChanged += (_, e) =>
        {
            // Every position reported during a move says the window manager is still moving
            // us, so the wait for it to stop begins again (PanelDrag).
            if (_drag.InFlight)
            {
                RestartSettle();
            }

            if (_requestedPosition is not { } requested)
            {
                return;
            }

            _requestedPosition = null;   // once per placement, not once per nudge
            var honoured = e.Point == requested;
            _log?.Write(
                $"panel position -> {e.Point.X},{e.Point.Y} (requested {requested.X},{requested.Y}; "
                + $"{(honoured ? "honoured" : "OVERRIDDEN by the window manager")})");
        };

        // Dismissal mirrors the Windows panel: Esc, or clicking away. So does moving it from
        // the keyboard, which is the same gesture as the header drag by another route.
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Dismiss("escape");
                return;
            }

            // Marked handled only when the panel actually moved, so every other press — and the
            // same press in a session that can keep no place — reaches the content exactly as it
            // did before.
            if (NudgeOf(e.Key, e.KeyModifiers) is { } nudge && NudgeTo(nudge.Direction, nudge.Fine))
            {
                e.Handled = true;
            }
        };
        Activated += (_, _) => _dismissal.Activated();
        Deactivated += (_, _) =>
        {
            // A move is run by the window manager, which grabs the pointer and can take the
            // activation with it — so without this the panel would dismiss itself the instant
            // it was dragged, which is the whole feature failing. The grace covers the
            // release: the click that ends a move lands wherever the panel now is, and arrives
            // after the manager has given the activation back. Returning here rather than
            // falling through leaves the refused-activation count alone, which is right: a
            // deactivation during a move is neither a click away nor a compositor declining to
            // focus the window.
            if (_drag.SuppressesDismissal(DateTimeOffset.UtcNow))
            {
                return;
            }

            if (_dismissal.ShouldHideOnDeactivated())
            {
                Dismiss("clicked away");
                return;
            }

            // Not a click-away — the compositor refused the activation. Named in the log
            // because "panel opened" alone cannot distinguish this from the #124 deadlock,
            // and the two need completely different fixes.
            _log?.Write(
                "panel deactivated before it was ever focused — dismissal suppressed "
                + $"(x{_dismissal.SuppressedDeactivations}); the window manager declined to activate it");
        };

        // Last, and only where a place can actually be read back: a session that would have to
        // throw the result away shows no handle and no move cursor, rather than an affordance
        // for a gesture that does nothing (rule 6).
        if (_memory is not null)
        {
            MakeHeaderTheDragHandle();
        }
    }

    public void ShowWith(
        UsageSnapshot snapshot,
        PanelStatistics stats,
        ClaudeAccount? account,
        PlanHistoryReport? dataReport,
        TranscriptScopeReport? scopeReport,
        DateTimeOffset utcNow,
        BoostNotices? boostNotices = null,
        CachedUtilization? cachedUsage = null)
    {
        _content.Populate(snapshot, stats, account, dataReport, scopeReport, utcNow, boostNotices,
            cachedUsage: cachedUsage);
        _dismissal.Opening();

        // Decided before Show, applied after it: the corner needs the panel's size, and
        // SizeToContent does not produce one until the window has been laid out. Choosing
        // the startup location up front means a screen we cannot read falls back to Avalonia's
        // own centring rather than to Manual-with-no-position, which is the top-left corner.
        var placeable = TargetScreen() is not null;
        WindowStartupLocation = placeable
            ? WindowStartupLocation.Manual
            : WindowStartupLocation.CenterScreen;

        Show();

        if (placeable)
        {
            // Placed from what the fit decided, not from what the window reports: the corner
            // is derived from the panel's height, and after a resize the compositor has not
            // answered yet.
            //
            // The height rule runs first either way, so a restored place is capped and scrolls
            // exactly as a corner one does: the cap is the work area's own height and does not
            // depend on where in it the panel sits.
            _placedHeightDip = FitToScreen(snapshot, stats, account, dataReport, scopeReport, utcNow,
                boostNotices, cachedUsage);
            Place(_placedHeightDip);
        }

        Activate();
    }

    /// <summary>
    /// Hides the panel and says which gesture did it.
    ///
    /// <para>Hidden rather than closed, as the Windows panel is: the window survives to be shown
    /// again. What that leaves behind — one hidden window, still the application's — is what
    /// made the <i>next</i> open end the process on the lifetime's default shutdown mode (issue
    /// #312, and <see cref="Program"/> for the mode this head starts with).</para>
    ///
    /// <para>Logged because a dismissal is otherwise invisible: nothing else in a report
    /// distinguishes a panel the user put away from one that never came up.</para>
    /// </summary>
    private void Dismiss(string how)
    {
        Hide();
        _log?.Write($"panel hidden ({how})");
    }

    /// <summary>
    /// Makes the panel fit this screen, and answers how tall it now is in DIP.
    ///
    /// <para>Two steps, in this order. First the panel is re-laid out compactly when the screen
    /// is too short for the natural layout (<see cref="PanelDensity"/>), which buys back about
    /// 94 DIP. Then, because compacting is not always enough, <see cref="PanelFit"/> caps the
    /// window at the work area and the content scrolls inside it — nothing is dropped and the
    /// last section stays reachable.</para>
    ///
    /// <para><b>Both steps measure the content, never the window.</b> The panel is
    /// <c>SizeToContent</c>, so its frame is only ever as tall as the compositor granted, and
    /// a compositor will not grant more than the screen: reading it made a 1024×768 desktop
    /// report a natural height of exactly 768 and conclude the panel fitted a 712 DIP work
    /// area, with four sections off the bottom edge and nothing to scroll them back
    /// (Maximilian's report of 2026-10-15). <see cref="PanelContent.NaturalHeightDip"/> asks
    /// the tree instead.</para>
    ///
    /// <para>And it measures <i>again</i> after the compact re-layout, because the tree that
    /// answered first is not the tree now on screen — the cap, the scroll decision and the
    /// corner all have to be about the second one.</para>
    ///
    /// <para>Re-populating rebuilds the whole tree, which is why the compact step only runs
    /// when the panel actually overflows. A display with room is laid out exactly as before.</para>
    /// </summary>
    private double FitToScreen(
        UsageSnapshot snapshot,
        PanelStatistics stats,
        ClaudeAccount? account,
        PlanHistoryReport? dataReport,
        TranscriptScopeReport? scopeReport,
        DateTimeOffset utcNow,
        BoostNotices? boostNotices = null,
        CachedUtilization? cachedUsage = null)
    {
        var natural = _content.NaturalHeightDip();
        if (TargetScreen() is not { } screen)
        {
            return natural;
        }

        var available = WorkAreaPlacement.AvailableHeightPx(ToBox(screen.WorkingArea)) / Scaling(screen);

        var density = PanelDensity.For(natural, available);
        if (density.IsCompact)
        {
            _content.Density = density;
            _content.Populate(snapshot, stats, account, dataReport, scopeReport, utcNow, boostNotices,
                cachedUsage: cachedUsage);
            _log?.Write(
                $"panel density: compact (natural {natural:0} dip > available {available:0} dip)");
        }

        var fit = PanelFit.Resolve(_content.NaturalHeightDip(), available);
        ApplyHeight(fit.WindowHeightDip, fit.MustScroll, available);
        return fit.WindowHeightDip;
    }

    /// <summary>
    /// Makes the window the height the fit decided.
    ///
    /// <para><c>SizeToContent</c> has to give up the height dimension for a capped panel, or
    /// the next layout pass grows the window straight back past the work area;
    /// <c>MaxHeight</c> holds the cap either way, including against a later re-layout this
    /// method never sees. A panel that fits goes back to sizing itself, so moving between a
    /// short screen and a tall one in one session leaves no cap behind.</para>
    ///
    /// <para>Takes the two figures rather than the <see cref="PanelFit"/> answer they came
    /// from: naming that type here would read to this head's own guard against blocking on a
    /// task as a wait on a task's result.</para>
    /// </summary>
    private void ApplyHeight(double heightDip, bool mustScroll, double availableHeightDip)
    {
        MaxHeight = availableHeightDip;
        _content.ScrollContent(mustScroll);

        if (mustScroll)
        {
            SizeToContent = SizeToContent.Width;
            Height = heightDip;
        }
        else
        {
            Height = double.NaN;
            SizeToContent = SizeToContent.WidthAndHeight;
        }

        _log?.Write(mustScroll
            ? $"panel height: capped at {heightDip:0} dip of {availableHeightDip:0} dip "
              + "available — content scrolls"
            : $"panel height: {heightDip:0} dip of {availableHeightDip:0} dip available");
    }

    // ── where the panel goes ────────────────────────────────────────────────────────

    /// <summary>
    /// Puts the panel where the user left it, or at the work-area corner when there is no such
    /// place — <see cref="PanelPlacementMemory"/> answers which, and re-clamps a remembered
    /// place into the work area as it stands at this open.
    ///
    /// <para>Every fallback lands on <see cref="PlaceAtWorkAreaCorner"/> rather than on a
    /// second guess: no store, a display the toolkit cannot name, nothing remembered for it,
    /// or a panel that no longer fits. The corner rule already has the right answer for each,
    /// and this head had no other behaviour before a place could be remembered at all.</para>
    /// </summary>
    private void Place(double panelHeightDip)
    {
        if (_memory is null || TargetScreen() is not { } screen || DisplayId(screen) is not { } displayId)
        {
            PlaceAtWorkAreaCorner(panelHeightDip);
            return;
        }

        if (ForgetPlaceOnOpen)
        {
            ForgetPlaceOnOpen = false;
            Forget(displayId, screen.DisplayName);
        }

        var scaling = Scaling(screen);
        var widthDip = (FrameSize ?? ClientSize).Width;

        if (_memory.Resolve(displayId, ToDipBox(screen.WorkingArea, scaling), widthDip, panelHeightDip)
            is not { } place)
        {
            PlaceAtWorkAreaCorner(panelHeightDip);
            return;
        }

        RequestPosition(place, scaling);
        _log?.Write(
            $"panel placement: where it was left, {place.LeftDip:0},{place.TopDip:0} dip on "
            + $"\"{screen.DisplayName}\" (panel {widthDip:0}x{panelHeightDip:0} dip, scale {scaling:0.##})");
    }

    /// <summary>
    /// Puts the panel back at the work-area corner and forgets where it was dragged to — the
    /// menu's "Reset panel position" row, on a panel that is already open.
    ///
    /// <para>Forgetting first, so a later open reaches the corner rule by the ordinary route
    /// rather than being told where to go. An open panel is also moved now, because the row is
    /// for someone looking at a panel they want back; one that is merely hidden has nothing to
    /// move and will be placed at the corner the next time it is shown.</para>
    /// </summary>
    public void ReturnToCorner()
    {
        if (TargetScreen() is { } screen && DisplayId(screen) is { } displayId)
        {
            Forget(displayId, screen.DisplayName);
        }

        if (IsVisible && _placedHeightDip > 0)
        {
            PlaceAtWorkAreaCorner(_placedHeightDip);
        }
    }

    /// <summary>
    /// Drop the saved place at the next open instead of opening at it — the same menu row,
    /// clicked before any panel existed to forget a place for.
    ///
    /// <para>A place is stored per display, and which display a panel will open on is not
    /// knowable until there is a panel to ask; the next open knows it, and is also the first
    /// moment the reset would be visible to anyone.</para>
    /// </summary>
    public bool ForgetPlaceOnOpen { get; set; }

    private void Forget(string displayId, string? displayName)
    {
        _positions?.Forget(displayId);
        _log?.Write($"panel position forgotten for \"{displayName}\" — back to the work-area corner");
    }

    /// <summary>
    /// Moves the panel to the work-area corner nearest the desktop's panel bar.
    ///
    /// <para>Called after <c>Show</c> because <see cref="Window.SizeToContent"/> leaves the
    /// size unknown until the first layout pass, and the corner is derived from it.</para>
    ///
    /// <para><paramref name="panelHeightDip"/> comes from <see cref="FitToScreen"/> rather than
    /// from the window, for two reasons: the compositor has not answered the resize that was
    /// just asked for, and on a short screen what it answers describes the screen rather than
    /// the panel. Width is still read from the window — nothing has ever capped it.</para>
    /// </summary>
    private void PlaceAtWorkAreaCorner(double panelHeightDip)
    {
        if (TargetScreen() is not { } screen)
        {
            _log?.Write("panel placement skipped — no screen geometry available");
            return;
        }

        // FrameSize includes the border this window draws; ClientSize is the fallback for a
        // compositor that has not reported a frame yet. Both are DIPs, and the work area is
        // device pixels, so the scale has to be applied — the classic HiDPI mistake is to
        // compare the two directly and land a quarter of the way up the screen.
        var size = new Size((FrameSize ?? ClientSize).Width, panelHeightDip);
        var scaling = Scaling(screen);

        var (left, top, corner) = WorkAreaPlacement.Place(
            ToBox(screen.Bounds),
            ToBox(screen.WorkingArea),
            (int)Math.Round(size.Width * scaling),
            (int)Math.Round(size.Height * scaling));

        _requestedPosition = new PixelPoint(left, top);
        Position = _requestedPosition.Value;

        _log?.Write(
            $"panel placement: {corner} at {left},{top} on \"{screen.DisplayName}\" "
            + $"(work {Describe(screen.WorkingArea)}, screen {Describe(screen.Bounds)}, "
            + $"scale {scaling:0.##}, panel {size.Width:0}x{size.Height:0} dip)");
    }

    // ── dragging it somewhere else ──────────────────────────────────────────────────

    /// <summary>
    /// Makes the panel's header the drag handle: the move cursor over it, and a press that
    /// becomes a move once the pointer has travelled <see cref="PanelDrag.ThresholdDip"/>.
    ///
    /// <para><b>The threshold is what keeps the header's own clicks.</b>
    /// <see cref="Window.BeginMoveDrag"/> takes the press event, not a point, and hands the
    /// pointer to the window manager for good — so calling it on the press itself would swallow
    /// every click on the header. Waiting for movement first means a press that does not move is
    /// a press the header's content sees exactly as it did before, which is why these handlers
    /// never mark an event handled and why nothing below the header is touched at all: the
    /// tiles, the Bars/Breakdown switch and the hover cards keep their own gestures.</para>
    ///
    /// <para>The press is held rather than re-created, because <c>BeginMoveDrag</c> will only
    /// take the real thing.</para>
    /// </summary>
    private void MakeHeaderTheDragHandle()
    {
        var handle = _content.Header;
        handle.Cursor = new Cursor(StandardCursorType.SizeAll);

        handle.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                _press = e;
                _drag.Pressed(e.GetPosition(this));
            }
        };

        handle.PointerReleased += (_, _) =>
        {
            _press = null;
            _drag.PressSpent();
        };

        handle.PointerMoved += (_, e) =>
        {
            var point = e.GetCurrentPoint(this);
            if (!_drag.ShouldBegin(point.Position, point.Properties.IsLeftButtonPressed)
                || _press is not { } press)
            {
                return;
            }

            _press = null;
            BeginMove(press);
        };
    }

    /// <summary>
    /// Hands the move to the window manager and starts waiting for it to finish.
    ///
    /// <para>Unlike the Windows head's move loop, this returns at once: the manager owns the
    /// gesture from here, reports where the panel ends up through <c>PositionChanged</c>, and
    /// never says that it has finished. <see cref="PanelDrag"/> states why the wait is the
    /// answer to that.</para>
    /// </summary>
    private void BeginMove(PointerPressedEventArgs press)
    {
        // A place still waiting on an answer is about to be the wrong one: the panel is being
        // taken somewhere else, and the next drop decides where it belongs.
        _settling.Abandon();

        _drag.Began();
        BeginMoveDrag(press);
        RestartSettle();
        _log?.Write("panel move started from the header");
    }

    /// <summary>
    /// The grace has elapsed, which means one of two things in order: the window manager has
    /// gone quiet, so the move is over and the settled place can be asked for, or the request
    /// that followed is due to be read back.
    ///
    /// <para>The grace is re-stamped at every step, so the dismissal window covers the whole
    /// settling rather than just its first tick: a panel that closed itself halfway through
    /// being put where it belongs would be the same defect as one that closed itself when
    /// dragged.</para>
    /// </summary>
    private void GraceElapsed()
    {
        _settle.Stop();
        _drag.Ended(DateTimeOffset.UtcNow);

        if (_settling.AwaitingAnswer ? _settling.ReadBack() : FinishMove())
        {
            _settle.Start();
        }
    }

    /// <summary>
    /// Saves where the move left the panel and asks for the place the shared rule says that is,
    /// so a snap to an edge is visible as soon as the manager lets go rather than only at the
    /// next open. Answers whether that request is now owed a read-back.
    ///
    /// <para>Nothing is stored for a display the toolkit cannot name: the place would then be
    /// restored onto whichever display next failed the same way. Losing it is the smaller cost,
    /// and the panel opens at the corner as it always did — the Windows head refuses the same
    /// thing for the same reason.</para>
    /// </summary>
    private bool FinishMove()
    {
        if (_memory is null || TargetScreen() is not { } screen || DisplayId(screen) is not { } displayId)
        {
            _log?.Write("panel move finished; no place was saved — the display cannot be named");
            return false;
        }

        var scaling = Scaling(screen);
        var widthDip = (FrameSize ?? ClientSize).Width;

        var place = Settle(
            _memory, displayId, ToDipBox(screen.WorkingArea, scaling),
            Position.X / scaling, Position.Y / scaling, widthDip, _placedHeightDip,
            _settling, scaling);

        _log?.Write(
            $"panel moved to {Position.X},{Position.Y} on \"{screen.DisplayName}\" — place saved"
            + (place is { } p ? $", settled at {p.LeftDip:0},{p.TopDip:0} dip" : "; nothing to settle at"));

        return place is not null;
    }

    /// <summary>
    /// What letting go of the panel means: the released place goes through the shared rule,
    /// which snaps it to a nearby edge and stores it, is read straight back so the window and
    /// the stored place agree about where the panel now is, and is then asked of the window
    /// manager through <paramref name="settling"/>.
    ///
    /// <para><b>The asking belongs here</b> rather than at the call site, because a settled place
    /// the window was never actually moved to is the whole of Maximilian's fourth VM run: the log
    /// had the right answer every time and the panel stayed where it was dropped. One method
    /// means a release cannot decide a place without also asking for it.</para>
    ///
    /// <para>Static, and reaching the window only through <paramref name="settling"/>'s
    /// callbacks, because this is the half of a drag that can be proven without a compositor —
    /// <c>PanelDragTests</c> drives it against a stand-in window manager. Everything around it
    /// needs a real one to produce a gesture, and CI has none.</para>
    /// </summary>
    /// <param name="settling">
    /// Where the settled place is asked for, or null to decide it and ask for nothing — which is
    /// the arithmetic on its own.
    /// </param>
    public static PanelPlacementMemory.Resolved? Settle(
        PanelPlacementMemory memory,
        string displayId,
        DipBox workArea,
        double leftDip,
        double topDip,
        double widthDip,
        double heightDip,
        PanelMoveSettling? settling = null,
        double scaling = 1.0)
    {
        memory.Remember(displayId, leftDip, topDip, widthDip, heightDip, workArea);
        var place = memory.Resolve(displayId, workArea, widthDip, heightDip);

        if (place is { } settled)
        {
            settling?.Ask(ToPixels(settled, scaling));
        }

        return place;
    }

    // ── moving it from the keyboard ─────────────────────────────────────────────────

    /// <summary>
    /// The move a key press asks for, or null when it is not one: Alt and an arrow key for a
    /// step, Alt and Shift and an arrow key for a fine one.
    ///
    /// <para>Static and pure, because this is the half of a keyboard move a test can reach —
    /// Avalonia raises no key events without a windowing subsystem either, the same gap
    /// <see cref="PanelDrag"/> names for the pointer.</para>
    ///
    /// <para>Alt is the modifier because a bare arrow key belongs to whatever the panel is
    /// scrolling, and because no desktop this head runs on claims Alt with an arrow key — the
    /// window-moving shortcuts are Alt+F7 and Super with an arrow key. How far each step goes is
    /// not decided here: <see cref="PanelPlacementMemory"/> owns both figures, for both
    /// heads.</para>
    /// </summary>
    public static (NudgeDirection Direction, bool Fine)? NudgeOf(Key key, KeyModifiers modifiers)
    {
        if (!modifiers.HasFlag(KeyModifiers.Alt))
        {
            return null;
        }

        var direction = key switch
        {
            Key.Left => NudgeDirection.Left,
            Key.Right => NudgeDirection.Right,
            Key.Up => NudgeDirection.Up,
            Key.Down => NudgeDirection.Down,
            _ => (NudgeDirection?)null,
        };

        return direction is { } step ? (step, modifiers.HasFlag(KeyModifiers.Shift)) : null;
    }

    /// <summary>
    /// Moves the panel one step and puts it where the shared rule says that place is, answering
    /// whether the key was this panel's to use at all.
    ///
    /// <para>False is every case a drag also refuses: no store, which is a native Wayland
    /// session, a display the toolkit cannot name, a move the window manager is still running,
    /// and a panel that has not been placed yet and so has no position to step from. In each the
    /// press is left for whatever else wants it rather than silently swallowed.</para>
    ///
    /// <para>The step itself goes through <see cref="PanelPlacementMemory.Nudge"/>, which is
    /// <see cref="Settle"/>'s own pair of calls with the step added first — so the keyboard
    /// snaps, clamps and saves exactly as letting go of the header does.</para>
    /// </summary>
    private bool NudgeTo(NudgeDirection direction, bool fine)
    {
        if (_memory is null || _drag.InFlight || _placedHeightDip <= 0
            || TargetScreen() is not { } screen || DisplayId(screen) is not { } displayId)
        {
            return false;
        }

        var scaling = Scaling(screen);

        var place = _memory.Nudge(
            displayId, ToDipBox(screen.WorkingArea, scaling),
            Position.X / scaling, Position.Y / scaling,
            (FrameSize ?? ClientSize).Width, _placedHeightDip, direction, fine);

        if (place is { } nudged)
        {
            RequestPosition(nudged, scaling);
        }

        return true;
    }

    /// <summary>
    /// The store a panel in <paramref name="placement"/> may use: the one given, or none where
    /// no place can be read back. The single point the native-Wayland branch is applied at, so
    /// "nothing is saved there" is one decision rather than a condition at each use.
    /// </summary>
    public static IPanelPositionStore? StoreFor(
        ClientPlacement placement, IPanelPositionStore store) =>
        placement.Allowed ? store : null;

    /// <summary>
    /// Asks for a DIP place in the device pixels <c>Position</c> is given in, and leaves the
    /// answer to the position handler above.
    ///
    /// <para>A drop does not come through here: it goes through <see cref="PanelMoveSettling"/>,
    /// which reads the answer back itself rather than waiting for a position change that a
    /// refused request never produces. An open and a nudge both move a panel the window manager
    /// is not holding, so the change does arrive and the handler is enough.</para>
    /// </summary>
    private void RequestPosition(PanelPlacementMemory.Resolved place, double scaling)
    {
        _requestedPosition = ToPixels(place, scaling);
        Position = _requestedPosition.Value;
    }

    private static PixelPoint ToPixels(PanelPlacementMemory.Resolved place, double scaling) => new(
        (int)Math.Round(place.LeftDip * scaling),
        (int)Math.Round(place.TopDip * scaling));

    private void RestartSettle()
    {
        _settle.Stop();
        _settle.Start();
    }

    /// <summary>
    /// The screen to place on: the one the window is already on where that is knowable, so a
    /// multi-monitor desktop does not always throw the panel onto monitor 1. Neither platform
    /// reports where the tray icon was drawn, so "where the window manager just put it" is the
    /// closest available proxy for where the user is looking.
    /// </summary>
    private Screen? TargetScreen() =>
        Screens.ScreenFromWindow(this) ?? Screens.Primary ?? Screens.All.FirstOrDefault();

    /// <summary>
    /// The key a remembered place is stored under. The display's own name rather than its
    /// rectangle: a rectangle changes with the resolution, and surviving exactly that is what
    /// the remembered place is re-clamped for.
    /// </summary>
    private static string? DisplayId(Screen screen) =>
        screen.DisplayName is { Length: > 0 } name ? name : null;

    private static double Scaling(Screen screen) => screen.Scaling > 0 ? screen.Scaling : 1.0;

    private static PixelBox ToBox(PixelRect r) => new(r.X, r.Y, r.Width, r.Height);

    /// <summary>
    /// The same rectangle in device-independent units — what the shared placement rule works
    /// in, since a remembered place is a physical spot on a display rather than a pixel count
    /// (<see cref="DipBox"/>).
    /// </summary>
    private static DipBox ToDipBox(PixelRect r, double scaling) => new(
        r.X / scaling, r.Y / scaling, r.Width / scaling, r.Height / scaling);

    private static string Describe(PixelRect r) => $"{r.Width}x{r.Height}+{r.X}+{r.Y}";

    /// <summary>Populates without showing — for the offscreen verification renders.</summary>
    internal PanelContent PopulateOnly(
        UsageSnapshot snapshot,
        PanelStatistics stats,
        ClaudeAccount? account,
        PlanHistoryReport? dataReport,
        TranscriptScopeReport? scopeReport,
        DateTimeOffset utcNow,
        BoostNotices? boostNotices = null,
        CachedUtilization? cachedUsage = null)
    {
        _content.Populate(snapshot, stats, account, dataReport, scopeReport, utcNow, boostNotices,
            cachedUsage: cachedUsage);
        return _content;
    }
}

/// <summary>
/// The press-and-move gesture that moves the panel, and the window it has to stay open
/// through — everything about a drag that can be decided without a compositor.
///
/// <para><b>Why the end of a move is a wait.</b> An X11 move is run by the window manager:
/// <c>BeginMoveDrag</c> asks for one and returns at once, the manager takes the pointer, and
/// the client is told where the window ended up by a stream of position changes — and is never
/// told that the stream has finished. The button release goes to the manager's own grab, not to
/// this window, so there is no release event to read. The end of a move is therefore the moment
/// the positions stop arriving, and <see cref="SettleGrace"/> is how long that takes to be sure
/// of. This is the one real difference from the Windows head, whose move loop blocks until the
/// user lets go and so knows the moment exactly — and the reason the settled place has to be
/// asked for more than once (<see cref="PanelMoveSettling"/>): the positions stop when the
/// pointer stops, which can be well before the button comes up and the grab with it.</para>
///
/// <para>The same figure covers the deactivation a move can take with it, for a plainer
/// reason: the panel must not dismiss itself for losing focus to the manager's grab, nor to the
/// click that ends the move.</para>
///
/// <para>Its own class rather than fields on the window, for the reason
/// <see cref="PanelDismissal"/> gives: Avalonia raises no pointer events without a windowing
/// subsystem and CI has none, so a state machine is the only part of a gesture a test can
/// reach.</para>
/// </summary>
public sealed class PanelDrag
{
    /// <summary>
    /// How far the pointer must travel on the header before a press becomes a move, so a click
    /// on the header is still a click and not a one-pixel drag. The figure is
    /// <see cref="PanelPlacementMemory.DragThresholdDip"/>, shared with the Windows head.
    /// </summary>
    public const double ThresholdDip = PanelPlacementMemory.DragThresholdDip;

    /// <summary>
    /// How long without a new position before a move counts as finished, and how long after
    /// that a lost activation is still the move settling rather than the user clicking away.
    /// The figure is <see cref="PanelPlacementMemory.DragSettleGrace"/>, shared with the
    /// Windows head.
    /// </summary>
    public static readonly TimeSpan SettleGrace = PanelPlacementMemory.DragSettleGrace;

    private Point? _pressedAt;
    private DateTimeOffset _endedAt = DateTimeOffset.MinValue;

    /// <summary>Whether the window manager is moving the panel right now.</summary>
    public bool InFlight { get; private set; }

    /// <summary>The handle was pressed at <paramref name="at"/>, in the window's own units.</summary>
    public void Pressed(Point at) => _pressedAt = at;

    /// <summary>Forgets a press that came up without the threshold being passed — a click.</summary>
    public void PressSpent() => _pressedAt = null;

    /// <summary>
    /// Whether <paramref name="at"/> is far enough from the press for this to be a move.
    ///
    /// <para>A pointer travelling with the button up spends the press rather than ignoring it:
    /// the release happened somewhere the handle never saw, and the next press is what starts a
    /// move. Without that, moving the pointer back over the header long after a click elsewhere
    /// would pick the old press up and the panel would leap.</para>
    /// </summary>
    public bool ShouldBegin(Point at, bool leftButtonDown)
    {
        if (!leftButtonDown)
        {
            _pressedAt = null;
            return false;
        }

        if (InFlight || _pressedAt is not { } origin)
        {
            return false;
        }

        return Point.Distance(origin, at) >= ThresholdDip;
    }

    /// <summary>The window manager has taken the move.</summary>
    public void Began()
    {
        _pressedAt = null;
        InFlight = true;
    }

    /// <summary>The positions have stopped arriving, so the move is over as of <paramref name="now"/>.</summary>
    public void Ended(DateTimeOffset now)
    {
        InFlight = false;
        _endedAt = now;
    }

    /// <summary>
    /// Whether a deactivation arriving at <paramref name="now"/> is the move rather than the
    /// user clicking away. True through the move and for <see cref="SettleGrace"/> after it.
    /// </summary>
    public bool SuppressesDismissal(DateTimeOffset now) => InFlight || now - _endedAt < SettleGrace;
}

