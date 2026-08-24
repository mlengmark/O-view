using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Point = System.Windows.Point;

namespace OView.Tray.Popup;

/// <summary>
/// Open/close transitions for the docked surfaces (the detail panel and the tray menu).
///
/// These are not invented. Windows' own Quick Settings flyout was recorded frame by
/// frame on this platform and the numbers below come from that trace: its top edge rises
/// while its bottom stays pinned to the taskbar, travelling the full height in ~230 ms,
/// with almost no fade — it is a *geometric reveal*, not a zoom and not a dissolve.
///
/// Sampled progress of that reveal, as a fraction of total travel:
///
///     8 ms → 0.15    24 ms → 0.35    57 ms → 0.60    115 ms → 0.84    230 ms → 1.00
///
/// The easing below is FITTED to those samples rather than picked by name — a search
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
/// than the fit and visibly too abrupt — it reached ~70% before the platform reaches 15%,
/// which reads as a pop rather than a rise. Named curves were not close enough to trust.
///
/// An earlier attempt scaled the whole surface from 97%. Smooth but wrong: it read as a
/// zoom rather than as a panel rising out of the taskbar, which is what the platform does
/// and what this was asked to match.
/// </summary>
internal static class FlyoutAnimation
{
    /// <summary>
    /// The entrance and exit durations, from the trace in the class remarks.
    ///
    /// <para>Internal for the same reason as the curves below: a transition that travels a
    /// shorter distance is <b>scaled from these</b> rather than timed independently
    /// (<see cref="DisclosureAnimation"/>), so the in : out ratio the platform uses survives
    /// wherever it is applied.</para>
    /// </summary>
    internal static readonly Duration OpenDuration = new(TimeSpan.FromMilliseconds(230));

    /// <inheritdoc cref="OpenDuration"/>
    internal static readonly Duration CloseDuration = new(TimeSpan.FromMilliseconds(150));

    /// <summary>How far the content travels into place, on top of the reveal.</summary>
    private const double SlideDistance = 20;

    /// <summary>
    /// The fitted decelerate curve. See the class remarks for the fit.
    ///
    /// <para>Internal rather than private because it is the app's motion vocabulary, not this
    /// file's private constant: <see cref="DisclosureAnimation"/> folds the panel's token
    /// explanation open on the same curve, so an in-place reveal and a surface entrance move
    /// the same way. Fitting it a second time by eye is how two surfaces end up with two
    /// different ideas of what "smooth" means here.</para>
    /// </summary>
    internal static readonly KeySpline Decelerate = new(0.02, 0.16, 0.20, 0.96);

    /// <summary>Its mirror, for getting out of the way.</summary>
    internal static readonly KeySpline Accelerate = new(0.80, 0.04, 0.98, 0.84);


    public static void Open(Window window, Point origin)
    {
        if (Prepare(window, origin) is not { } surface)
        {
            window.Opacity = 1;
            return;
        }

        var (content, clip, slide, fromBottom) = surface;
        var w = content.ActualWidth;
        var h = content.ActualHeight;
        if (w <= 0 || h <= 0)
        {
            window.Opacity = 1;
            return;
        }

        // Collapsed against the docked edge, expanding to the full surface.
        var closed = fromBottom ? new Rect(0, h, w, 0) : new Rect(0, 0, w, 0);
        Reveal(clip, closed, new Rect(0, 0, w, h), OpenDuration, Decelerate);
        Slide(slide, fromBottom ? SlideDistance : -SlideDistance, 0, OpenDuration, Decelerate);

        // Barely a fade: the trace showed the flyout at near-full intensity from its
        // first visible frame, so opacity only takes the edge off the first instant.
        window.BeginAnimation(UIElement.OpacityProperty,
            BuildFade(0, 1, new Duration(TimeSpan.FromMilliseconds(90)), Decelerate));
    }

    /// <summary>
    /// Runs the close transition and calls <paramref name="onClosed"/> when it finishes.
    /// The caller does the actual Hide() there rather than here: the window must stay
    /// visible for the duration, and only the caller knows whether it still wants to be
    /// hidden by the time the animation ends.
    /// </summary>
    public static void Close(Window window, Point origin, Action onClosed)
    {
        if (Prepare(window, origin) is not { } surface)
        {
            onClosed();
            return;
        }

        var (content, clip, slide, fromBottom) = surface;
        var w = content.ActualWidth;
        var h = content.ActualHeight;
        if (w <= 0 || h <= 0)
        {
            onClosed();
            return;
        }

        var closed = fromBottom ? new Rect(0, h, w, 0) : new Rect(0, 0, w, 0);
        Reveal(clip, new Rect(0, 0, w, h), closed, CloseDuration, Accelerate);
        Slide(slide, 0, fromBottom ? SlideDistance : -SlideDistance, CloseDuration, Accelerate);

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
        // an object nothing will ever raise — which is precisely what broke the toggle:
        // Hide() never ran, the window sat visible at zero opacity, and every later click
        // saw it as "open" and tried to close it again. The icon looked dead.
        var fade = BuildFade(window.Opacity, 0, CloseDuration, Accelerate);
        fade.Completed += (_, _) => Finish();
        window.BeginAnimation(UIElement.OpacityProperty, fade);

        // Backstop. The symptom of a missed completion is not a dropped frame, it is a
        // permanently unresponsive tray icon, so the hide does not depend on the event
        // alone. Firing twice is harmless — Finish() is idempotent and the caller
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
    /// Re-fits the reveal clip to the content's size, for a surface that has changed size
    /// while open.
    ///
    /// <para><b>Why this is needed at all.</b> <see cref="Open"/> clips the content to a
    /// <see cref="Rect"/> measured when the flyout opened, and that rectangle is a fixed
    /// number — it does not track the content. A surface that then grows is drawn only as far
    /// as the old height and sliced off below it. The tray menu hit this the moment the
    /// threshold list could expand: the window grew correctly and re-docked correctly, and
    /// the card was still rendered at its former height, so the rows past the fold lost their
    /// bottoms along with the border and the rounded corner.</para>
    ///
    /// <para><b>Releasing the animation first is mandatory, not tidiness.</b>
    /// <c>BeginAnimation</c> defaults to <c>FillBehavior.HoldEnd</c>, so the animated
    /// <c>Rect</c> goes on overriding local values for as long as it is attached — assigning
    /// <c>clip.Rect</c> without clearing it first is silently ignored, and the fix would look
    /// like it had been applied while changing nothing.</para>
    /// </summary>
    public static void FitClipToContent(Window window)
    {
        if (window.Content is not FrameworkElement content || content.Clip is not RectangleGeometry clip)
        {
            return;
        }

        clip.BeginAnimation(RectangleGeometry.RectProperty, null);
        clip.Rect = new Rect(0, 0, content.ActualWidth, content.ActualHeight);
    }

    /// <summary>
    /// Clears any running animations and restores the surface to plain, fully visible
    /// state. Used by verification paths that need a still, and by an open that follows
    /// a cancelled close.
    /// </summary>
    public static void Reset(Window window)
    {
        window.BeginAnimation(UIElement.OpacityProperty, null);
        window.Opacity = 1;

        if (window.Content is not FrameworkElement content)
        {
            return;
        }

        if (content.Clip is RectangleGeometry clip)
        {
            clip.BeginAnimation(RectangleGeometry.RectProperty, null);
            content.Clip = null;
        }

        if (content.RenderTransform is TranslateTransform slide)
        {
            slide.BeginAnimation(TranslateTransform.YProperty, null);
            slide.Y = 0;
        }
    }

    /// <summary>
    /// Ensures the content carries the clip and translate the transitions drive. Both are
    /// created once and reused, so repeated opens do not stack transforms.
    /// </summary>
    private static (FrameworkElement Content, RectangleGeometry Clip, TranslateTransform Slide, bool FromBottom)?
        Prepare(Window window, Point origin)
    {
        if (window.Content is not FrameworkElement content)
        {
            return null;
        }

        if (content.Clip is not RectangleGeometry clip)
        {
            clip = new RectangleGeometry();
            content.Clip = clip;
        }

        if (content.RenderTransform is not TranslateTransform slide)
        {
            slide = new TranslateTransform();
            content.RenderTransform = slide;
        }

        // origin.Y == 1 means docked at the bottom, so the surface grows upward; a
        // top-docked taskbar reverses it. PopupPositioner is the only thing that knows.
        return (content, clip, slide, origin.Y >= 0.5);
    }

    private static void Reveal(
        RectangleGeometry clip, Rect from, Rect to, Duration duration, KeySpline spline)
    {
        var animation = new RectAnimationUsingKeyFrames { Duration = duration };
        animation.KeyFrames.Add(new DiscreteRectKeyFrame(from, KeyTime.FromPercent(0)));
        animation.KeyFrames.Add(new SplineRectKeyFrame(to, KeyTime.FromPercent(1), spline));
        clip.BeginAnimation(RectangleGeometry.RectProperty, animation);
    }

    private static void Slide(
        TranslateTransform slide, double from, double to, Duration duration, KeySpline spline)
    {
        var animation = new DoubleAnimationUsingKeyFrames { Duration = duration };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromPercent(0)));
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromPercent(1), spline));
        slide.BeginAnimation(TranslateTransform.YProperty, animation);
    }

    /// <summary>
    /// Builds the fade without starting it, so a caller can attach Completed first —
    /// which is mandatory, not stylistic. See <see cref="Close"/>.
    /// </summary>
    private static DoubleAnimationUsingKeyFrames BuildFade(
        double from, double to, Duration duration, KeySpline spline)
    {
        var animation = new DoubleAnimationUsingKeyFrames { Duration = duration };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromPercent(0)));
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromPercent(1), spline));
        return animation;
    }
}
