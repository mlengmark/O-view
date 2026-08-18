namespace OView.App.Platform;

/// <summary>
/// Hands a piece of work back to the thread the head draws on.
///
/// <para><b>Why the engine needs one.</b> A poll is file discovery, JSON parsing and SQLite
/// writes, and its cost scales with the size of the user's transcript history — the first
/// machine to arrive with 563 MB of it spent that whole ingest with a frozen tray icon and
/// an unresponsive menu, because the poll ran on the dispatcher (issue #125). So the read
/// moves to the thread pool, and this is how the result comes home.</para>
///
/// <para><b>It is not optional dressing on the timers.</b> <see cref="ITimerFactory"/>
/// already exists so ticks <i>arrive</i> on the UI thread; this exists so results
/// <i>return</i> to it. Both heads wire their event handlers straight to the engine's
/// events and touch UI from them — <c>TrayController.Render</c> is a direct subscriber —
/// so anything raised off the UI thread would be a thread-affinity violation at best and a
/// silent corruption at worst.</para>
///
/// <para>An engine started without one publishes inline on the calling thread. That is the
/// right behaviour for tests, which want a poll to be finished when <c>Refresh</c> returns,
/// and it is what makes the seam's absence mean "there is no separate UI thread here"
/// rather than "someone forgot".</para>
/// </summary>
public interface IUiDispatcher
{
    /// <summary>
    /// Queues <paramref name="work"/> on the UI thread and returns immediately. May throw
    /// if the dispatcher has already shut down; callers treat that as "there is nothing
    /// left to draw on" rather than as an error.
    /// </summary>
    void Post(Action work);
}
