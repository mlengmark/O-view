using OView.App.Platform;
using OView.Core.Models;
using OView.Core.Providers;

namespace OView.App.Tests;

/// <summary>A clock the test moves by hand, so cadence and windows are exercised without waiting.</summary>
public sealed class FakeClock(DateTimeOffset start) : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = start;

    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>
/// A timer that never fires on its own — the test ticks it. Records start/stop so a
/// schedule can be asserted rather than inferred from timing.
/// </summary>
public sealed class FakeTimer(TimeSpan interval, Action onTick) : IAppTimer
{
    public TimeSpan Interval { get; set; } = interval;

    public bool IsRunning { get; private set; }

    public bool Disposed { get; private set; }

    public void Start() => IsRunning = true;

    public void Stop() => IsRunning = false;

    public void Dispose() => Disposed = true;

    /// <summary>Fire the timer, as the real one would when its interval elapses.</summary>
    public void Tick()
    {
        if (IsRunning)
        {
            onTick();
        }
    }
}

/// <summary>Hands out <see cref="FakeTimer"/>s in creation order, which the engine's <c>Start</c> fixes.</summary>
public sealed class FakeTimerFactory : ITimerFactory
{
    public List<FakeTimer> Created { get; } = [];

    // These are positional, so they track the order UsageEngine.Start creates its timers.
    // Inserting one in the middle silently re-points every accessor after it — which is how
    // adding the plan-history cadence (issue #163) made the update-check test read the wrong
    // timer. If another is added, fix the indices here rather than reordering Start.

    /// <summary>The full poll — transcripts and the 31-day figures. Created first.</summary>
    public FakeTimer Poll => Created[0];

    /// <summary>The plan-history-only cadence (issue #163).</summary>
    public FakeTimer PlanPoll => Created[1];

    /// <summary>The one-shot first update check.</summary>
    public FakeTimer FirstUpdateCheck => Created[2];

    /// <summary>The recurring update check.</summary>
    public FakeTimer RecurringUpdateCheck => Created[3];

    public IAppTimer Create(TimeSpan interval, Action onTick)
    {
        var timer = new FakeTimer(interval, onTick);
        Created.Add(timer);
        return timer;
    }
}

/// <summary>
/// Stands in for the head's UI thread. Nothing is run until the test pumps it, which is
/// what makes "was this raised on the UI thread or on the reading thread?" an assertion
/// rather than a race.
/// </summary>
public sealed class FakeDispatcher : IUiDispatcher
{
    private readonly List<Action> _queued = [];

    /// <summary>The thread that constructed this — the stand-in for the UI thread.</summary>
    public int OwningThreadId { get; } = Environment.CurrentManagedThreadId;

    public int Pending
    {
        get { lock (_queued) { return _queued.Count; } }
    }

    public void Post(Action work)
    {
        lock (_queued) { _queued.Add(work); }
    }

    /// <summary>Runs everything queued so far, on the calling thread. Returns how many ran.</summary>
    public int Pump()
    {
        Action[] due;
        lock (_queued)
        {
            due = [.. _queued];
            _queued.Clear();
        }

        foreach (var work in due)
        {
            work();
        }
        return due.Length;
    }

    /// <summary>
    /// Pumps until something is due or the wait expires. The engine posts from the thread
    /// pool, so a test has to allow for the read not having finished yet.
    /// </summary>
    public bool PumpWithin(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Pump() > 0)
            {
                return true;
            }
            Thread.Sleep(5);
        }
        return false;
    }
}

/// <summary>A provider whose answer the test sets, including "throw" to exercise the failure path.</summary>
public sealed class FakeProvider : IUsageProvider
{
    public UsageSnapshot Next { get; set; } = UsageSnapshot.None;

    public Exception? ThrowOnNext { get; set; }

    public int Calls { get; private set; }

    /// <summary>Blocks every read until released — stands in for a large first ingest.</summary>
    public ManualResetEventSlim? BlockUntil { get; set; }

    /// <summary>The thread each read ran on, so "off the UI thread" can be asserted.</summary>
    public List<int> ReadThreadIds { get; } = [];

    public UsageSnapshot GetSnapshot(DateTimeOffset utcNow)
    {
        lock (ReadThreadIds) { ReadThreadIds.Add(Environment.CurrentManagedThreadId); }
        Calls++;
        BlockUntil?.Wait(TimeSpan.FromSeconds(10));
        if (ThrowOnNext is { } ex)
        {
            ThrowOnNext = null;
            throw ex;
        }
        return Next;
    }

    /// <summary>Convenience: a live snapshot at a given session percentage.</summary>
    public void SetSession(int percent, DataSource source = DataSource.Live) =>
        Next = new UsageSnapshot(source, percent, null, null, DateTimeOffset.UnixEpoch);
}

/// <summary>Captures log lines so a test can assert what was recorded without a file.</summary>
public sealed class ListLog : IAppLog
{
    public List<string> Lines { get; } = [];

    public void Write(string message) => Lines.Add(message);
}

/// <summary>
/// A disposable temp directory, so an engine can be pointed at real files — the rollup
/// store is real SQLite and the settings and reset log are real JSON, which is the point:
/// these tests exercise the actual storage paths, not stand-ins for them.
/// </summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "oview-app-tests", Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
