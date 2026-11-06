using OView.App;

namespace OView.Linux.Tray;

/// <summary>
/// Keeps the tray icon off the session bus until there is a reading to put in it, and for no
/// longer than <see cref="DefaultPatience"/>.
///
/// <para><b>Why the icon is not simply published grey and repainted.</b> That was the order
/// here, and a repaint a host does not hear about changes nothing on screen. Publishing the
/// no-data ring a moment before the first reading leaves the whole correction resting on one
/// <c>NewIcon</c> signal, sent while the host is still setting the item up: GLib's
/// <c>GDBusProxy</c> drops every signal that lands before its own initialisation finishes, and
/// Ubuntu 24.04's AppIndicators extension does not read the icon again afterwards. A host that
/// loses that race keeps the grey ring until the next poll, up to a minute later. The first
/// thing a host reads has to be right, because nothing guarantees it reads anything else.</para>
///
/// <para><b>Why it gives up.</b> The first read costs whatever the machine's transcript history
/// costs, minutes on a first run over hundreds of MB, and an icon that took that long to appear
/// would look like an app that had not started. So the wait is bounded, and past it the no-data
/// ring is the honest thing to show: nothing has been read yet.</para>
///
/// <para>Driven by the head's own timers, so everything here happens on the UI thread and no
/// test has to wait in real time.</para>
/// </summary>
public sealed class FirstReadingHold
{
    /// <summary>
    /// How long the icon waits for the first reading. A reading took 0.3 s on the desktop this
    /// was reported from and under a second on a CI runner; five seconds leaves room for a slow
    /// disk and is still "within seconds of launch" for someone with a very long history.
    /// </summary>
    public static readonly TimeSpan DefaultPatience = TimeSpan.FromSeconds(5);

    private readonly IAppTimer _patience;
    private readonly IAppLog? _log;
    private Action? _publish;

    /// <param name="publish">Puts the icon on the bus. Called exactly once, on the UI thread.</param>
    /// <param name="timers">The head's timer factory, whose ticks arrive on the UI thread.</param>
    /// <param name="log">Says which of the two ways the wait ended, the first thing a report needs.</param>
    /// <param name="patience">How long to wait; <see cref="DefaultPatience"/> unless a test says otherwise.</param>
    public FirstReadingHold(Action publish, ITimerFactory timers, IAppLog? log = null, TimeSpan? patience = null)
    {
        _publish = publish;
        _log = log;
        Patience = patience ?? DefaultPatience;
        _patience = timers.Create(Patience, PatienceRanOut);
    }

    /// <summary>How long this one waits. Readable so a test can name the wait it is driving.</summary>
    public TimeSpan Patience { get; }

    /// <summary>Starts the wait. Before the polling, so a slow first poll cannot outlast it.</summary>
    public void Start() => _patience.Start();

    /// <summary>
    /// The icon now holds what the first poll found, a reading or an honest "no data", so
    /// there is nothing left to wait for. Every later call does nothing.
    /// </summary>
    public void ReadingArrived() => Release("the first reading is in the icon");

    private void PatienceRanOut() =>
        Release($"no reading after {Patience.TotalSeconds:0}s, showing the no-data icon until one lands");

    private void Release(string why)
    {
        if (_publish is not { } publish)
        {
            return;
        }

        _publish = null;
        _patience.Dispose();

        _log?.Write($"tray published: {why}");
        publish();
    }
}
