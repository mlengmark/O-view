using Tmds.DBus.Protocol;
using Xunit.Abstractions;

namespace OView.Linux.Tests.Runtime;

/// <summary>
/// A left click on the tray icon, sent the way a notification area sends one, with the real app
/// on the other end of the real bus.
///
/// <para><b>Why this exists.</b> The click is where this head has failed worst: the first one
/// deadlocked (#124), and later one segfaulted outright because the panel was built on the
/// D-Bus thread (#143). A SIGSEGV is not an exception, so neither the app's own catch nor any
/// in-process test could see it — the report was a core dump and an empty log. Nothing short of
/// a real click on a real bus reaches that code path, which is what this does.</para>
///
/// <para><b>How a click arrives.</b> A notification area calls
/// <c>org.kde.StatusNotifierItem.Activate</c> on the item, with the screen coordinates of the
/// click, and waits for its answer. Both halves matter: the answer coming back is what #124 did
/// not do, and the app still being alive afterwards is what #143 did not do.</para>
///
/// <para><b>What proves the panel is up.</b> The app's own <c>panel opened</c> line, written once
/// the window has been built and shown. The virtual display is photographed too, and that
/// photograph is the only thing that shows a person what came up — but it is evidence for a
/// reader, not an assertion: nothing installed in the <c>linux-runtime</c> job can ask X11 which
/// windows are mapped, so the log line is the only signal a test can act on.</para>
///
/// <para><b>Failing at once when the app has gone.</b> Every wait here goes through
/// <see cref="LinuxAppProcess"/>, which stops waiting the moment the app exits and reports the
/// app's own log with the failure. That behaviour has its own test
/// (<see cref="LinuxAppStartRuntimeTests.BothWaitsFailWithTheAppsOwnOutputAsSoonAsItHasGone"/>),
/// so it is used here rather than proved again.</para>
///
/// <para>Runs only in the <c>linux-runtime</c> job, which provides the display
/// (<c>xvfb-run</c>), the bus (<c>dbus-run-session</c>) and <c>OVIEW_LINUX_RUNTIME=1</c>.
/// Elsewhere each test returns early, the same way the other runtime tests do.</para>
/// </summary>
[Trait("Category", "LinuxRuntime")]
public class TrayClickRuntimeTests(ITestOutputHelper output)
{
    /// <summary>The line <c>LinuxApp.ShowPanel</c> writes once the panel window has been shown.</summary>
    public const string PanelOpenedLogLine = "panel opened";

    private const string ItemInterface = "org.kde.StatusNotifierItem";

    /// <summary>
    /// How long the panel has to be up after the click. The bound this test exists to hold: a
    /// person who clicks a tray icon and sees nothing for five seconds has been failed, whatever
    /// the app does afterwards.
    /// </summary>
    private static readonly TimeSpan PanelPatience = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long anything that is not the panel may take — the app starting, the icon reaching the
    /// notification area, the item answering. Generous because this is a shared CI runner
    /// starting a GUI toolkit cold; none of these is a duration this test measures.
    /// </summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Where a notification area would say the click happened. Any coordinates do — the app
    /// positions its own panel and ignores them — but a real host sends the pointer's position,
    /// so sending a plausible one keeps the call indistinguishable from a desktop's.
    /// </summary>
    private const int ClickX = 1200;
    private const int ClickY = 20;

    private static bool InRuntimeJob => Environment.GetEnvironmentVariable("OVIEW_LINUX_RUNTIME") == "1";

    /// <summary>
    /// The whole click, end to end: a notification area is waiting, the app comes up and
    /// publishes its icon to it, the area clicks the icon, and the panel is up within five
    /// seconds with the app still running.
    ///
    /// <para>One test rather than four, because these are four stages of one click and a failure
    /// at any of them is the same report — "clicking the icon does nothing on Linux". Splitting
    /// them would also start the app four times, and the job has ten minutes for everything.</para>
    /// </summary>
    [Fact]
    public async Task ALeftClickOpensThePanelWithinFiveSeconds()
    {
        if (!InRuntimeJob) return;

        // The host first: the app looks for a notification area once, before the toolkit starts,
        // so one that arrives later is a different case from the one every desktop presents.
        await using var host = await StandInTrayHost.StartAsync();
        await using var app = await LinuxAppProcess.StartAsync(output, nameof(ALeftClickOpensThePanelWithinFiveSeconds));

        try
        {
            // The toolkit is alive and the engine has reached it, so there is something for a
            // click to open a panel from.
            await app.WaitForLogAsync(LinuxAppProcess.IconRenderedLogLine, Patience);

            var item = await app.WaitForAsync(
                host.WaitForItemAsync(Patience), Patience, "the icon to reach the notification area");

            // A connection of this test's own, which is how a notification area clicks: the
            // stand-in host keeps its own connection to itself, and DBusConnection.Session is the
            // shared static the rest of the run — and the app's own code — depends on.
            using var area = new DBusConnection(SessionBusAddress());
            await area.ConnectAsync();

            // Sent, not yet answered. The five seconds run from here, because this is the moment
            // the person let go of the mouse button.
            var answered = Activate(area, item);

            await app.WaitForLogAsync(PanelOpenedLogLine, PanelPatience);

            // The answer to the click, which the panel being up does not imply: a host calls
            // Activate and waits, so an item that opens its window and never replies hangs the
            // notification area itself. That is issue #124, and it looks like a working app from
            // everywhere except the desktop.
            var answeredBy = await app.WaitForAsync(answered, Patience, "the item to answer the left click");

            // And it was the clicked item that answered, not something else on the bus that
            // happens to own the same object path.
            Assert.Equal(item.BusName, answeredBy);

            // Issue #143: the click used to take the app down with it. Checked last, so a crash
            // during the panel's first paint is caught as well as one during the click.
            Assert.False(app.HasExited, "o-view exited after its panel was opened by a left click");
        }
        catch
        {
            // Best effort, and deliberately not the throwing call below: a photograph that cannot
            // be taken must not replace the failure the test actually found.
            TryCapture();
            throw;
        }

        // On the pass path the photograph is part of what this test produces — the only thing
        // that shows a reader what came up — so failing to take it fails the test.
        DisplayCapture.CaptureTo(nameof(ALeftClickOpensThePanelWithinFiveSeconds));
    }

    /// <summary>
    /// Calls <c>Activate</c> on the item and returns the wait for its answer, so the caller can
    /// start the clock before the answer arrives rather than after it. The answer carries no
    /// body, so what is read off it is who sent it.
    /// </summary>
    private static Task<string> Activate(DBusConnection area, RegisteredItem item)
    {
        var writer = area.GetMessageWriter();
        writer.WriteMethodCallHeader(
            // The sender the bus assigned the item, not the name the item gave for itself: a
            // registration may name either, and only the first is guaranteed to be addressable.
            destination: item.BusName,
            path: item.ObjectPath,
            @interface: ItemInterface,
            member: "Activate",
            signature: "ii",
            flags: MessageFlags.None);
        writer.WriteInt32(ClickX);
        writer.WriteInt32(ClickY);

        return area.CallMethodAsync(
            writer.CreateMessage(), static (m, _) => m.SenderAsString ?? "(the answer named no sender)");
    }

    private static string SessionBusAddress() =>
        Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")
        ?? throw new InvalidOperationException(
            "DBUS_SESSION_BUS_ADDRESS is not set: there is no session bus to click over.");

    private void TryCapture()
    {
        try
        {
            DisplayCapture.CaptureTo(nameof(ALeftClickOpensThePanelWithinFiveSeconds));
        }
        catch (Exception ex)
        {
            output.WriteLine($"the display could not be photographed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
