using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Point = System.Windows.Point;

namespace OView.Tray.Popup;

/// <summary>
/// Open/close transitions for the docked surfaces (the detail panel and the tray menu).
///
/// Both used to appear and vanish in a single frame, which reads as a glitch rather than
/// as a window opening — the thing every system flyout avoids. They now fade and grow
/// from the corner they are docked to, so the motion points back at the tray icon that
/// summoned them.
///
/// Why scale rather than a slide: a slide has to translate the content, and the content
/// fills the window, so the part that slides in from beyond the edge is clipped by the
/// window bounds. Growing from 97% shrinks inward instead — no clipping, no window moves
/// mid-animation (which stutter under per-monitor DPI), and it lands in the same place a
/// slide would suggest.
///
/// Durations follow the platform's own flyouts: a slightly slower open than close, since
/// the open is the one the eye follows and the close should get out of the way.
/// </summary>
internal static class FlyoutAnimation
{
    private static readonly Duration OpenDuration = new(TimeSpan.FromMilliseconds(160));
    private static readonly Duration CloseDuration = new(TimeSpan.FromMilliseconds(110));

    /// <summary>How small it starts. Subtle on purpose — a big zoom reads as a toy.</summary>
    private const double StartScale = 0.97;

    private static readonly IEasingFunction OpenEase = new CubicEase { EasingMode = EasingMode.EaseOut };
    private static readonly IEasingFunction CloseEase = new CubicEase { EasingMode = EasingMode.EaseIn };

    /// <summary>
    /// Runs the open transition. Safe to call while a close is still running — the
    /// animations are simply replaced, so re-opening mid-fade snaps back to visible
    /// instead of continuing to disappear.
    /// </summary>
    public static void Open(Window window, Point origin)
    {
        var content = Prepare(window, origin);
        if (content is null)
        {
            window.Opacity = 1;
            return;
        }

        window.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, OpenDuration) { EasingFunction = OpenEase });
        Scale(content, StartScale, 1, OpenDuration, OpenEase);
    }

    /// <summary>
    /// Runs the close transition and calls <paramref name="onClosed"/> when it finishes.
    /// The caller does the actual Hide() there rather than here: the window must stay
    /// visible for the duration, and only the caller knows whether it still wants to be
    /// hidden by the time the animation ends.
    /// </summary>
    public static void Close(Window window, Point origin, Action onClosed)
    {
        var content = Prepare(window, origin);
        if (content is null)
        {
            onClosed();
            return;
        }

        var fade = new DoubleAnimation(window.Opacity, 0, CloseDuration) { EasingFunction = CloseEase };
        fade.Completed += (_, _) => onClosed();
        window.BeginAnimation(UIElement.OpacityProperty, fade);
        Scale(content, 1, StartScale, CloseDuration, CloseEase);
    }

    /// <summary>
    /// Clears any running animations and restores the window to plain, fully visible
    /// state. Used by verification paths that need a still, and by an open that follows
    /// a cancelled close.
    /// </summary>
    public static void Reset(Window window)
    {
        window.BeginAnimation(UIElement.OpacityProperty, null);
        window.Opacity = 1;

        if (window.Content is FrameworkElement { RenderTransform: ScaleTransform scale })
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scale.ScaleX = 1;
            scale.ScaleY = 1;
        }
    }

    /// <summary>
    /// Ensures the content carries a ScaleTransform anchored at the docked corner. The
    /// transform is created once and reused, so repeated opens do not stack transforms.
    /// </summary>
    private static FrameworkElement? Prepare(Window window, Point origin)
    {
        if (window.Content is not FrameworkElement content)
        {
            return null;
        }

        if (content.RenderTransform is not ScaleTransform)
        {
            content.RenderTransform = new ScaleTransform(1, 1);
        }

        // Set every time: the docked corner changes with the taskbar edge and with which
        // monitor the surface opened on.
        content.RenderTransformOrigin = origin;
        return content;
    }

    private static void Scale(
        FrameworkElement content, double from, double to, Duration duration, IEasingFunction ease)
    {
        if (content.RenderTransform is not ScaleTransform transform)
        {
            return;
        }

        var animation = new DoubleAnimation(from, to, duration) { EasingFunction = ease };
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }
}
