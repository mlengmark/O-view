using System.Windows;
using System.Windows.Media.Animation;
using OView.App.Rendering;

namespace OView.Tray.Popup;

/// <summary>
/// What the Windows head's animations share: whether animations are on, the translation of
/// <see cref="PanelMotion"/>'s toolkit-free figures into WPF types, and the builder more
/// than one surface needs. The figures themselves live in <see cref="PanelMotion"/>; nothing
/// here decides how long anything takes.
/// </summary>
internal static class WpfMotion
{
    /// <summary>
    /// Whether Windows' "animate controls and elements" setting is on. Read on every call
    /// rather than cached, so switching it takes effect for the next transition without a
    /// restart.
    /// </summary>
    public static bool AnimationsOn => SystemParameters.ClientAreaAnimation;

    /// <summary>A figure in milliseconds, as WPF wants it.</summary>
    public static Duration Duration(double milliseconds) =>
        new(TimeSpan.FromMilliseconds(milliseconds));

    /// <summary>A curve as the four numbers WPF takes.</summary>
    public static KeySpline Spline(PanelMotion.Curve curve) =>
        new(curve.X1, curve.Y1, curve.X2, curve.Y2);

    /// <summary>
    /// A fade that waits <paramref name="delayMs"/> before it starts, holding
    /// <paramref name="from"/> until then. Built without being started so the caller can attach
    /// <c>Completed</c> first, which has to happen before <c>BeginAnimation</c>.
    ///
    /// <para>The delay is a key frame at the start rather than <c>BeginTime</c>: a delayed
    /// animation with the default fill leaves the property at its base value during the wait,
    /// which for a row that is about to fade in is the fully visible one, a flash of the end
    /// state before the start.</para>
    /// </summary>
    public static DoubleAnimationUsingKeyFrames DelayedFade(
        double from, double to, double durationMs, double delayMs, PanelMotion.Curve? curve = null)
    {
        var total = delayMs + durationMs;
        var animation = new DoubleAnimationUsingKeyFrames { Duration = Duration(total) };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));

        if (delayMs > 0)
        {
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(
                from, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delayMs))));
        }

        var end = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(total));
        animation.KeyFrames.Add(curve is { } c
            ? new SplineDoubleKeyFrame(to, end, Spline(c))
            : new LinearDoubleKeyFrame(to, end));
        return animation;
    }

    /// <summary>
    /// A value travelling to where the property already rests: a bar growing, the switch's
    /// highlight sliding, a hover fill fading. The caller assigns the end value first, and this
    /// lets go when it finishes, so there is no held value to release, no completion to wait for
    /// and nothing to strand if one is missed. Replaced midway, the caller reads the property for
    /// the value on screen and starts the next one from there.
    /// </summary>
    public static DoubleAnimationUsingKeyFrames Travel(
        double from, double to, double durationMs, double delayMs = 0, PanelMotion.Curve? curve = null)
    {
        var animation = DelayedFade(from, to, durationMs, delayMs, curve ?? PanelMotion.Decelerate);
        animation.FillBehavior = FillBehavior.Stop;
        return animation;
    }
}
