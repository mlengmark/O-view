namespace OView.App;

/// <summary>
/// A repeating timer, abstracted so the engine can schedule work without naming a UI
/// framework. The WPF head backs this with a <c>DispatcherTimer</c>, which keeps every
/// callback on the UI thread so a head can surface a balloon or a dialog from a tick
/// without marshalling; the Linux head supplies its own loop's equivalent.
///
/// <para>Tests supply a fake and drive it by hand, which is what makes the refresh cycle,
/// the cadence changes and the update schedule testable without waiting in real time.</para>
/// </summary>
public interface IAppTimer : IDisposable
{
    /// <summary>Assigning re-times the timer. Implementations should ignore an unchanged value.</summary>
    TimeSpan Interval { get; set; }

    void Start();

    void Stop();
}

/// <summary>
/// Creates timers. The callback is supplied at creation rather than as an event, because
/// every timer in the engine has exactly one subscriber and a missed subscription would be
/// a silently dead schedule.
/// </summary>
public interface ITimerFactory
{
    IAppTimer Create(TimeSpan interval, Action onTick);
}
