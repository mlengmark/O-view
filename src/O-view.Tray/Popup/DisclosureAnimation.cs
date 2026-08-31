using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace OView.Tray.Popup;

/// <summary>
/// The fold that opens and closes the panel's "Why so large?" explanation.
///
/// <para>It used to be a <c>Visibility</c> flip: the body appeared whole, the
/// <c>SizeToContent</c> window jumped to its new height in one frame, and the re-dock moved
/// it to a new position in the same frame. Three simultaneous discontinuities in a surface
/// whose entrance is a 230 ms geometric reveal — the panel is the one thing on screen that
/// had already been tuned against a frame-by-frame trace of the platform, and the disclosure
/// ignored all of it.</para>
///
/// <para><b>The motion is inherited, not invented.</b> Both the curve and the timing come
/// from <see cref="FlyoutAnimation"/>, whose numbers were fitted to a recording of Windows'
/// own Quick Settings flyout. The fold travels a fraction of the distance the entrance does,
/// so the durations are <b>scaled</b> from it — <see cref="Scale"/> — rather than picked by
/// feel, which keeps the platform's in : out ratio and stops the app growing a second,
/// slightly different idea of "smooth".</para>
///
/// <para><b>A geometric reveal, not a dissolve.</b> There is deliberately no fade: the body's
/// height animates and its content is clipped to it, so the text slides out from behind the
/// fold edge exactly as the panel itself rises out of the taskbar. <c>FlyoutAnimation</c>'s
/// remarks record that a fade and a scale were both tried on the entrance and both read as
/// the wrong gesture; adding either here would say the panel and its own disclosure move by
/// different rules.</para>
///
/// <para><b>Re-docking per frame is load-bearing.</b> The window is <c>SizeToContent</c> and
/// docked by its top-left, so a fold that grows it without re-placing it pushes the new
/// content down into the taskbar (issue #33) — for the whole length of the animation rather
/// than for the single frame the instant version was exposed to. Tracking
/// <c>SizeChanged</c> is what makes the fold read as the panel growing <i>upward</i> out of
/// the docked corner, which is the same direction its entrance travels.</para>
/// </summary>
/// <param name="host">The flyout window, whose height follows the body's.</param>
/// <param name="body">The folding content. Must clip to its bounds, or it spills mid-fold.</param>
/// <param name="chevron">
/// The disclosure arrow's rotation, turned by the same curve. <b>Null where the control
/// carries no arrow</b> — the Bars/Breakdown switch (issue #253) shows its state by which
/// segment is lit, so a chevron there would be a second, redundant indicator.
/// </param>
/// <param name="reposition">Re-docks the host — called on every frame the height changes.</param>
internal sealed class DisclosureAnimation(
    Window host, FrameworkElement body, RotateTransform? chevron, Action reposition)
{
    /// <summary>
    /// How much of the entrance's travel this fold covers. The panel is ~700 px tall and
    /// enters in 230 ms; the explanation adds ~60 px, and matching the entrance's duration
    /// over a twelfth of its distance reads as hesitation rather than as motion.
    ///
    /// <para>Applied to both durations, so the fold keeps the traced 230 : 150 ratio between
    /// opening and closing — out is quicker than in, because getting out of the way is not a
    /// gesture anyone waits to watch.</para>
    /// </summary>
    private const double Scale = 0.83;

    private static readonly Duration ExpandDuration = Scaled(FlyoutAnimation.OpenDuration);
    private static readonly Duration CollapseDuration = Scaled(FlyoutAnimation.CloseDuration);

    private static Duration Scaled(Duration d) =>
        new(TimeSpan.FromMilliseconds(Math.Round(d.TimeSpan.TotalMilliseconds * Scale)));

    /// <summary>Rotation of the chevron in each state. 180° turns the arrow to point back up.</summary>
    private const double ExpandedAngle = 180;

    private SizeChangedEventHandler? _tracking;
    private DispatcherTimer? _guard;
    private bool _running;

    /// <summary>
    /// Where the fold is right now, so a click that lands mid-animation continues from what
    /// is on screen instead of snapping to one end and travelling the whole way again.
    /// </summary>
    public (double Height, double Angle) Current => (
        body.Visibility == Visibility.Visible ? body.ActualHeight : 0,
        chevron?.Angle ?? 0);

    /// <summary>
    /// Puts the fold in its finished state immediately, with nothing left animating.
    ///
    /// <para>This is the state setter the non-interactive paths use — <c>Populate</c> resets
    /// the disclosure on every open, and the verification renders need a still rather than a
    /// frame from the middle of a transition.</para>
    ///
    /// <para><b>Clearing the animations first is mandatory, not tidiness.</b>
    /// <c>BeginAnimation</c> holds its end value over the local one for as long as it is
    /// attached, and this window is reused across opens — so without the release, assigning
    /// <c>Height</c> or <c>Angle</c> here is silently ignored and the panel opens still
    /// wearing the last fold's geometry.</para>
    /// </summary>
    public void Apply(bool expanded)
    {
        Stop();

        body.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        body.Height = double.NaN;   // back to auto, so later content changes size naturally
        if (chevron is not null) chevron.Angle = expanded ? ExpandedAngle : 0;
    }

    /// <summary>
    /// Freezes the fold partway through, for a verification render of a state that otherwise
    /// exists only for a tenth of a second on a real desktop.
    ///
    /// <para>Exact rather than approximate: the height and the chevron are driven by one
    /// curve over one duration, so at a given fraction of the height's travel the arrow is at
    /// the same fraction of its turn. A rendered frame is therefore a real frame.</para>
    /// </summary>
    /// <param name="fraction">How far open, 0–1.</param>
    /// <param name="naturalHeight">The body's fully open height, measured by the caller.</param>
    public void ApplyPartial(double fraction, double naturalHeight)
    {
        Stop();

        var f = Math.Clamp(fraction, 0, 1);
        body.Visibility = Visibility.Visible;
        body.Height = naturalHeight * f;
        if (chevron is not null) chevron.Angle = ExpandedAngle * f;
    }

    /// <summary>
    /// Pins the body at the height the fold will start from, before the caller lays the panel
    /// out for its new state.
    ///
    /// <para><b>This is what keeps the window from ever being laid out at the far end of the
    /// fold.</b> Showing the body at its natural height and animating from zero afterwards
    /// looks identical in the visual tree and is not: the intervening layout resizes the
    /// window, which is docked by its top-left, and the compositor presents that HWND before
    /// the re-dock catches up. <c>--fold-check</c>'s screen grabs caught the resulting frame —
    /// a full-height panel 72 px down, its bottom inside the taskbar — which is the exact
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
    /// so the answer arrives without a window layout pass — see <see cref="Prepare"/> for why
    /// that distinction is the whole point.</para>
    /// </summary>
    public double MeasureNatural(double availableWidth)
    {
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
    /// The body's height when open, measured by the caller — it has already laid the panel
    /// out at its final size to re-decide the density, so measuring again here would be a
    /// second full layout pass for a number it is holding.
    /// </param>
    public void Run(bool expanded, double fromHeight, double fromAngle, double toHeight)
    {
        Stop();

        var duration = expanded ? ExpandDuration : CollapseDuration;
        var spline = expanded ? FlyoutAnimation.Decelerate : FlyoutAnimation.Accelerate;

        // Visible throughout, in both directions: collapsed content has no height to animate,
        // so a fold that closes has to stay on screen until it has finished closing.
        body.Visibility = Visibility.Visible;
        body.Height = fromHeight;

        _running = true;

        _tracking = (_, _) => reposition();
        host.SizeChanged += _tracking;

        var height = Spline(fromHeight, toHeight, duration, spline);

        // Completed MUST be attached before BeginAnimation. WPF takes its own copy of the
        // timeline when the animation starts, so a handler added afterwards is attached to an
        // object nothing will ever raise — the exact mistake that once left the flyout
        // visible at zero opacity with a dead tray icon (see FlyoutAnimation.Close).
        height.Completed += (_, _) => Finish(expanded);
        body.BeginAnimation(FrameworkElement.HeightProperty, height);

        chevron?.BeginAnimation(RotateTransform.AngleProperty,
            Spline(fromAngle, expanded ? ExpandedAngle : 0, duration, spline));

        // Backstop, for the same reason the close transition has one: the symptom of a missed
        // completion here is a panel stuck at a fixed height that no longer grows with its
        // own content, and a SizeChanged handler re-docking the window for the rest of the
        // session. Firing twice is harmless — Finish is idempotent.
        _guard = new DispatcherTimer
        {
            Interval = duration.TimeSpan + TimeSpan.FromMilliseconds(150),
        };
        _guard.Tick += (_, _) => Finish(expanded);
        _guard.Start();
    }

    private void Finish(bool expanded)
    {
        if (!_running)
        {
            return;
        }

        _running = false;

        // Releases the animations and restores auto height, so the body goes back to sizing
        // itself — a panel whose text later rewraps to three lines must not stay pinned to
        // the height this fold happened to end on.
        Apply(expanded);

        // Laid out before it is re-docked, so the dock is computed from the height the panel
        // has FINISHED at rather than from the last animated frame — the fold's last step is
        // its largest on the way out, so a stale one leaves the panel visibly off its edge.
        host.UpdateLayout();
        reposition();

        // And again once the layout that Apply just invalidated has actually run. A
        // SizeToContent window's ActualHeight trails the pass that produced it, so the
        // re-dock above places the panel for the height it had a frame ago — measured, not
        // supposed: --fold-check caught a collapse settling 13 px above its docked edge and
        // staying there, because the last fold frame was the number the dock was computed
        // from. Loaded priority is the first point after layout, and re-docking an
        // already-correct window costs one SetWindowPos.
        host.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, reposition);
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

    private static DoubleAnimationUsingKeyFrames Spline(
        double from, double to, Duration duration, KeySpline spline)
    {
        var animation = new DoubleAnimationUsingKeyFrames { Duration = duration };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromPercent(0)));
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromPercent(1), spline));
        return animation;
    }
}
