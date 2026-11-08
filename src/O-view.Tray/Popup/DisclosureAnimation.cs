using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using OView.App.Rendering;

namespace OView.Tray.Popup;

/// <summary>
/// How a disclosure's rows move as it folds: when row <c>i</c> starts, how long one fades in,
/// how long they all take to fade out together, and how far one rises into place, in DIP.
/// </summary>
internal sealed record DisclosureRowMotion(
    Func<int, double> StartMs, double FadeMs, double CloseFadeMs, double RiseDip);

/// <summary>
/// The fold that opens and closes the panel's "Why so large?" explanation.
///
/// <para>It used to be a <c>Visibility</c> flip: the body appeared whole, the
/// <c>SizeToContent</c> window jumped to its new height in one frame, and the re-dock moved
/// it to a new position in the same frame. Three simultaneous discontinuities in the one
/// thing on screen that had already been tuned against a frame-by-frame trace of the
/// platform, and the disclosure ignored all of it.</para>
///
/// <para><b>The curve is inherited, not invented.</b> It comes from
/// <see cref="PanelMotion"/>, whose numbers were fitted to a recording of Windows' own
/// Quick Settings flyout, so the app has one idea of "smooth". The durations are the
/// caller's: the panel's fold and the menu's threshold list travel different distances and
/// have different figures.</para>
///
/// <para><b>The rows fade.</b> This was once a pure geometric reveal, with no fade at all, but
/// a hard edge passing through rows at full strength read as unfinished, so the rows now arrive
/// behind that edge and leave ahead of it. The last one finishes after the fold has. Opacity
/// and a render transform take no part in layout, so the fold's measured height is unchanged.</para>
///
/// <para><b>Re-docking per frame is load-bearing.</b> The window is <c>SizeToContent</c> and
/// docked by its top-left, so a fold that grows it without re-placing it pushes the new
/// content down into the taskbar (issue #33), for the whole length of the animation rather
/// than for the single frame the instant version was exposed to. Tracking
/// <c>SizeChanged</c> is what makes the fold read as the panel growing <i>upward</i> out of
/// the docked corner, which is the same direction its entrance travels.</para>
/// </summary>
/// <param name="host">The flyout window, whose height follows the body's.</param>
/// <param name="body">The folding content. Must clip to its bounds, or it spills mid-fold.</param>
/// <param name="chevron">
/// The disclosure arrow's rotation, turned by the same curve. <b>Null where the control
/// carries no arrow</b>, the Bars/Breakdown switch (issue #253) shows its state by which
/// segment is lit, so a chevron there would be a second, redundant indicator.
/// </param>
/// <param name="expandMs">How long the fold takes to open, in milliseconds.</param>
/// <param name="collapseMs">
/// How long it takes to close. Shorter than opening: getting out of the way is not a gesture
/// anyone waits to watch.
/// </param>
/// <param name="rowMotion">How the rows given to <see cref="SetRows"/> move.</param>
/// <param name="reposition">Re-docks the host, called on every frame the height changes.</param>
/// <param name="collapseCurve">The curve of a shrink; null is <see cref="PanelMotion.Accelerate"/>.</param>
internal sealed class DisclosureAnimation(
    Window host, FrameworkElement body, RotateTransform? chevron,
    double expandMs, double collapseMs, DisclosureRowMotion rowMotion, Action reposition,
    PanelMotion.Curve? collapseCurve = null)
{
    /// <summary>Rotation of the chevron in each state. 180° turns the arrow to point back up.</summary>
    private const double ExpandedAngle = 180;

    /// <summary>The spacing of a row's rise frames: one frame of a 60 Hz display.</summary>
    private const double RiseFrameMs = 16;

    /// <summary>One row's cells: a row of a grid is not an element, so they move as a group.</summary>
    private sealed record Row(IReadOnlyList<UIElement> Cells, TranslateTransform Rise);

    private IReadOnlyList<Row> _rows = [];
    private SizeChangedEventHandler? _tracking;
    private DispatcherTimer? _guard;
    private bool _running;
    private int _run;

    /// <summary>
    /// Where the fold is right now, so a click that lands mid-animation continues from what
    /// is on screen instead of snapping to one end and travelling the whole way again.
    ///
    /// <para>Read from the <c>Height</c> property, which returns its animated value:
    /// <c>ActualHeight</c> keeps reporting the content's full height while the body is pinned
    /// shorter. Auto (NaN) means nothing is animating, so the arranged height is the answer.</para>
    /// </summary>
    public (double Height, double Angle) Current => (
        body.Visibility != Visibility.Visible ? 0
            : double.IsNaN(body.Height) ? body.ActualHeight
            : body.Height,
        chevron?.Angle ?? 0);

    /// <summary>Whether the height is on its way somewhere, as distinct from rows still arriving after it.</summary>
    public bool IsMoving => _running;

    /// <summary>Runs when the height comes to rest, for the owner's clip: left on, it moves the text under it.</summary>
    public Action? Settled { get; init; }

    /// <summary>Names the rows to fade, top first. <see cref="Apply"/> gives them their resting state.</summary>
    public void SetRows(IEnumerable<IReadOnlyList<UIElement>> rows)
    {
        _rows = rows.Select(cells => new Row(cells, new TranslateTransform())).ToList();

        foreach (var row in _rows)
        {
            foreach (var cell in row.Cells)
            {
                cell.RenderTransform = row.Rise;
            }
        }
    }

    /// <summary>
    /// Puts the fold in its finished state immediately, with nothing left animating.
    ///
    /// <para>This is the state setter the non-interactive paths use, <c>Populate</c> resets
    /// the disclosure on every open, and the verification renders need a still rather than a
    /// frame from the middle of a transition.</para>
    ///
    /// <para><b>Clearing the animations first is mandatory, not tidiness.</b>
    /// <c>BeginAnimation</c> holds its end value over the local one for as long as it is
    /// attached, and this window is reused across opens, so without the release, assigning
    /// <c>Height</c> or <c>Angle</c> here is silently ignored and the panel opens still
    /// wearing the last fold's geometry.</para>
    /// </summary>
    public void Apply(bool expanded)
    {
        Stop();
        Settle(expanded);
        SettleRows(expanded);
    }

    /// <summary>
    /// Freezes the fold partway through, for a verification render of a state that otherwise
    /// exists only for a fraction of a second on a real desktop.
    ///
    /// <para>Exact rather than approximate: a moment rather than a fraction of the height,
    /// because the rows run on their own clocks. Everything is read from the same curves and
    /// figures <see cref="Run"/> animates, so a rendered frame is a real frame.</para>
    /// </summary>
    /// <param name="atMs">How long after the click, in milliseconds.</param>
    /// <param name="naturalHeight">The body's fully open height, measured by the caller.</param>
    /// <param name="fromHeight">Where the fold began, for a body that was never shut.</param>
    public void ApplyPartial(double atMs, double naturalHeight, double fromHeight = 0)
    {
        Stop();

        var travel = PanelMotion.Decelerate.ValueAt(atMs / expandMs);
        body.Visibility = Visibility.Visible;
        body.Height = fromHeight + (naturalHeight - fromHeight) * travel;
        if (chevron is not null) chevron.Angle = ExpandedAngle * travel;

        for (var i = 0; i < _rows.Count; i++)
        {
            var shown = PanelMotion.Decelerate.ValueAt((atMs - rowMotion.StartMs(i)) / rowMotion.FadeMs);
            Pin(_rows[i], shown, WholePixels(rowMotion.RiseDip * (1 - shown)));
        }
    }

    /// <summary>
    /// Pins the body at the height the fold will start from, before the caller lays the panel
    /// out for its new state.
    ///
    /// <para><b>This is what keeps the window from ever being laid out at the far end of the
    /// fold.</b> Showing the body at its natural height and animating from zero afterwards
    /// looks identical in the visual tree and is not: the intervening layout resizes the
    /// window, which is docked by its top-left, and the compositor presents that HWND before
    /// the re-dock catches up. <c>--fold-check</c>'s screen grabs caught the resulting frame,
    /// a full-height panel 72 px down, its bottom inside the taskbar, which is the exact
    /// failure the docked placement exists to prevent, and it is invisible to any offscreen
    /// render because the visual tree is perfect in it.</para>
    /// </summary>
    public void Prepare(double fromHeight)
    {
        Stop();
        body.Visibility = Visibility.Visible;
        body.Height = fromHeight;
    }

    /// <summary>
    /// The body's height when fully open, measured without letting the window grow to it.
    ///
    /// <para>A child <c>Measure</c> updates that child's <c>DesiredSize</c> and nothing else,
    /// so the answer arrives without a window layout pass, see <see cref="Prepare"/> for why
    /// that distinction is the whole point.</para>
    /// </summary>
    public double MeasureNatural(double availableWidth)
    {
        // Released first: a running fold holds Height over the assignment below, and a click that
        // turns a close round would measure the fold's height instead of the body's.
        body.BeginAnimation(FrameworkElement.HeightProperty, null);

        var height = body.Height;
        var visibility = body.Visibility;

        body.Visibility = Visibility.Visible;
        body.Height = double.NaN;
        body.Measure(new System.Windows.Size(availableWidth, double.PositiveInfinity));
        var natural = body.DesiredSize.Height;

        body.Height = height;
        body.Visibility = visibility;
        return natural;
    }

    /// <summary>
    /// Folds to <paramref name="expanded"/>, starting from where the fold currently is.
    /// </summary>
    /// <param name="toHeight">
    /// The body's height when open, measured by the caller, it has already laid the panel
    /// out at its final size to re-decide the density, so measuring again here would be a
    /// second full layout pass for a number it is holding.
    /// </param>
    public void Run(bool expanded, double fromHeight, double fromAngle, double toHeight)
    {
        // Less the margin: the measure includes it and Height does not, so aiming at the measure
        // overshoots and the panel drops back in one frame when the fold lets go.
        var moved = Move(
            expanded, growing: expanded, fromHeight, fromAngle,
            Math.Max(0, toHeight - body.Margin.Top - body.Margin.Bottom));

        if (moved)
        {
            RunRows(expanded);
        }
        else
        {
            SettleRows(expanded);
        }
    }

    /// <summary>
    /// Moves a body that stays open from one height to another: growing as a fold opens, shrinking
    /// as one closes, and auto-sized again at the end. The rows are the caller's to start, because
    /// the table's leave before its height moves and arrive with it.
    /// </summary>
    /// <param name="toHeight">The body's own height at the far end, without its margin.</param>
    public void Resize(double fromHeight, double toHeight) =>
        Move(endsOpen: true, growing: toHeight >= fromHeight, fromHeight, chevron?.Angle ?? 0, toHeight);

    /// <summary>The rows fade away from wherever they have got to, all together.</summary>
    public void LeaveRows() => RunRows(expanded: false);

    /// <summary>Holds the rows named by <see cref="SetRows"/> faded out and below their places, ready to arrive.</summary>
    public void ParkRows() => SettleRows(expanded: false);

    /// <summary>The rows fade and rise into place, one after another, from wherever they have got to.</summary>
    public void ArriveRows() => RunRows(expanded: true);

    /// <summary>Whether the height is animating; false when Windows' animation setting skipped it.</summary>
    private bool Move(bool endsOpen, bool growing, double fromHeight, double fromAngle, double toHeight)
    {
        Stop();

        var durationMs = growing ? expandMs : collapseMs;
        var duration = WpfMotion.Duration(durationMs);
        var spline = WpfMotion.Spline(growing ? PanelMotion.Decelerate : collapseCurve ?? PanelMotion.Accelerate);

        // Visible throughout, in both directions: collapsed content has no height to animate,
        // so a fold that closes has to stay on screen until it has finished closing.
        body.Visibility = Visibility.Visible;
        body.Height = fromHeight;

        _running = true;

        // Windows' animation setting off: the same end state and the same re-docks, in one step.
        if (!PanelMotion.Runs(WpfMotion.AnimationsOn, durationMs))
        {
            Finish(endsOpen);
            return false;
        }

        _tracking = (_, _) => reposition();
        host.SizeChanged += _tracking;

        var height = Spline(fromHeight, toHeight, duration, spline);

        // Completed MUST be attached before BeginAnimation. WPF takes its own copy of the
        // timeline when the animation starts, so a handler added afterwards is attached to an
        // object nothing will ever raise, the exact mistake that once left the flyout
        // visible at zero opacity with a dead tray icon (see FlyoutAnimation.Close).
        //
        // And only this run's: a fold replaced by a second click still reports completion at
        // its original time, which would finish the new fold in the state the old one wanted.
        var run = ++_run;
        height.Completed += (_, _) =>
        {
            if (run == _run)
            {
                Finish(endsOpen);
            }
        };
        body.BeginAnimation(FrameworkElement.HeightProperty, height);

        chevron?.BeginAnimation(RotateTransform.AngleProperty,
            Spline(fromAngle, endsOpen ? ExpandedAngle : 0, duration, spline));

        // Backstop, for the same reason the close transition has one: the symptom of a missed
        // completion here is a panel stuck at a fixed height that no longer grows with its
        // own content, and a SizeChanged handler re-docking the window for the rest of the
        // session. Firing twice is harmless, Finish is idempotent.
        _guard = new DispatcherTimer
        {
            Interval = duration.TimeSpan + TimeSpan.FromMilliseconds(150),
        };
        _guard.Tick += (_, _) => Finish(endsOpen);
        _guard.Start();
        return true;
    }

    private void Finish(bool expanded)
    {
        if (!_running)
        {
            return;
        }

        _running = false;

        // Releases the animations and restores auto height, so the body goes back to sizing
        // itself, a panel whose text later rewraps to three lines must not stay pinned to
        // the height this fold happened to end on.
        Stop();
        Settle(expanded);
        Settled?.Invoke();

        // Not the rows on the way open: the last of them finishes after the fold does, and their
        // animations end by holding the resting values. Closed, the next open needs them waiting
        // below their places.
        if (!expanded)
        {
            SettleRows(false);
        }

        // Laid out before it is re-docked, so the dock is computed from the height the panel
        // has FINISHED at rather than from the last animated frame, the fold's last step is
        // its largest on the way out, so a stale one leaves the panel visibly off its edge.
        host.UpdateLayout();
        reposition();

        // And again once the layout that Apply just invalidated has actually run. A
        // SizeToContent window's ActualHeight trails the pass that produced it, so the
        // re-dock above places the panel for the height it had a frame ago, measured, not
        // supposed: --fold-check caught a collapse settling 13 px above its docked edge and
        // staying there, because the last fold frame was the number the dock was computed
        // from. Loaded priority is the first point after layout, and re-docking an
        // already-correct window costs one SetWindowPos.
        host.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, reposition);
    }

    /// <summary>
    /// Starts every row from where it is, so a click mid-fade, even after the fold has finished
    /// and only the last rows are still arriving, carries on from what is on screen.
    /// </summary>
    private void RunRows(bool expanded)
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            var from = row.Cells[0].Opacity;
            var startMs = rowMotion.StartMs(i);
            var fade = expanded
                ? WpfMotion.DelayedFade(from, 1, rowMotion.FadeMs, startMs, PanelMotion.Decelerate)
                : WpfMotion.DelayedFade(from, 0, rowMotion.CloseFadeMs, 0);

            foreach (var cell in row.Cells)
            {
                cell.BeginAnimation(UIElement.OpacityProperty, fade);
            }

            // Pinned first, so it holds this frame's value: until a row's delay is up, and for
            // the whole of a closing fade, where a leaving row fades where it stands.
            var y = row.Rise.Y;
            row.Rise.BeginAnimation(TranslateTransform.YProperty, null);
            row.Rise.Y = y;
            row.Rise.BeginAnimation(TranslateTransform.YProperty, expanded ? Rise(y, startMs) : null);
        }
    }

    /// <summary>
    /// A row's rise on whole device pixels, for the reason the surface slide has: Display-mode
    /// text carried at a fractional offset shimmers on the slow end of the curve.
    /// </summary>
    private DoubleAnimationUsingKeyFrames Rise(double fromY, double startMs)
    {
        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = WpfMotion.Duration(startMs + rowMotion.FadeMs),
        };

        for (var ms = 0.0; ms <= rowMotion.FadeMs; ms += RiseFrameMs)
        {
            var left = fromY * (1 - PanelMotion.Decelerate.ValueAt(ms / rowMotion.FadeMs));
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(
                WholePixels(left), KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(startMs + ms))));
        }

        return animation;
    }

    private double WholePixels(double dip)
    {
        var scale = VisualTreeHelper.GetDpi(host).DpiScaleY;
        return Math.Round(dip * scale) / scale;
    }

    private void Settle(bool expanded)
    {
        body.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        body.Height = double.NaN;   // back to auto, so later content changes size naturally
        if (chevron is not null) chevron.Angle = expanded ? ExpandedAngle : 0;
    }

    private void SettleRows(bool expanded)
    {
        foreach (var row in _rows)
        {
            Pin(row, expanded ? 1 : 0, expanded ? 0 : rowMotion.RiseDip);
        }
    }

    private static void Pin(Row row, double opacity, double rise)
    {
        // Released first, for the reason Apply gives.
        row.Rise.BeginAnimation(TranslateTransform.YProperty, null);
        row.Rise.Y = rise;

        foreach (var cell in row.Cells)
        {
            cell.BeginAnimation(UIElement.OpacityProperty, null);
            cell.Opacity = opacity;
        }
    }

    private void Stop()
    {
        _running = false;

        if (_tracking is not null)
        {
            host.SizeChanged -= _tracking;
            _tracking = null;
        }

        _guard?.Stop();
        _guard = null;

        body.BeginAnimation(FrameworkElement.HeightProperty, null);
        chevron?.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    internal static DoubleAnimationUsingKeyFrames Spline(
        double from, double to, Duration duration, KeySpline spline)
    {
        var animation = new DoubleAnimationUsingKeyFrames { Duration = duration };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromPercent(0)));
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromPercent(1), spline));
        return animation;
    }
}
