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

    /// <summary>The poll timer — the first the engine creates.</summary>
    public FakeTimer Poll => Created[0];

    /// <summary>The one-shot first update check.</summary>
    public FakeTimer FirstUpdateCheck => Created[1];

    /// <summary>The recurring update check.</summary>
    public FakeTimer RecurringUpdateCheck => Created[2];

    public IAppTimer Create(TimeSpan interval, Action onTick)
    {
        var timer = new FakeTimer(interval, onTick);
        Created.Add(timer);
        return timer;
    }
}

/// <summary>A provider whose answer the test sets, including "throw" to exercise the failure path.</summary>
public sealed class FakeProvider : IUsageProvider
{
    public UsageSnapshot Next { get; set; } = UsageSnapshot.None;

    public Exception? ThrowOnNext { get; set; }

    public int Calls { get; private set; }

    public UsageSnapshot GetSnapshot(DateTimeOffset utcNow)
    {
        Calls++;
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
