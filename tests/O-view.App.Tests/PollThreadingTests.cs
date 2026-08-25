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

    private static UsageEngine NewEngine(
        TempDir dir, FakeProvider provider, IClock? clock = null, IAppLog? log = null) =>
        new(new UsageEngineOptions
        {
            Clock = clock ?? new FakeClock(T0),
            Log = log,
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

    /// <summary>
    /// A read that throws <i>outright</i>, rather than returning a failure result, must not
    /// take the cadence with it.
    ///
    /// <para>This is a different failure from the one above, and the distinction is the whole
    /// bug. There, the provider throws from inside <c>Read</c>'s own try and is caught, so a
    /// <see cref="UsageEngine"/> poll still returns and still releases the gate. Here nothing
    /// catches it: the exception escapes the runner's lambda, the gate is released nowhere
    /// else on that path, and <c>Task.Run</c> discards the result so the exception is never
    /// observed. <c>_busy</c> then stays set for the life of the process and every later tick
    /// is dropped by <c>TryEnter</c>.</para>
    ///
    /// <para>What that looked like in the field: a tray process alive for 35 minutes on a 60 s
    /// cadence, holding its store open, with 409 KB of unread transcript and a ledger whose
    /// newest row was five days old — while the store passed every health check, the panel
    /// went on drawing its last snapshot, and nothing was written anywhere to say why.</para>
    ///
    /// <para><b>The assertion that matters is the last one: the poll AFTER the failed one
    /// runs.</b> Everything before it is setup.</para>
    /// </summary>
    [Fact]
    public void AReadThatThrowsOutrightStillReleasesTheGate()
    {
        using var dir = new TempDir();
        var provider = new FakeProvider();
        provider.SetSession(30);

        var clock = new FakeClock(T0);
        var log = new ListLog();
        using var engine = NewEngine(dir, provider, clock, log);
        var timers = new FakeTimerFactory();
        var dispatcher = new FakeDispatcher();

        engine.Start(timers, dispatcher);
        Assert.True(dispatcher.PumpWithin(Patience));
        Assert.Equal(1, provider.Calls);

        // The full poll reads the clock before entering its own guard, so this throws from
        // the one place a provider fake cannot reach.
        clock.ThrowOnNext = new InvalidOperationException("the clock is unavailable");
        timers.Poll.Tick();

        // Wait for the failed poll to finish rather than racing it — and assert on the way
        // past that the failure was recorded at all, because a silent one is what made this
        // undiagnosable from a support bundle.
        Assert.True(
            SpinWait.SpinUntil(
                () => log.Lines.Any(l => l.StartsWith("poll read FAILED", StringComparison.Ordinal)),
                Patience),
            "a read that threw outright was not recorded anywhere");

        Assert.Equal(1, provider.Calls);   // it never reached the provider

        timers.Poll.Tick();

        Assert.True(dispatcher.PumpWithin(Patience), "the cadence never recovered from a failed read");
        Assert.Equal(2, provider.Calls);
        Assert.Equal(30, engine.Latest.SessionPercent);
    }
}
