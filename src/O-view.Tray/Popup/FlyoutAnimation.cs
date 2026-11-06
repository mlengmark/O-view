using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using OView.App.Rendering;
using Point = System.Windows.Point;

namespace OView.Tray.Popup;

/// <summary>
/// Open/close transitions for the docked surfaces (the detail panel and the tray menu).
///
/// The whole card slides out of its docked edge and back as one rigid piece: border, corners
/// and content together, at full opacity, with no clip of its own. The window's bounds are the
/// edge it emerges from. The slide and its durations are Maximilian's decision of 2026-11-06,
/// made watching a side-by-side demo.
///
/// It replaced a wipe: a clip rectangle grew from the docked edge while the content moved the
/// last 20 px behind it and a 90 ms fade took the edge off the first instant. Three motions at
/// three rates, so the card was uncovered by a moving line rather than moving, and its border
/// and rounded corners only existed on the final frame. A plain slide had been rejected
/// earlier because the part beyond the edge is cut off by the window's bounds. That is exactly
/// what was seen in the demo, the card emerging 12 px above the taskbar, and it was chosen
/// over the wipe.
///
/// The curves are not invented, and they are all that is left of the wipe. Windows' own Quick
/// Settings flyout was recorded frame by frame on this platform: its top edge rises while its
/// bottom stays pinned to the taskbar, travelling the full height in ~230 ms, with almost no
/// fade. The durations here are deliberately slower than that; the shape is the platform's.
///
/// Sampled progress of that trace, as a fraction of total travel:
///
///     8 ms → 0.15    24 ms → 0.35    57 ms → 0.60    115 ms → 0.84    230 ms → 1.00
///
/// The easing below is FITTED to those samples rather than picked by name, a search
/// over cubic-Bezier control points, scored by RMSE against the trace. The winner and
/// the obvious guesses, with predicted progress at three checkpoints:
///
///     KeySpline(0.02,0.16 0.20,0.96)   RMSE 0.0033   0.14 / 0.60 / 0.84   ← used
///     quartic ease-out                 RMSE 0.0633   0.14 / 0.69 / 0.91
///     cubic-bezier(0,0,0,1)            RMSE 0.0638   0.23 / 0.69 / 0.89
///     cubic ease-out                   RMSE 0.1603   0.05 / 0.37 / 0.68
///     (measured Windows)                             0.15 / 0.60 / 0.84
///
/// The third line was this file's first attempt, on the reasoning that Windows uses
/// cubic-bezier(0,0,0,1). Measured against the real thing it is twenty times further out
/// than the fit and visibly too abrupt, it reached ~70% before the platform reaches 15%,
/// which reads as a pop rather than a rise. Named curves were not close enough to trust.
///
/// Two other gestures were tried on the surfaces before the wipe and neither is coming back.
/// Scaling the whole surface from 97% was smooth but wrong: it read as a zoom rather than as a
/// panel rising out of the taskbar. A fade read as a dissolve, which is why this one moves at
/// full opacity from its first frame to its last.
/// </summary>
internal static class FlyoutAnimation
{
    private static readonly double OpenMs = PanelMotion.Ms(PanelMotion.SurfaceOpenBaseMs);

    private static readonly double CloseMs = PanelMotion.Ms(PanelMotion.SurfaceCloseBaseMs);

    /// <summary>How long a surface takes to arrive, as it runs on screen.</summary>
    internal static readonly Duration OpenDuration = WpfMotion.Duration(OpenMs);

    /// <summary>How long a surface takes to leave, as it runs on screen.</summary>
    internal static readonly Duration CloseDuration = WpfMotion.Duration(CloseMs);

    /// <summary>
    /// The fitted decelerate curve. See the class remarks for the fit; the numbers are
    /// <see cref="PanelMotion.Decelerate"/>.
    ///
    /// <para>Internal rather than private because it is the app's motion vocabulary, not this
    /// file's private constant: <see cref="DisclosureAnimation"/> folds the panel's token
    /// explanation open on the same curve, so an in-place reveal and a surface entrance move
    /// the same way. Fitting it a second time by eye is how two surfaces end up with two
    /// different ideas of what "smooth" means here.</para>
    /// </summary>
    internal static readonly KeySpline Decelerate = WpfMotion.Spline(PanelMotion.Decelerate);

    /// <summary>Its mirror, for getting out of the way.</summary>
    internal static readonly KeySpline Accelerate = WpfMotion.Spline(PanelMotion.Accelerate);


    /// <summary>
    /// Slides the card to rest, at full opacity. With Windows' animations off the card is
    /// simply at rest.
    /// </summary>
    /// <param name="continuing">
    /// True when this interrupts a close, so the card is already on its way out: it turns round
    /// from wherever it is on screen, rather than snapping out of view and replaying.
    /// </param>
    public static void Open(Window window, Point origin, bool continuing = false)
    {
        if (!PanelMotion.Runs(WpfMotion.AnimationsOn, OpenMs) || Prepare(window) is not { } surface)
        {
            Reset(window);
            return;
        }

        var (content, translate) = surface;

        // Before the opacity, not after: the first frame the window is visible for must not be
        // the card at rest. Released first, because an attached animation holds its end value
        // over the local one and this assignment would be silently ignored.
        if (!continuing)
        {
            translate.BeginAnimation(TranslateTransform.YProperty, null);
            translate.Y = OutOfView(content, origin);
        }

        window.Opacity = 1;
        translate.BeginAnimation(TranslateTransform.YProperty, BuildSlide(window, 0, OpenDuration, Decelerate));
    }

    /// <summary>
    /// Runs the close transition and calls <paramref name="onClosed"/> when it finishes.
    /// The caller does the actual Hide() there rather than here: the window must stay
    /// visible for the duration, and only the caller knows whether it still wants to be
    /// hidden by the time the animation ends.
    ///
    /// <para>The slide has no start value, so one that interrupts an open leaves from wherever
    /// the card has got to. With Windows' animations off nothing runs and the caller hides the
    /// window at once.</para>
    /// </summary>
    public static void Close(Window window, Point origin, Action onClosed)
    {
        if (!PanelMotion.Runs(WpfMotion.AnimationsOn, CloseMs) || Prepare(window) is not { } surface)
        {
            onClosed();
            return;
        }

        var (content, translate) = surface;

        // Exactly once, however the transition ends.
        var finished = false;
        void Finish()
        {
            if (finished)
            {
                return;
            }

            finished = true;
            onClosed();
        }

        // Completed MUST be attached before BeginAnimation. WPF takes its own copy of the
        // timeline when the animation starts, so a handler added afterwards is attached to
        // an object nothing will ever raise, which is precisely what broke the toggle:
        // Hide() never ran, the window sat visible at zero opacity, and every later click
        // saw it as "open" and tried to close it again. The icon looked dead.
        var slide = BuildSlide(window, OutOfView(content, origin), CloseDuration, Accelerate);
        slide.Completed += (_, _) => Finish();
        translate.BeginAnimation(TranslateTransform.YProperty, slide);

        // Backstop. The symptom of a missed completion is not a dropped frame, it is a
        // permanently unresponsive tray icon, so the hide does not depend on the event
        // alone. Firing twice is harmless, Finish() is idempotent and the caller
        // re-checks whether it still wants to be hidden.
        var guard = new DispatcherTimer
        {
            Interval = CloseDuration.TimeSpan + TimeSpan.FromMilliseconds(150),
        };
        guard.Tick += (_, _) =>
        {
            guard.Stop();
            Finish();
        };
        guard.Start();
    }

    /// <summary>
    /// Clears any running animations and restores the surface to plain, fully visible
    /// state. Used by verification paths that need a still, and by an open with Windows'
    /// animations off.
    /// </summary>
    public static void Reset(Window window)
    {
        window.BeginAnimation(UIElement.OpacityProperty, null);
        window.Opacity = 1;

        if (window.Content is not FrameworkElement content)
        {
            return;
        }

        // Nothing here sets a clip any more, but one left on the content would still cut the
        // card, so a still must not inherit it.
        content.Clip = null;

        if (content.RenderTransform is TranslateTransform translate)
        {
            translate.BeginAnimation(TranslateTransform.YProperty, null);
            translate.Y = 0;
        }
    }

    /// <summary>
    /// Ensures the content carries the translate the transitions drive, created once and
    /// reused so repeated opens do not stack transforms. Null for a surface with nothing laid
    /// out to move.
    /// </summary>
    private static (FrameworkElement Content, TranslateTransform Translate)? Prepare(Window window)
    {
        if (window.Content is not FrameworkElement { ActualHeight: > 0 } content)
        {
            return null;
        }

        if (content.RenderTransform is not TranslateTransform translate)
        {
            translate = new TranslateTransform();
            content.RenderTransform = translate;
        }

        return (content, translate);
    }

    /// <summary>
    /// The offset that puts the card fully past its docked edge. origin.Y == 1 means docked at
    /// the bottom, so the card waits below and rises; a top-docked taskbar reverses it.
    /// PopupPositioner is the only thing that knows.
    /// </summary>
    private static double OutOfView(FrameworkElement content, Point origin) =>
        origin.Y >= 0.5 ? content.ActualHeight : -content.ActualHeight;

    /// <summary>
    /// Builds the slide without starting it, so a caller can attach Completed first,
    /// which is mandatory, not stylistic. See <see cref="Close"/>.
    /// </summary>
    private static WholePixelSlide BuildSlide(Window window, double to, Duration duration, KeySpline spline) =>
        new()
        {
            To = to,
            Spline = spline,
            DeviceScale = VisualTreeHelper.GetDpi(window).DpiScaleY,
            Duration = duration,
        };

    /// <summary>
    /// Eases from wherever the property is to <see cref="To"/>, landing on whole device pixels.
    ///
    /// <para><b>Why the rounding.</b> Both windows set <c>TextFormattingMode="Display"</c>,
    /// which fits glyphs to whole pixels, so text carried at a fractional offset shimmers, and
    /// the long, slow tail of the decelerate curve is where the eye has time to see it.</para>
    ///
    /// <para><b>Why dependency properties.</b> WPF animates a frozen copy of the timeline, made
    /// by cloning its dependency properties, so a plain field would reach the clock at its
    /// default.</para>
    ///
    /// <para><b>Why there is no From.</b> WPF hands a replacement animation the on-screen value
    /// of the one it replaces, which is what lets a click mid-slide turn the card round where
    /// it is rather than snapping it to an end.</para>
    /// </summary>
    private sealed class WholePixelSlide : DoubleAnimationBase
    {
        private static readonly DependencyProperty ToProperty =
            DependencyProperty.Register(nameof(To), typeof(double), typeof(WholePixelSlide));

        private static readonly DependencyProperty SplineProperty =
            DependencyProperty.Register(nameof(Spline), typeof(KeySpline), typeof(WholePixelSlide));

        private static readonly DependencyProperty DeviceScaleProperty =
            DependencyProperty.Register(
                nameof(DeviceScale), typeof(double), typeof(WholePixelSlide), new PropertyMetadata(1.0));

        public double To
        {
            get => (double)GetValue(ToProperty);
            init => SetValue(ToProperty, value);
        }

        public KeySpline Spline
        {
            get => (KeySpline)GetValue(SplineProperty);
            init => SetValue(SplineProperty, value);
        }

        public double DeviceScale
        {
            get => (double)GetValue(DeviceScaleProperty);
            init => SetValue(DeviceScaleProperty, value);
        }

        protected override Freezable CreateInstanceCore() => new WholePixelSlide();

        protected override double GetCurrentValueCore(
            double defaultOriginValue, double defaultDestinationValue, AnimationClock animationClock)
        {
            var eased = Spline.GetSplineProgress(animationClock.CurrentProgress ?? 0);
            var value = defaultOriginValue + (To - defaultOriginValue) * eased;
            return Math.Round(value * DeviceScale) / DeviceScale;
        }
    }
}
