using OView.App.Platform;
using OView.Core.Models;

namespace OView.App.Tests;

/// <summary>
/// Nothing reaches a skin except through its dispatcher (OVIAA-67).
///
/// <para>Both skins touch their tray icon straight from these calls, so a push that arrives
/// on whichever thread happened to raise it is a thread-affinity fault in the skin — and one
/// that only shows on the machine whose first ingest is slow enough to run off the UI thread
/// (issue #125). <see cref="ShellHost"/> is the one place that can rule it out, by posting
/// every push.</para>
///
/// <para><b>What makes these assertions bite:</b> each one drives the shell from the test
/// thread, with nothing dispatched around it, and then checks the skin was <i>not</i> called
/// before the dispatcher ran. A <see cref="ShellHost"/> that invoked the skin directly would
/// pass any "was this the UI thread?" check here, because the test thread <i>is</i> the UI
/// thread — so the question asked instead is "did it wait for the dispatcher?".</para>
///
/// <para>The fakes are local on purpose: this file owns the one thing it asserts, and
/// <c>Fakes.cs</c>'s dispatcher runs work for a different test's reasons.</para>
/// </summary>
public class ShellToSkinDispatchTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    /// <summary>How long a dispatcher-driven test waits for a thread-pool read to land.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Holds everything until the test pumps it, and says while it is pumping that whatever
    /// runs now came from here — which is what lets the skin tell a posted call from a direct
    /// one on the very same thread.
    /// </summary>
    private sealed class GatedDispatcher : IUiDispatcher
    {
        private readonly List<Action> _queued = [];

        /// <summary>True only inside <see cref="Pump"/>.</summary>
        public bool Dispatching { get; private set; }

        public int Pending
        {
            get { lock (_queued) { return _queued.Count; } }
        }

        public void Post(Action work)
        {
            lock (_queued) { _queued.Add(work); }
        }

        /// <summary>Runs what is queued now and returns how much that was. Work posted by
        /// that work waits for the next pump, as it would on a real message pump.</summary>
        public int Pump()
        {
            Action[] due;
            lock (_queued)
            {
                due = [.. _queued];
                _queued.Clear();
            }

            Dispatching = true;
            try
            {
                foreach (var work in due)
                {
                    work();
                }
            }
            finally
            {
                Dispatching = false;
            }

            return due.Length;
        }
    }

    /// <summary>
    /// A skin that records what it was told, in order, and counts anything that reached it
    /// while the dispatcher was not running — which must stay at zero.
    /// </summary>
    private sealed class RecordingSkin(GatedDispatcher dispatcher) : IShellToSkin
    {
        public List<string> Calls { get; } = [];

        public List<UsageSnapshot> Snapshots { get; } = [];

        public int ArrivedUndispatched { get; private set; }

        public void ShowSnapshot(UsageSnapshot snapshot)
        {
            Record("snapshot");
            Snapshots.Add(snapshot);
        }

        public void ShowNotification(AppNotification notification) => Record("notification");

        public void UpdateCheckDue() => Record("update check");

        public void RateCheckDue() => Record("rate check");

        private void Record(string call)
        {
            Calls.Add(call);
            if (!dispatcher.Dispatching)
            {
                ArrivedUndispatched++;
            }
        }
    }

    private sealed record Harness(
        ShellHost Host,
        FakeProvider Provider,
        GatedDispatcher Dispatcher,
        RecordingSkin Skin,
        FakeTimerFactory Timers,
        ISkinToShell BuiltFrom);

    private static Harness NewHarness(TempDir dir)
    {
        var provider = new FakeProvider();
        var dispatcher = new GatedDispatcher();
        RecordingSkin? skin = null;
        ISkinToShell? builtFrom = null;

        var host = new ShellHost(
            new UsageEngineOptions
            {
                Clock = new FakeClock(T0),
                Provider = provider,
                RollupDbPath = dir.File("usage.db"),
                WeeklyResetAnchorPath = dir.File("weekly-resets.json"),
                SettingsPath = dir.File("settings.json"),
            },
            dispatcher,
            shell =>
            {
                builtFrom = shell;
                return skin = new RecordingSkin(dispatcher);
            });

        return new Harness(host, provider, dispatcher, skin!, new FakeTimerFactory(), builtFrom!);
    }

    /// <summary>
    /// The shell a skin is built from is the shell it keeps being handed — a second one would
    /// mean two engines, and the one thing this class exists to guarantee is that there is
    /// exactly one.
    /// </summary>
    [Fact]
    public void TheSkinIsBuiltFromTheShellItAsksThrough()
    {
        using var dir = new TempDir();
        var h = NewHarness(dir);
        using var host = h.Host;

        Assert.Same(h.Host.Shell, h.BuiltFrom);
        Assert.Equal(UsageSnapshot.None, h.Host.Shell.Latest);
    }

    /// <summary>
    /// A refresh publishes on the calling thread, so a snapshot pushed straight at the skin
    /// would already be drawn when this returns. It must not be.
    /// </summary>
    [Fact]
    public void ASnapshotWaitsForTheDispatcher()
    {
        using var dir = new TempDir();
        var h = NewHarness(dir);
        using var host = h.Host;

        h.Provider.SetSession(42);
        h.Host.Shell.Refresh();

        Assert.Empty(h.Skin.Calls);
        Assert.Equal(1, h.Dispatcher.Pump());

        Assert.Equal(["snapshot"], h.Skin.Calls);
        Assert.Equal(42, h.Skin.Snapshots.Single().SessionPercentValue.Value);
        Assert.Equal(0, h.Skin.ArrivedUndispatched);
    }

    /// <summary>
    /// The same for a notification, which the shell raises in the same breath as the snapshot
    /// that crossed the threshold — and in that order, which the hop must preserve.
    /// </summary>
    [Fact]
    public void ANotificationWaitsForTheDispatcherAndStaysBehindItsSnapshot()
    {
        using var dir = new TempDir();
        var h = NewHarness(dir);
        using var host = h.Host;

        h.Provider.SetSession(10);          // arms the threshold
        h.Host.Shell.Refresh();
        h.Dispatcher.Pump();
        h.Skin.Calls.Clear();

        h.Provider.SetSession(95);          // crosses it
        h.Host.Shell.Refresh();

        Assert.Empty(h.Skin.Calls);
        h.Dispatcher.Pump();

        Assert.Equal(["snapshot", "notification"], h.Skin.Calls);
        Assert.Equal(0, h.Skin.ArrivedUndispatched);
    }

    /// <summary>
    /// The two "it is time to go and look" pushes, which a timer tick raises on whatever
    /// thread the tick arrived on. Neither carries data, so posting them is the only thing
    /// that makes them safe for a skin that opens a dialog from one of them.
    /// </summary>
    [Fact]
    public void TheUpdateAndRateChecksWaitForTheDispatcher()
    {
        using var dir = new TempDir();
        var h = NewHarness(dir);
        using var host = h.Host;

        h.Provider.SetSession(5);
        h.Host.Start(h.Timers);

        // Start's first read runs off this thread and publishes back through the dispatcher;
        // let it finish so the counts below are only about the two checks.
        Assert.True(
            SpinWait.SpinUntil(() => h.Dispatcher.Pending > 0, Patience),
            "the first poll never published its result");
        h.Dispatcher.Pump();   // the shell's own publish
        h.Dispatcher.Pump();   // the snapshot it posted for the skin
        Assert.Equal(["snapshot"], h.Skin.Calls);
        h.Skin.Calls.Clear();

        h.Timers.FirstUpdateCheck.Tick();
        h.Timers.FirstRateCheck.Tick();

        Assert.Empty(h.Skin.Calls);
        Assert.Equal(2, h.Dispatcher.Pump());

        Assert.Equal(["update check", "rate check"], h.Skin.Calls);
        Assert.Equal(0, h.Skin.ArrivedUndispatched);
    }
}
