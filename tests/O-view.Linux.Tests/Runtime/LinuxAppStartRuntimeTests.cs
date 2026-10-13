using System.Diagnostics;
using OView.Linux.Tray;
using Xunit.Abstractions;
using Xunit.Sdk;

// Every runtime test that needs a notification area owns org.kde.StatusNotifierWatcher, and a
// bus name can only have one owner: two test classes doing it at once means one of them fails
// on a collision rather than on anything it was testing. xunit runs collections — and so
// classes — in parallel by default, so without this the stand-in host's own tests and this
// file's would race. The rest of the assembly is unit tests measured in milliseconds, so
// serialising the whole thing costs nothing worth having.
//
// It lives in this file because an assembly-level attribute has to live in some file, and this
// is the one that needs it; its own file would read better.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace OView.Linux.Tests.Runtime;

/// <summary>
/// The shipped Linux app, started on the virtual display with a stand-in notification area
/// waiting for it.
///
/// <para>This is the first test anywhere that runs the real head. Up to now the Linux skin
/// was proved by unit tests over its pieces, which is why every tray defect that reached a
/// desktop — a first click that deadlocked (#124), a menu that took tens of seconds (#125) —
/// got through: all of them needed the toolkit, the dispatcher and the bus together. The
/// click and the menu are tested in their own slices; what is proved here is the thing they
/// both stand on, that the app comes up, its icon arrives, and it stays up.</para>
///
/// <para><b>The icon arriving is asserted twice, for an area that was already there and for
/// one that appears afterwards.</b> Both failed before the icon was put in the application's
/// icon collection, and they failed in the telling way: the app came up healthy, found the
/// area and drew its gauge, while no <c>org.kde.StatusNotifierItem-…</c> name ever appeared
/// on the session bus. Either of them going red again means the item is not being published
/// at all, rather than published and refused.</para>
///
/// <para>Runs only in the <c>linux-runtime</c> job, which provides the display
/// (<c>xvfb-run</c>), the bus (<c>dbus-run-session</c>) and <c>OVIEW_LINUX_RUNTIME=1</c>.
/// Elsewhere there is nothing to start the app on, so each test returns early the same way
/// <see cref="LinuxRuntimeEnvironmentTests"/> does.</para>
/// </summary>
[Trait("Category", "LinuxRuntime")]
public class LinuxAppStartRuntimeTests(ITestOutputHelper output)
{
    private static bool InRuntimeJob => Environment.GetEnvironmentVariable("OVIEW_LINUX_RUNTIME") == "1";

    /// <summary>
    /// How long any one step may take. Generous because this is a shared CI runner starting a
    /// GUI toolkit cold: nothing here measures a duration, it only refuses to hang for ever.
    /// </summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The head on a desktop shaped like every desktop that ships a notification area: the
    /// area is there before the app starts, and the app has to come up on it.
    ///
    /// <para>Three things in one pass, because they are three stages of one start-up and a
    /// failure at any of them is the same report ("it doesn't work on Linux"): the app finds
    /// the notification area on the bus before the toolkit starts, it gets far enough to draw
    /// its gauge — which needs the toolkit, the engine and the dispatcher between them — and
    /// it is still running once it has. A head that renders once and dies is the failure this
    /// job exists to catch, and nothing short of a real process could see it.</para>
    /// </summary>
    [Fact]
    public async Task TheAppStartsOnTheVirtualDisplayAndCarriesOnRunning()
    {
        if (!InRuntimeJob) return;

        // The host first, deliberately: the app asks the bus for a notification area once,
        // before the toolkit starts, so one started afterwards would be a different case —
        // the one TheIconReachesAHostThatAppearedAfterTheApp covers.
        await using var host = await StandInTrayHost.StartAsync();
        await using var app = await LinuxAppProcess.StartAsync(output, nameof(TheAppStartsOnTheVirtualDisplayAndCarriesOnRunning));

        var startup = await app.WaitForLogAsync(LinuxAppProcess.StartupLogLine, Patience);
        Assert.Contains($"trayHost={TrayHostState.Present}", startup);

        // Drawn from the engine's first poll, which Start runs immediately, and reported from
        // the UI thread the dispatcher marshalled it onto. The one line that says the toolkit
        // is alive rather than merely launched.
        await app.WaitForLogAsync(LinuxAppProcess.IconRenderedLogLine, Patience);

        Assert.False(app.HasExited, "o-view exited after rendering its icon");
    }

    /// <summary>
    /// The icon reaching a notification area that was already there, which is the ordinary
    /// case on every desktop that ships one.
    /// </summary>
    [Fact]
    public async Task TheIconReachesTheStandInHostAndTheAppCarriesOnRunning()
    {
        if (!InRuntimeJob) return;

        // The host first, deliberately: the app asks the bus for a notification area once,
        // before the toolkit starts, and one started afterwards would be found by the retry
        // path instead — a different thing from the one under test.
        await using var host = await StandInTrayHost.StartAsync();
        await using var app = await LinuxAppProcess.StartAsync(output, nameof(TheIconReachesTheStandInHostAndTheAppCarriesOnRunning));

        var startup = await app.WaitForLogAsync(LinuxAppProcess.StartupLogLine, Patience);
        Assert.Contains($"trayHost={TrayHostState.Present}", startup);

        var registered = await app.WaitForAsync(
            host.WaitForItemAsync(Patience), Patience, "the icon to reach the stand-in host");

        // A unique bus name, assigned by the bus to the connection the registration came from.
        Assert.StartsWith(":", registered.BusName);
        Assert.Equal([registered], host.Items);

        // The point of the whole phase: the app is still there afterwards. An icon that
        // arrives from a process that then dies is the failure this job exists to catch.
        Assert.False(app.HasExited, "o-view exited after registering its icon");
    }

    /// <summary>
    /// The same thing with the notification area arriving second, which is what a GNOME user
    /// installing the AppIndicator extension while O-view runs does.
    ///
    /// <para>Here to tell two failures apart. The icon not arriving says nothing about why;
    /// the icon not arriving when the area was there first but arriving when it appeared
    /// afterwards says the tray backend only ever acts on the name changing hands, and never
    /// on finding it already owned — which is the ordinary case on every desktop that ships a
    /// notification area.</para>
    /// </summary>
    [Fact]
    public async Task TheIconReachesAHostThatAppearedAfterTheApp()
    {
        if (!InRuntimeJob) return;

        await using var app = await LinuxAppProcess.StartAsync(output, nameof(TheIconReachesAHostThatAppearedAfterTheApp));

        var startup = await app.WaitForLogAsync(LinuxAppProcess.StartupLogLine, Patience);
        Assert.Contains($"trayHost={TrayHostState.Absent}", startup);

        await using var host = await StandInTrayHost.StartAsync();

        var registered = await app.WaitForAsync(
            host.WaitForItemAsync(Patience), Patience, "the icon to reach a host that started after it");

        Assert.StartsWith(":", registered.BusName);
        Assert.False(app.HasExited, "o-view exited after registering its icon");
    }

    /// <summary>
    /// Both waits, once the app has gone: they fail with its own output, and they fail at
    /// once rather than waiting out a timeout nothing can satisfy any more.
    /// </summary>
    [Fact]
    public async Task BothWaitsFailWithTheAppsOwnOutputAsSoonAsItHasGone()
    {
        if (!InRuntimeJob) return;

        // A notification area is there so the app starts the way it does everywhere else, and
        // nothing here waits for the icon: what is under test is the harness's behaviour once
        // the app has gone, and StartAsync has already returned a started app.
        await using var host = await StandInTrayHost.StartAsync();
        await using var app = await LinuxAppProcess.StartAsync(output, nameof(BothWaitsFailWithTheAppsOwnOutputAsSoonAsItHasGone));

        // Killed from outside, as a crash would take it. Without this check the harness is
        // trustworthy only when it passes: a later slice waiting five seconds for a panel from
        // an app that died on its first click would report a timeout, and the real failure —
        // and the log line naming it — would be thrown away.
        using (var victim = Process.GetProcessById(app.ProcessId))
        {
            victim.Kill();
            await victim.WaitForExitAsync().WaitAsync(Patience);
        }

        // Both waits, because a test that uses one of them is no better off for the other
        // being careful. The log wait is what the start-up tests use; the task wait is what
        // the click and the menu will use for a panel that never appears.
        var waited = Stopwatch.StartNew();

        var logFailure = await Assert.ThrowsAsync<XunitException>(
            () => app.WaitForLogAsync("a line o-view will never write", TimeSpan.FromMinutes(1)));

        var taskFailure = await Assert.ThrowsAsync<XunitException>(
            () => app.WaitForAsync(
                new TaskCompletionSource<bool>().Task, TimeSpan.FromMinutes(1), "something that never happens"));

        waited.Stop();

        // A minute each was offered and neither took it: both noticed the app had gone.
        Assert.True(waited.Elapsed < Patience,
            $"the waits took {waited.Elapsed.TotalSeconds:0.#} s rather than failing as soon as the app had gone");

        Assert.Contains("the app exited first", taskFailure.Message);

        // The app's own output, which is the whole value of either failure: its startup line,
        // and the log it was read from.
        foreach (var failure in new[] { logFailure, taskFailure })
        {
            Assert.Contains(LinuxAppProcess.StartupLogLine, failure.Message);
            Assert.Contains(app.LogPath, failure.Message);
        }
    }
}
