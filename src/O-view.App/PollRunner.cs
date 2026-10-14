using OView.App.Platform;

namespace OView.App;

/// <summary>
/// Reads off the UI thread and publishes back onto it, under a gate so a slow read cannot
/// stack another behind it.
///
/// <para>Shared by all three of <see cref="UsageEngine"/>'s cadences — the full poll, the
/// plan-only poll and the usage-cache refresh — rather than written three times. They differ
/// in <i>what</i> they read and publish; the threading, the drop-don't-queue rule and the
/// shutdown handling are identical, and this file's history is one of behaviour written
/// twice and fixed once.</para>
///
/// <para>Split out of <see cref="UsageEngine"/> purely to shorten that file — same members,
/// same behaviour, same logging. <see cref="AttachDispatcher"/> is called from
/// <see cref="UsageEngine.Start"/>, since the dispatcher is not known until then; with none
/// attached, a run has no separate UI thread to protect and runs inline, which is what the
/// tests rely on.</para>
/// </summary>
public sealed class PollRunner
{
    private readonly IAppLog? _log;
    private IUiDispatcher? _dispatcher;

    public PollRunner(IAppLog? log)
    {
        _log = log;
    }

    /// <summary>Where a scheduled poll publishes back to. Null means "publish inline".</summary>
    public void AttachDispatcher(IUiDispatcher? dispatcher) => _dispatcher = dispatcher;

    /// <summary>
    /// One cadence's "a read is already in flight" flag. A type rather than an <c>int</c>
    /// field because there are several of them and <c>Interlocked</c> on a field cannot be
    /// passed to the shared runner.
    /// </summary>
    public sealed class Gate
    {
        private int _busy;

        public bool TryEnter() => Interlocked.Exchange(ref _busy, 1) == 0;

        public void Exit() => Volatile.Write(ref _busy, 0);
    }

    /// <summary>
    /// <para><b>The gate is released on every path out of the read, including a throw.</b>
    /// It has to be: nothing else on this path releases it, so one escaped exception left
    /// <c>_busy</c> set for the life of the process and every later tick was dropped by
    /// <see cref="Gate.TryEnter"/>. That failure is silent and total — the panel goes on
    /// drawing its last snapshot, no transcript is ingested, no observed weekly reset is
    /// written, and because <c>_log</c> is null unless <c>--log</c> was passed there is
    /// nothing on disk to say why. Observed in the field: a tray up for 35 minutes with a
    /// 60 s cadence, 409 KB of unread transcript, and a rollup store whose newest row was
    /// five days old while the store itself passed every health check.</para>
    /// </summary>
    public void Run<T>(Gate gate, string what, Func<T> read, Action<T> publish)
    {
        if (_dispatcher is not { } dispatcher)
        {
            // No separate UI thread to protect — run inline, which is what the tests rely on.
            publish(read());
            return;
        }

        // A poll slower than the interval must not stack another behind it. Dropping the
        // tick is right rather than queueing it: the next one reads the same files and
        // reports the same state, so a queue would only replay stale work.
        if (!gate.TryEnter())
        {
            _log?.Write($"{what} skipped — previous still running");
            return;
        }

        // Three lines per poll, and the gaps between them are the diagnosis. A tick that
        // never logs "read begin" means the timer is not firing; "read begin" with no "read
        // done" means the read is hung or threw; "read done" with no "published" means the
        // dispatcher is not running the callback — and each of those is a different bug in a
        // different layer. Without them a stalled poll and a healthy idle one are the same
        // silence, which is how five days of no ingestion went unnoticed.
        var startedAt = Environment.TickCount64;
        _log?.Write($"{what} read begin");

        _ = Task.Run(() =>
        {
            T result;
            try
            {
                result = read();
                _log?.Write($"{what} read done in {Environment.TickCount64 - startedAt} ms");
            }
            catch (Exception ex)
            {
                // Both reads already guard themselves and return a failure result rather
                // than throwing — but each only guards what sits inside its own try, and
                // neither covers what it touches before entering one. This is the backstop
                // for that gap, and for whatever a future read reaches for first.
                //
                // It is not defensive padding: the gate is released nowhere else on this
                // path, so without this the cadence is dead for the life of the process
                // after a single throw, and Task.Run discards the exception so nothing can
                // even report it. Releasing here costs one dropped tick instead.
                gate.Exit();
                _log?.Write($"{what} read FAILED {ex.GetType().Name}: {ex.Message}");
                return;
            }

            try
            {
                dispatcher.Post(() =>
                {
                    try
                    {
                        publish(result);
                        _log?.Write($"{what} published after {Environment.TickCount64 - startedAt} ms");
                    }
                    finally
                    {
                        gate.Exit();
                    }
                });
            }
            catch (Exception ex)
            {
                // The dispatcher is gone — the app is shutting down. Release the gate so
                // nothing is wedged, and drop the result: there is no longer anything to
                // draw it on.
                gate.Exit();
                _log?.Write($"{what} publish skipped {ex.GetType().Name}: {ex.Message}");
            }
        });
    }
}
