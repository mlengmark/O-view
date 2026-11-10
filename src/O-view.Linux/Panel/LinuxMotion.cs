using System.Diagnostics;
using Avalonia.Threading;
using OView.App.Rendering;

namespace OView.Linux.Panel;

/// <summary>
/// The Linux head's motion: <see cref="PanelMotion"/>'s figures and curves run against Avalonia,
/// one frame at a time, and the one place every animated part of the panel goes through.
///
/// <para><b>Why not Avalonia's own animations.</b> Its transitions run on the compositor's
/// clock, which a test cannot advance, and they answer "where is it now?" only by reading the
/// property back. Here the curve is <see cref="PanelMotion.Curve.ValueAt"/>, the same arithmetic
/// the Windows head's still renders use, so the two heads cannot come to disagree about where a
/// motion is at 40%, and a test drives the clock by hand.</para>
///
/// <para><b>Why a timer and a stopwatch.</b> Each frame asks the stopwatch how long the motion has
/// run, rather than counting ticks, so a late or dropped tick costs smoothness and never time: a
/// motion always lands when its figure says, and its completion is the same tick that lands it.
/// There is no separate completion event to miss, which is the failure that once left the Windows
/// panel visible and the tray icon looking dead.</para>
///
/// <para><b>The desktop's setting is asked at the start of every motion,</b> not once, so turning
/// animations off takes effect at the next one without a restart. Off, or a zero-length figure,
/// and the end value is applied at once and the motion's completion runs straight away
/// (<see cref="PanelMotion.Runs"/>).</para>
/// </summary>
public sealed class LinuxMotion
{
    /// <summary>
    /// How often a running motion is drawn. About one display frame at 60 Hz; the value on screen
    /// is computed from the elapsed time, so this sets smoothness and nothing else.
    /// </summary>
    public static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(16);

    private readonly Func<bool> _animationsOn;
    private readonly Func<TimeSpan> _clock;
    private readonly Action<bool> _pump;
    private readonly List<MotionValue> _running = [];
    private bool _pumping;

    /// <summary>The panel's motion, on the UI thread's timer, asking <paramref name="animationsOn"/> at every start.</summary>
    public LinuxMotion(Func<bool> animationsOn)
        : this(animationsOn, StopwatchClock(), pump: null)
    {
    }

    /// <summary>
    /// A motion on a clock and a frame source of the caller's: the tests' hook, since no test here
    /// has a dispatcher that runs timers. <paramref name="pump"/> is told when frames are needed
    /// (true) and when they no longer are (false); null means the UI thread's own timer.
    /// </summary>
    public LinuxMotion(Func<bool> animationsOn, Func<TimeSpan> clock, Action<bool>? pump)
    {
        _animationsOn = animationsOn;
        _clock = clock;
        _pump = pump ?? DispatcherPump();
    }

    /// <summary>Whether a motion starting now would run. Asks the desktop's setting each time.</summary>
    public bool AnimationsOn => _animationsOn();

    /// <summary>Whether anything is moving.</summary>
    public bool Running => _running.Count > 0;

    /// <summary>
    /// A value this motion can move, starting at <paramref name="initial"/>, which
    /// <paramref name="apply"/> puts on screen. Applied once now, so the screen and the value agree
    /// from the start.
    /// </summary>
    public MotionValue Value(double initial, Action<double> apply) => new(this, initial, apply);

    /// <summary>Draws one frame of everything that is moving. The timer calls it; a test calls it by hand.</summary>
    public void Tick()
    {
        var now = _clock();

        // A copy, because a motion landing can start another, and that one waits for the next frame.
        foreach (var value in _running.ToArray())
        {
            value.Advance(now);
        }

        _running.RemoveAll(v => !v.InFlight);
        StopPumpIfIdle();
    }

    internal TimeSpan Now => _clock();

    internal void Started(MotionValue value)
    {
        if (!_running.Contains(value))
        {
            _running.Add(value);
        }

        if (!_pumping)
        {
            _pumping = true;
            _pump(true);
        }
    }

    internal void Stopped(MotionValue value)
    {
        _running.Remove(value);
        StopPumpIfIdle();
    }

    private void StopPumpIfIdle()
    {
        if (_pumping && _running.Count == 0)
        {
            _pumping = false;
            _pump(false);
        }
    }

    private static Func<TimeSpan> StopwatchClock()
    {
        var stopwatch = Stopwatch.StartNew();
        return () => stopwatch.Elapsed;
    }

    /// <summary>
    /// The UI thread's timer, made at the first motion rather than here, so building a panel that
    /// never moves (the offscreen renders) touches no dispatcher. Render priority, so a frame is
    /// drawn ahead of input and background work, as a frame should be.
    /// </summary>
    private Action<bool> DispatcherPump()
    {
        DispatcherTimer? timer = null;
        return run =>
        {
            if (timer is null)
            {
                if (!run)
                {
                    return;
                }

                timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = FrameInterval };
                timer.Tick += (_, _) => Tick();
            }

            if (run)
            {
                timer.Start();
            }
            else
            {
                timer.Stop();
            }
        };
    }
}

/// <summary>
/// One number the panel shows, moved by <see cref="LinuxMotion"/>: an offset, an opacity, a height.
///
/// <para><b>A new motion starts from what is on screen.</b> <see cref="To"/> takes no start value:
/// it begins at <see cref="Current"/>, wherever the last motion had got to, so a click in the middle
/// of one turns it round where it is rather than snapping to an end and replaying. A motion that is
/// replaced never runs its completion, because what it was completing is no longer what was asked
/// for.</para>
/// </summary>
public sealed class MotionValue
{
    private readonly LinuxMotion _motion;
    private readonly Action<double> _apply;

    private double _from;
    private double _durationMs;
    private double _delayMs;
    private TimeSpan _startedAt;
    private PanelMotion.Curve _curve;
    private Action? _arrived;

    internal MotionValue(LinuxMotion motion, double initial, Action<double> apply)
    {
        _motion = motion;
        _apply = apply;
        Current = initial;
        Target = initial;
        _apply(initial);
    }

    /// <summary>The value on screen now.</summary>
    public double Current { get; private set; }

    /// <summary>Where the value is going, or where it rests.</summary>
    public double Target { get; private set; }

    /// <summary>Whether a motion is under way, including one still waiting out its delay.</summary>
    public bool InFlight { get; private set; }

    /// <summary>Puts the value at <paramref name="value"/> at once, dropping any motion and its completion.</summary>
    public void Set(double value)
    {
        Cancel();
        Target = value;
        Show(value);
    }

    /// <summary>
    /// Moves the value from where it is to <paramref name="target"/> over
    /// <paramref name="durationMs"/> (on-screen milliseconds, a <see cref="PanelMotion.Ms"/> figure)
    /// along <paramref name="curve"/>, after <paramref name="delayMs"/>, then runs
    /// <paramref name="arrived"/>.
    ///
    /// <para>With the desktop's animations off, or nothing to run, the value is at its target and
    /// <paramref name="arrived"/> has run by the time this returns.</para>
    /// </summary>
    public void To(
        double target, double durationMs, PanelMotion.Curve curve, Action? arrived = null, double delayMs = 0)
    {
        if (!PanelMotion.Runs(_motion.AnimationsOn, durationMs))
        {
            Set(target);
            arrived?.Invoke();
            return;
        }

        _from = Current;
        Target = target;
        _durationMs = durationMs;
        _delayMs = Math.Max(0, delayMs);
        _curve = curve;
        _arrived = arrived;
        _startedAt = _motion.Now;
        InFlight = true;
        _motion.Started(this);
    }

    /// <summary>One frame: where the motion is at <paramref name="now"/>, and its completion if it has landed.</summary>
    internal void Advance(TimeSpan now)
    {
        if (!InFlight)
        {
            return;
        }

        var elapsed = (now - _startedAt).TotalMilliseconds - _delayMs;
        if (elapsed < 0)
        {
            return;
        }

        var time = elapsed / _durationMs;
        if (time < 1)
        {
            Show(_from + (Target - _from) * _curve.ValueAt(time));
            return;
        }

        // Landed exactly, whatever the last frame's arithmetic, so a value at rest is the figure
        // asked for and not a hair short of it.
        var arrived = _arrived;
        _arrived = null;
        InFlight = false;
        Show(Target);
        arrived?.Invoke();
    }

    private void Cancel()
    {
        if (!InFlight)
        {
            return;
        }

        InFlight = false;
        _arrived = null;
        _motion.Stopped(this);
    }

    private void Show(double value)
    {
        Current = value;
        _apply(value);
    }
}
