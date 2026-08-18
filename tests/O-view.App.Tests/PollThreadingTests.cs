using OView.Core.Models;

namespace OView.App.Tests;

/// <summary>
/// A scheduled poll reads off the UI thread and publishes back onto it (issue #125).
///
/// <para>The bug these pin was not a crash. The read — file discovery, JSON parsing, SQLite
/// writes — ran on the dispatcher, and its cost scales with total transcript history on a
/// first run. The first machine with a large enough history spent that whole ingest with a
/// frozen tray icon and an unresponsive menu, and nothing in the suite noticed because
/// every test drives the engine on one thread anyway.</para>
///
/// <para>So the assertions here are about <i>which thread</i>, not about what the numbers
/// say. <see cref="UsageEngineTests"/> owns the numbers.</para>
/// </summary>
public class PollThreadingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static UsageEngine NewEngine(TempDir dir, FakeProvider provider) => new(new UsageEngineOptions
    {
        Clock = new FakeClock(T0),
        Provider = provider,
        RollupDbPath = dir.File("usage.db"),
        WeeklyResetLogPath = dir.File("weekly-resets.json"),
        SettingsPath = dir.File("settings.json"),
    });

    /// <summary>
    /// The regression itself. <c>Start</c> must return while the read is still going, or
    /// the head cannot draw anything until the first ingest finishes.
    /// </summary>
    [Fact]
    public void StartDoesNotWaitForTheFirstReadWhenADispatcherIsSupplied()
    {
        using var dir = new TempDir();
        using var blocked = new ManualResetEventSlim(false);
        var provider = new FakeProvider { BlockUntil = blocked };
        provider.SetSession(42);

        using var engine = NewEngine(dir, provider);
        var dispatcher = new FakeDispatcher();

        engine.Start(new FakeTimerFactory(), dispatcher);

        // Start has returned with the provider still inside GetSnapshot. Nothing has been
        // published yet, and the UI thread — this one — is free.
        Assert.Equal(UsageSnapshot.None, engine.Latest);
        Assert.Equal(0, dispatcher.Pending);

        blocked.Set();

        Assert.True(dispatcher.PumpWithin(Patience), "the poll never published its result");
        Assert.Equal(42, engine.Latest.SessionPercent);
    }

    /// <summary>
    /// The read runs somewhere else; the publish runs here. Both halves matter — a read on
    /// the UI thread is the freeze, and a publish off it is a thread-affinity violation in
    /// both heads, which subscribe to these events and touch their tray icons directly.
    /// </summary>
    [Fact]
    public void TheReadRunsOffTheUiThreadAndTheEventsArriveOnIt()
    {
        using var dir = new TempDir();
        var provider = new FakeProvider();
        provider.SetSession(51);

        using var engine = NewEngine(dir, provider);
        var dispatcher = new FakeDispatcher();

        var raisedOn = new List<int>();
        engine.SnapshotUpdated += _ => raisedOn.Add(Environment.CurrentManagedThreadId);

        engine.Start(new FakeTimerFactory(), dispatcher);
        Assert.True(dispatcher.PumpWithin(Patience), "the poll never published its result");

        Assert.DoesNotContain(dispatcher.OwningThreadId, provider.ReadThreadIds);
        Assert.Equal([dispatcher.OwningThreadId], raisedOn);
    }

    /// <summary>
    /// A poll slower than the interval must drop the next tick rather than stack another
    /// read behind it. Two concurrent ingests over the same store would contend for the
    /// connection and, worse, double the work that made the app unresponsive to begin with.
    /// </summary>
    [Fact]
    public void ATickArrivingWhileAPollIsStillReadingIsDropped()
    {
        using var dir = new TempDir();
        using var blocked = new ManualResetEventSlim(false);
        var provider = new FakeProvider { BlockUntil = blocked };
        provider.SetSession(7);

        using var engine = NewEngine(dir, provider);
        var timers = new FakeTimerFactory();
        var log = new ListLog();

        engine.Start(timers, new FakeDispatcher());

        // The initial poll is inside GetSnapshot. Fire the timer repeatedly on top of it.
        SpinWait.SpinUntil(() => provider.Calls == 1, Patience);
        timers.Poll.Tick();
        timers.Poll.Tick();

        Assert.Equal(1, provider.Calls);

        blocked.Set();
    }

    /// <summary>
    /// With no dispatcher there is no separate UI thread to protect, and a poll must finish
    /// before <c>Refresh</c> returns. Every other test in this suite depends on that, and it
    /// is the behaviour the seam's absence is defined to mean.
    /// </summary>
    [Fact]
    public void WithoutADispatcherAPollIsFullySynchronousOnTheCallingThread()
    {
        using var dir = new TempDir();
        var provider = new FakeProvider();
        provider.SetSession(63);

        using var engine = NewEngine(dir, provider);
        engine.Start(new FakeTimerFactory());

        Assert.Equal(63, engine.Latest.SessionPercent);
        Assert.Equal([Environment.CurrentManagedThreadId], provider.ReadThreadIds);
    }

    /// <summary>
    /// A read that throws still has to reach the publish, or the cadence is never re-timed
    /// and a warming-up engine stays stuck at its fast interval forever.
    /// </summary>
    [Fact]
    public void AFailedReadStillPublishesAndKeepsThePreviousSnapshot()
    {
        using var dir = new TempDir();
        var provider = new FakeProvider();
        provider.SetSession(30);

        using var engine = NewEngine(dir, provider);
        var timers = new FakeTimerFactory();
        var dispatcher = new FakeDispatcher();

        engine.Start(timers, dispatcher);
        Assert.True(dispatcher.PumpWithin(Patience));
        Assert.Equal(30, engine.Latest.SessionPercent);

        provider.ThrowOnNext = new InvalidOperationException("store is unreadable");
        timers.Poll.Tick();

        Assert.True(dispatcher.PumpWithin(Patience), "a failed poll published nothing at all");
        Assert.Equal(30, engine.Latest.SessionPercent);   // the previous state, kept
    }
}
