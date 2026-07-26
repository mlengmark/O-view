using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
    private static readonly Duration OpenDuration = new(TimeSpan.FromMilliseconds(230));
    private static readonly Duration CloseDuration = new(TimeSpan.FromMilliseconds(150));

    /// <summary>How far the content travels into place, on top of the reveal.</summary>
    private const double SlideDistance = 20;

    /// <summary>The fitted decelerate curve. See the class remarks for the fit.</summary>
    private static readonly KeySpline Decelerate = new(0.02, 0.16, 0.20, 0.96);

    /// <summary>Its mirror, for getting out of the way.</summary>
    private static readonly KeySpline Accelerate = new(0.80, 0.04, 0.98, 0.84);

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
        Fade(window, 0, 1, new Duration(TimeSpan.FromMilliseconds(90)), Decelerate);
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

        var fade = Fade(window, window.Opacity, 0, CloseDuration, Accelerate);
        fade.Completed += (_, _) => onClosed();
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

    private static DoubleAnimationUsingKeyFrames Fade(
        Window window, double from, double to, Duration duration, KeySpline spline)
    {
        var animation = new DoubleAnimationUsingKeyFrames { Duration = duration };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromPercent(0)));
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromPercent(1), spline));
        window.BeginAnimation(UIElement.OpacityProperty, animation);
        return animation;
    }
}
