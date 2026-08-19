using OView.App;
using OView.App.Platform;
using OView.Linux.Tray;

namespace OView.Linux.Tests;

/// <summary>
/// The marshalling that stops a bus callback touching the toolkit from the wrong thread
/// (issue #143 — <c>segmentation fault (core dumped)</c> on the first left click).
///
/// <para>Nothing here can prove the app no longer crashes: that needs a live session bus and
/// an Avalonia dispatcher in one process, which no machine in this project has — the gap that
/// let the #124 deadlock ship in the first place. What it can prove is that the work is
/// <b>handed to the dispatcher instead of run where the bus raised it</b>, which is the whole
/// of the fix and the part a later edit could quietly undo.</para>
/// </summary>
public class BusCallbackTests
{
    /// <summary>Queues work instead of running it, so "posted" and "ran" stay distinguishable.</summary>
    private sealed class QueueingDispatcher : IUiDispatcher
    {
        public List<Action> Posted { get; } = [];

        public void Post(Action work) => Posted.Add(work);

        public void Pump()
        {
            foreach (var work in Posted.ToList())
            {
                work();
            }
        }
    }

    private sealed class DeadDispatcher : IUiDispatcher
    {
        public void Post(Action work) => throw new InvalidOperationException("dispatcher has shut down");
    }

    private sealed class RecordingLog : IAppLog
    {
        public List<string> Lines { get; } = [];

        public void Write(string message) => Lines.Add(message);
    }

    /// <summary>
    /// The defect itself. Running inline is what put an X11 window construction on Tmds.DBus's
    /// thread; the callback must return having only queued the work.
    /// </summary>
    [Fact]
    public void WorkIsPostedToTheDispatcherRatherThanRunInline()
    {
        var dispatcher = new QueueingDispatcher();
        var ran = false;

        var handler = BusCallback.For(dispatcher, () => ran = true);
        handler(this, EventArgs.Empty);

        Assert.False(ran);                     // NOT on the bus thread
        Assert.Single(dispatcher.Posted);

        dispatcher.Pump();
        Assert.True(ran);                      // on the UI thread, once
    }

    [Fact]
    public void EachInvocationPostsItsOwnWork()
    {
        var dispatcher = new QueueingDispatcher();
        var runs = 0;

        var handler = BusCallback.For(dispatcher, () => runs++);
        handler(this, EventArgs.Empty);
        handler(this, EventArgs.Empty);
        dispatcher.Pump();

        Assert.Equal(2, runs);
    }

    /// <summary>
    /// "Exit O-view" is itself one of these callbacks, so a dispatcher that has already gone
    /// is reachable rather than theoretical. Dropping the work is right — there is no UI
    /// thread left to run it on — but it must not throw back across the bus.
    /// </summary>
    [Fact]
    public void ADeadDispatcherIsSurvivedAndNamedInTheLog()
    {
        var log = new RecordingLog();

        var handler = BusCallback.For(new DeadDispatcher(), () => { }, log);
        handler(this, EventArgs.Empty);        // must not throw

        Assert.Contains(log.Lines, l => l.Contains("bus callback dropped", StringComparison.Ordinal));
    }

    /// <summary>
    /// A handler built here is identifiable by its target type, which is what lets the wiring
    /// tests assert on the real delegate the head attaches instead of on a copy of it.
    /// </summary>
    [Fact]
    public void HandlersAreIdentifiableByTheirDeclaringType()
    {
        var handler = BusCallback.For(new QueueingDispatcher(), () => { });

        Assert.Equal(typeof(BusCallback), handler.Method.DeclaringType);
    }
}
