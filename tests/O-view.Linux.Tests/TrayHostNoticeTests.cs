using OView.Linux.Tray;
using OView.Presentation.Text;

namespace OView.Linux.Tests;

/// <summary>
/// The notice that O-view has nowhere to show its icon, and the one thing that was wrong with
/// it: its timing.
///
/// <para><b>What it got wrong.</b> Every login on a stock GNOME desktop opened with "O-view is
/// running, but has nowhere to show its icon", and then the icon appeared. The probe has to
/// run before the toolkit starts, and it was reading the bus some tens of milliseconds before
/// the session registered its own watcher. Nothing was broken except the order of two
/// events.</para>
///
/// <para><b>Why these are unit tests and not runtime ones.</b> CI's <c>linux-runtime</c> job
/// runs with a stand-in tray host present, so the no-host path never executes there, the
/// notice it would have to catch is one that is correctly never shown. Every wait here is
/// injected, so no test sleeps and none of them needs a session bus.</para>
/// </summary>
public class TrayHostNoticeTests
{
    /// <summary>
    /// The fix, stated as the thing that used to happen: a host that turns up while the notice
    /// is still being held back means the user is never told anything at all.
    /// </summary>
    [Fact]
    public async Task AHostThatAppearsInsideTheWaitIsNeverAnnounced()
    {
        var notifier = new RecordingNotifier();
        var iconReshown = 0;

        // The wait never finishes: whatever the notice does here, it does not do it because
        // ten seconds passed.
        await new TrayHostNotice(
                notifier.Show,
                notifier.Close,
                () => iconReshown++,
                delay: (_, _) => Never(),
                hostArrives: _ => Task.FromResult(true))
            .RunAsync(TrayHostState.Absent);

        Assert.Empty(notifier.Shown);
        Assert.Equal(1, iconReshown);   // the icon's own recovery still runs
    }

    /// <summary>
    /// The other half of the same claim: nothing is said <i>before</i> the wait is over. Driven
    /// by a wait this test completes by hand, so the assertion is about the order of the two
    /// events rather than about any duration.
    /// </summary>
    [Fact]
    public async Task NothingIsSaidUntilTheWaitIsOver()
    {
        var notifier = new RecordingNotifier();
        var wait = new TaskCompletionSource();

        var running = new TrayHostNotice(
                notifier.Show,
                notifier.Close,
                () => { },
                delay: (_, _) => wait.Task,
                hostArrives: _ => Task.FromResult(false))
            .RunAsync(TrayHostState.Absent);

        Assert.Empty(notifier.Shown);

        wait.SetResult();
        await WithoutHanging(running);

        Assert.Equal(
            (NoticeText.NoTrayHostTitle, NoticeText.TrayHostAbsent),
            Assert.Single(notifier.Shown));
    }

    /// <summary>
    /// A desktop that really has no notification area is still told, in the words it was always
    /// told in, including the state no notice ever used to show, so that a reading cannot fall
    /// through to a sentence written for a different one.
    /// </summary>
    [Theory]
    [InlineData(TrayHostState.Absent)]
    [InlineData(TrayHostState.Unknown)]
    public async Task ADesktopWithNoNotificationAreaIsToldInPresentationsWords(TrayHostState state)
    {
        var notifier = new RecordingNotifier();

        await new TrayHostNotice(
                notifier.Show,
                notifier.Close,
                () => { },
                delay: (_, _) => Task.CompletedTask,
                hostArrives: _ => Task.FromResult(false))
            .RunAsync(state);

        Assert.Equal(
            (NoticeText.NoTrayHostTitle, SniHostProbe.Explain(state)),
            Assert.Single(notifier.Shown));
    }

    /// <summary>
    /// A host that arrives after the notice has been shown takes it off the screen as well as
    /// bringing the icon back. Leaving it up is the pairing that reads worst of all: a
    /// notification saying there is nowhere to draw the icon, beside the icon.
    /// </summary>
    [Fact]
    public async Task AHostThatAppearsAfterTheNoticeClosesItAndBringsTheIconBack()
    {
        var notifier = new RecordingNotifier();
        var host = new TaskCompletionSource<bool>();
        var iconReshown = 0;

        var running = new TrayHostNotice(
                notifier.Show,
                notifier.Close,
                () => iconReshown++,
                delay: (_, _) => Task.CompletedTask,
                hostArrives: _ => host.Task)
            .RunAsync(TrayHostState.Absent);

        Assert.Single(notifier.Shown);   // the wait is over, so it has already spoken

        host.SetResult(true);
        await WithoutHanging(running);

        Assert.Equal(RecordingNotifier.FirstId, Assert.Single(notifier.Closed));
        Assert.Equal(1, iconReshown);
    }

    /// <summary>
    /// A bus that cannot be watched is not a host. The watch reports false for both "nothing is
    /// there" and "I could not ask", and only the first of those would justify saying nothing,
    /// so the notice is shown, and the icon's recovery, which has nothing left to wait on, is
    /// not claimed to have happened.
    /// </summary>
    [Fact]
    public async Task ABusThatCannotBeWatchedStillGetsTheNoticeAndNoRecovery()
    {
        var notifier = new RecordingNotifier();
        var iconReshown = 0;

        await new TrayHostNotice(
                notifier.Show,
                notifier.Close,
                () => iconReshown++,
                delay: (_, _) => Task.CompletedTask,
                hostArrives: _ => Task.FromResult(false))
            .RunAsync(TrayHostState.Unknown);

        Assert.Single(notifier.Shown);
        Assert.Empty(notifier.Closed);
        Assert.Equal(0, iconReshown);
    }

    /// <summary>
    /// Exiting during the wait abandons the notice: the menu's Exit row cancels this, and an
    /// app on its way out has nobody to tell.
    /// </summary>
    [Fact]
    public async Task ExitingDuringTheWaitAbandonsTheNotice()
    {
        var notifier = new RecordingNotifier();
        using var exiting = new CancellationTokenSource();

        var running = new TrayHostNotice(
                notifier.Show,
                notifier.Close,
                () => Assert.Fail("the icon was re-shown while the app was exiting"),
                delay: (_, token) => Task.Delay(Timeout.Infinite, token),
                hostArrives: _ => Never<bool>())
            .RunAsync(TrayHostState.Absent, exiting.Token);

        exiting.Cancel();
        await WithoutHanging(running);   // never faults: cancellation is an ordinary exit here

        Assert.Empty(notifier.Shown);
    }

    /// <summary>
    /// The wait the app actually uses, as a number rather than as "a wait". It is the one
    /// property of this that a user feels, and the one a test cannot afford to take.
    /// </summary>
    [Fact]
    public void TheWaitIsAboutTenSecondsUnlessATestSaysOtherwise()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), TrayHostNotice.DefaultGrace);

        var notifier = new RecordingNotifier();
        Assert.Equal(
            TrayHostNotice.DefaultGrace,
            new TrayHostNotice(notifier.Show, notifier.Close, () => { }).Grace);
    }

    /// <summary>A notification daemon as a list of what it was asked to show and to close.</summary>
    private sealed class RecordingNotifier
    {
        /// <summary>The id the first notification is filed under, as a real daemon would.</summary>
        internal const uint FirstId = 7;

        internal List<(string Title, string Body)> Shown { get; } = [];

        internal List<uint> Closed { get; } = [];

        internal Task<uint?> Show(string title, string body)
        {
            Shown.Add((title, body));
            return Task.FromResult<uint?>(FirstId + (uint)Shown.Count - 1);
        }

        internal Task Close(uint id)
        {
            Closed.Add(id);
            return Task.CompletedTask;
        }
    }

    /// <summary>A wait that is never over, and a watch that never reports.</summary>
    private static Task Never() => new TaskCompletionSource().Task;

    /// <inheritdoc cref="Never()"/>
    private static Task<T> Never<T>() => new TaskCompletionSource<T>().Task;

    /// <summary>
    /// Fails the test rather than hanging the suite. Every wait in here is injected, so a run
    /// that takes seconds is a notice waiting on something real, which is the defect, not a
    /// slow machine.
    /// </summary>
    private static Task WithoutHanging(Task running) => running.WaitAsync(TimeSpan.FromSeconds(5));
}
