using System.Windows;
using System.Windows.Interop;
using OView.Tray.Tray;
using Point = System.Windows.Point;

namespace OView.Tray.Popup;

/// <summary>
/// A surface that toggles from the tray icon. Lets the caller complete a toggle without
/// knowing which surface it is holding.
/// </summary>
internal interface IFlyout
{
    bool IsVisible { get; }

    /// <summary>True when this dismissed itself a moment ago — see <see cref="DockedFlyout.ClosedByClickAway"/>.</summary>
    bool ClosedByClickAway { get; }

    void DismissNow();
}

/// <summary>
/// The docked-flyout state machine, shared by the detail panel and the tray menu.
///
/// <para><b>Composed, not inherited.</b> Both hosts are XAML-rooted <see cref="Window"/>s,
/// which subclass awkwardly; the host forwards its <c>Deactivated</c> and Esc here and
/// calls <see cref="Show"/> once its content is populated.</para>
///
/// <para>This existed twice, in two files that did not know about each other, and it is the
/// code with the worst track record in the app: both copies carry comments describing
/// regressions where the panel or the menu sat visible at zero opacity and the tray icon
/// looked permanently dead. <c>--toggle-check</c> and <c>--menu-check</c> exist because a
/// single activation can never catch that class of fault — it only shows on the SECOND
/// open. Two copies meant reasoning about it twice and fixing it once (GitHub issue
/// #54).</para>
///
/// <para>They had already diverged. The menu grew <c>_suppressDeactivate</c> and a pending
/// action queue; the panel got neither. The queue is genuinely menu-only — the panel has no
/// action rows — and stays on <see cref="MenuWindow"/>. The re-entrancy guard is not:
/// hiding a foreground window raises <c>Deactivated</c>, which re-enters the close on ANY
/// host, so it belongs here. See <see cref="BeginClose"/>.</para>
/// </summary>
internal sealed class DockedFlyout(Window host)
{
    /// <summary>
    /// How long after a self-dismissal a tray click still counts as the second half of that
    /// toggle rather than a fresh open.
    ///
    /// <para>A contract with the tray icon, not a tuning knob. Clicking the icon while a
    /// flyout is open deactivates it, so by the time the click arrives the surface is
    /// already closing; treating that click as a re-open made the icon a one-way switch
    /// that could only ever open. Generous enough to cover the deactivate-then-click
    /// ordering and the close transition, short enough that a deliberate second click a
    /// moment later still opens.</para>
    ///
    /// <para>It was a bare literal in both copies, so changing one gave the panel and the
    /// menu different toggle feel.</para>
    /// </summary>
    public static readonly TimeSpan ClickAwayGrace = TimeSpan.FromMilliseconds(400);

    private Point _dockOrigin = new(1, 1);
    private bool _closing;
    private DateTimeOffset _closedAt = DateTimeOffset.MinValue;
    private Action? _pendingAction;
    private bool _suppressDeactivate;

    /// <summary>Disables auto-hide for verification screenshots.</summary>
    public bool PinForVerification { get; set; }

    /// <summary>Whether a close transition is in flight — the state that made menu rows dead.</summary>
    public bool IsClosing => _closing;

    /// <summary>Corner the surface is docked to, so it grows from the tray rather than the middle.</summary>
    public Point DockOrigin => _dockOrigin;

    /// <summary>See <see cref="ClickAwayGrace"/>.</summary>
    public bool ClosedByClickAway => DateTimeOffset.UtcNow - _closedAt < ClickAwayGrace;

    /// <summary>Forward the host's <c>Deactivated</c> here.</summary>
    public void OnDeactivated()
    {
        if (!PinForVerification && !_suppressDeactivate)
        {
            BeginClose();
        }
    }

    /// <summary>
    /// Docks the surface at the tray corner and animates it in.
    ///
    /// <para><c>SizeToContent</c> height is unknown until measured, so the window is laid
    /// out off-screen and then placed; opacity starts at 0 so the off-screen frame never
    /// flashes at the final spot.</para>
    /// </summary>
    /// <param name="afterLayout">
    /// Runs after the first layout pass and before placement. The detail panel draws its
    /// chart here: the graph is a <c>Canvas</c> whose <c>ActualWidth</c> is only known once
    /// the tree has been laid out.
    /// </param>
    /// <param name="takeForeground">
    /// Explicitly take the foreground after showing. The menu needs it — a tray-resident app
    /// owns no activated window, so <c>Activate()</c> alone is not guaranteed, and without
    /// the foreground the flyout never receives the deactivation that dismisses it on an
    /// outside click (issue #11). The panel has always relied on <c>Activate()</c> and does
    /// dismiss correctly, so its behaviour is left alone rather than changed in passing.
    /// </param>
    public void Show(Action? afterLayout = null, bool takeForeground = false)
    {
        // Cancels a close still in flight, so clicking the tray icon again mid-fade brings
        // the surface straight back instead of letting it finish disappearing.
        _closing = false;

        host.Opacity = 0;
        host.Left = -10_000;
        host.Top = -10_000;
        host.Show();
        host.UpdateLayout();

        afterLayout?.Invoke();

        (host.Left, host.Top, _dockOrigin) = PopupPositioner.Place(host.ActualWidth, host.ActualHeight);
        host.Activate();

        if (PinForVerification)
        {
            // Stills need the finished state, not a frame from the middle of a fade.
            FlyoutAnimation.Reset(host);
        }
        else
        {
            FlyoutAnimation.Open(host, _dockOrigin);
        }

        if (takeForeground)
        {
            ForegroundWindow.Take(new WindowInteropHelper(host).Handle);
        }
    }

    /// <summary>
    /// Fades and shrinks back into the docked corner, then hides — and only then runs
    /// <paramref name="after"/>. Waiting the ~110 ms costs nothing perceptible and keeps the
    /// guarantee that an action's balloon or modal never appears behind a still-visible
    /// topmost surface.
    /// </summary>
    public void BeginClose(Action? after = null)
    {
        // A close already in flight must not swallow the action. An early return used to
        // drop it outright, so a row activated while the flyout was mid-close did nothing
        // at all and the click simply vanished — the hardest kind of fault to report,
        // because the menu looks like it worked. Queue it onto the running close instead.
        if (_closing)
        {
            if (after is not null)
            {
                _pendingAction += after;
            }
            return;
        }

        _closing = true;
        _closedAt = DateTimeOffset.UtcNow;
        _pendingAction = after;

        FlyoutAnimation.Close(host, _dockOrigin, () =>
        {
            // Re-checked because the transition is long enough for the tray icon to be
            // clicked again: if the surface was re-opened while this was running, the flag
            // is already clear and hiding now would undo that.
            if (_closing)
            {
                _closing = false;

                // Hiding the foreground window raises Deactivated, which re-enters this
                // method — by which point _closing is already false, so it would start a
                // SECOND close transition on an already-hidden window and, worse, push
                // _closedAt forward by another ~150 ms, silently stretching the toggle's
                // grace window. The menu guarded against this; the panel did not.
                _suppressDeactivate = true;
                try
                {
                    host.Hide();
                }
                finally
                {
                    _suppressDeactivate = false;
                }
            }

            // Taken and cleared before invoking, so an action that reopens the surface does
            // not inherit a stale queue.
            var pending = _pendingAction;
            _pendingAction = null;
            pending?.Invoke();
        });
    }
}
