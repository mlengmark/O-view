using System.Runtime.InteropServices;
using OView.Presentation.Text;
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
/// in-process test could see it, the report was a core dump and an empty log. Nothing short of
/// a real click on a real bus reaches that code path, which is what this does.</para>
///
/// <para><b>This is the live bus and the dispatcher together.</b> That combination was long
/// written down as unreachable, and it is what let #124 ship and then let #143 ship past the fix
/// for #124. It is reachable here, a real session bus, a real notification area, a real click,
/// and the app's own Avalonia dispatcher on the other end, so the claim does not hold for this
/// test. Older comments elsewhere in the suite still make it; they are stale, not a contract.</para>
///
/// <para><b>How a click arrives.</b> A notification area calls
/// <c>org.kde.StatusNotifierItem.Activate</c> on the item, with the screen coordinates of the
/// click, and waits for its answer. Both halves matter: the answer coming back is what #124 did
/// not do, and the app still being alive afterwards is what #143 did not do.</para>
///
/// <para><b>What proves the panel is up, and that the right thread put it there.</b> The app's
/// own <c>panel opened</c> line, written once the window has been built and shown, which also
/// names the thread that built it. Both halves are asserted, and the second is the one that
/// catches #143's defect: with <see cref="OView.Linux.Tray.BusCallback"/>'s hand-off removed the
/// panel still opened under this job's virtual display and the line still arrived, because the
/// runner has no usable GLX and the frame is drawn on the CPU (measured, PR #295). A test that
/// waits only for the line passes on a head that has gone back to building windows on the bus
/// thread. The virtual display is photographed too, before the click and after it, and that pair
/// is the only thing that shows a person what came up, but it is evidence for a reader, not an
/// assertion: nothing installed in the <c>linux-runtime</c> job can ask X11 which windows are
/// mapped, so the log line is the only signal a test can act on.</para>
///
/// <para><b>Failing at once when the app has gone.</b> Every wait here goes through
/// <see cref="LinuxAppProcess"/>, which stops waiting the moment the app exits and reports the
/// app's own log with the failure. That behaviour has its own test
/// (<see cref="LinuxAppStartRuntimeTests.BothWaitsFailWithTheAppsOwnOutputAsSoonAsItHasGone"/>),
/// so it is used here rather than proved again.</para>
///
/// <para><b>And the second click, which is a different defect.</b> The panel is hidden on
/// dismissal, not closed, so the next open closed a window that was still the application's only
/// one, and on Avalonia's default shutdown mode that ended the process while the new panel was
/// coming up (issue #312). One click could never see it: the first open has nothing to close.
/// <see cref="ASecondOpenAfterADismissalShowsThePanelAgainInsteadOfEndingTheApp"/> is the pair of
/// opens, with the dismissal in between.</para>
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

    /// <summary>
    /// What that line says when the UI thread was the one that built and showed the window,
    /// <c>Dispatcher.UIThread.CheckAccess()</c>, read inside <c>ShowPanel</c> itself. With
    /// <see cref="OView.Linux.Tray.BusCallback"/>'s hand-off gone it reads <c>uiThread=false</c>.
    /// </summary>
    private const string OpenedByTheUiThread = "uiThread=true";

    /// <summary>The line <c>PanelWindow</c> writes when a dismissal has hidden it.</summary>
    private const string PanelHiddenLogLine = "panel hidden";

    /// <summary>The line <c>LinuxApp</c> writes naming the shutdown mode the lifetime is on.</summary>
    private const string ShutdownModeLogLine = "shutdown mode=";

    /// <summary>
    /// What that line has to say for a dismissed panel to be safe to close. Read off the live
    /// lifetime by the app itself, so this is the mode in force and not the one asked for.
    /// </summary>
    private const string EndsOnlyWhenAsked = "OnExplicitShutdown";

    private const string ItemInterface = "org.kde.StatusNotifierItem";

    /// <summary>
    /// How long the panel has to be up after the click. The bound this test exists to hold: a
    /// person who clicks a tray icon and sees nothing for five seconds has been failed, whatever
    /// the app does afterwards.
    /// </summary>
    private static readonly TimeSpan PanelPatience = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long anything that is not the panel may take, the app starting, the icon reaching the
    /// notification area, the item answering. Generous because this is a shared CI runner
    /// starting a GUI toolkit cold; none of these is a duration this test measures.
    /// </summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Where a notification area would say the click happened. Any coordinates do, the app
    /// positions its own panel and ignores them, but a real host sends the pointer's position,
    /// so sending a plausible one keeps the call indistinguishable from a desktop's.
    /// </summary>
    private const int ClickX = 1200;
    private const int ClickY = 20;

    /// <summary>The photograph this test exists to produce: the display with the panel on it.</summary>
    private const string AfterTheClick = nameof(ALeftClickOpensThePanelWithinFiveSeconds);

    /// <summary>The same display moments earlier, so the one above can be held against something.</summary>
    private const string BeforeTheClick = AfterTheClick + "-before-the-click";

    /// <summary>
    /// Names the second test's app folder, its saved log and its photograph. Shortened from the
    /// test's own name, which is longer than a file name wants to be.
    /// </summary>
    private const string TheSecondOpen = "ASecondOpenAfterADismissal";

    /// <summary>
    /// How long the toolkit is given to put something on screen before the photograph is
    /// settled for. Nothing is measured here and nothing fails when it runs out, see
    /// <see cref="PhotographTheOpenPanelAsync"/>.
    /// </summary>
    private static readonly TimeSpan PaintPatience = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan BetweenPhotographs = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How long the dismissal is given to show up in the log. Not a duration anyone waits on, a
    /// panel hides itself in the frame the focus leaves, so this is only long enough to tell a
    /// slow runner from a focus change the X server never delivered.
    /// </summary>
    private static readonly TimeSpan DismissalPatience = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How long the app is watched after the second panel has opened. The shutdown this test
    /// exists to rule out is already in flight by then, it starts inside the <c>Close()</c> that
    /// precedes the new window, so this is a settling period, not a measurement.
    /// </summary>
    private static readonly TimeSpan TheShutdownWouldHaveLanded = TimeSpan.FromSeconds(2);

    /// <summary>How long Exit O-view has to end the process once the row has been clicked.</summary>
    private static readonly TimeSpan ExitPatience = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How much longer the app's own wait runs than the budget inside it, so a timeout is
    /// reported by the inner wait, which names how many lines did arrive, rather than as a bare
    /// "waited 5 s". The same arrangement <see cref="TrayMenuRuntimeTests"/> uses.
    /// </summary>
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan BetweenLogReads = TimeSpan.FromMilliseconds(50);

    private static bool InRuntimeJob => Environment.GetEnvironmentVariable("OVIEW_LINUX_RUNTIME") == "1";

    /// <summary>
    /// The whole click, end to end: a notification area is waiting, the app comes up and
    /// publishes its icon to it, the area clicks the icon, and the panel is up within five
    /// seconds with the app still running.
    ///
    /// <para>One test rather than four, because these are four stages of one click and a failure
    /// at any of them is the same report, "clicking the icon does nothing on Linux". Splitting
    /// them would also start the app four times, and the job has ten minutes for everything.</para>
    /// </summary>
    [Fact]
    public async Task ALeftClickOpensThePanelWithinFiveSeconds()
    {
        if (!InRuntimeJob) return;

        // The host first: the app looks for a notification area once, before the toolkit starts,
        // so one that arrives later is a different case from the one every desktop presents.
        await using var host = await StandInTrayHost.StartAsync();
        await using var app = await LinuxAppProcess.StartAsync(output, AfterTheClick);

        // Declared out here only so the settled photograph at the end has it to compare against.
        byte[]? beforeTheClick = null;

        try
        {
            // The toolkit is alive and the engine has reached it, so there is something for a
            // click to open a panel from.
            await app.WaitForLogAsync(LinuxAppProcess.IconRenderedLogLine, Patience);

            var item = await app.WaitForAsync(
                host.WaitForItemAsync(Patience), Patience, "the icon to reach the notification area");

            // A connection of this test's own, which is how a notification area clicks: the
            // stand-in host keeps its own connection to itself, and DBusConnection.Session is the
            // shared static the rest of the run, and the app's own code, depends on.
            using var area = new DBusConnection(SessionBusAddress());
            await area.ConnectAsync();

            // The display as it was with no panel on it. Taken before the click so the
            // photograph afterwards has something to be held against, and best effort: a
            // picture that cannot be taken must not decide whether the click works.
            beforeTheClick = Photograph(BeforeTheClick);

            // Sent, not yet answered. The five seconds run from here, because this is the moment
            // the person let go of the mouse button.
            var answered = Activate(area, item);

            var panelOpened = await app.WaitForLogAsync(PanelOpenedLogLine, PanelPatience);

            // Issue #143 is not "the panel did not open", it is "the window was built on the
            // D-Bus thread". Measured: with the hand-off removed the panel still opens here, so
            // this assertion, not the line's arrival, is what makes the removal visible.
            Assert.True(
                panelOpened.Contains(OpenedByTheUiThread, StringComparison.Ordinal),
                $"the panel was not opened by the UI thread, so the click reached the toolkit from "
                + $"the bus thread the way issue #143 did: \"{panelOpened}\"");

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
            // Best effort, and deliberately not the settled photograph below: a picture that
            // cannot be taken must not replace the failure the test actually found, and a
            // failing run wants the display as it was when the test gave up, not later.
            Photograph(AfterTheClick);
            throw;
        }

        // On the pass path the photograph is part of what this test produces, the only thing
        // that shows a reader what came up, so failing to take it fails the test.
        await PhotographTheOpenPanelAsync(beforeTheClick);
    }

    /// <summary>
    /// Looking at the panel twice: open it, put it away, open it again, and the app is still
    /// running, with a second <c>panel opened</c> line to show the panel came back.
    ///
    /// <para><b>The defect.</b> A dismissed panel is hidden rather than closed, so it is still one
    /// of the application's windows. The next open closes it before building its replacement, and
    /// under <c>ShutdownMode.OnLastWindowClose</c>, Avalonia's default for a classic desktop
    /// lifetime, and what this head used to take, that close started a shutdown. The new panel
    /// was built and shown anyway, so <c>panel opened</c> was written, and then the process ended
    /// with nothing having failed: no exception, no segfault, no shutdown line. Every Linux user
    /// lost the app the second time they looked at it (issue #312).</para>
    ///
    /// <para><b>What is asserted, and in which order.</b> The shutdown mode first, read out of the
    /// app's own log where it reports what the live lifetime is on, before either click, because
    /// on the wrong mode everything below it fails as "the panel did not come back" and the mode
    /// is the reason. Then the two opens, the second of which must be there twice over: the line,
    /// and the app still being alive once the shutdown would have landed. Last, Exit O-view, which
    /// is the one thing that <i>should</i> end the process and the one an explicit mode could
    /// plausibly have broken.</para>
    ///
    /// <para><b>Why the clicks are not enough on their own.</b> The second <c>panel opened</c> line
    /// was written on the broken head too, it is the line the bug report's log ends on. So the
    /// aliveness check after it, not the line, is what fails on a regression.</para>
    /// </summary>
    [Fact]
    public async Task ASecondOpenAfterADismissalShowsThePanelAgainInsteadOfEndingTheApp()
    {
        if (!InRuntimeJob) return;

        await using var host = await StandInTrayHost.StartAsync();
        await using var app = await LinuxAppProcess.StartAsync(output, TheSecondOpen);

        // The toolkit alive and the engine's snapshot through to it, so there is something for a
        // click to open a panel from.
        await app.WaitForLogAsync(LinuxAppProcess.IconRenderedLogLine, Patience);

        var item = await app.WaitForAsync(
            host.WaitForItemAsync(Patience), Patience, "the icon to reach the notification area");

        // The mode the lifetime actually has. Asserted here rather than at the end because it is
        // the cause: on OnLastWindowClose the two opens below end the app, and a failure reading
        // "the panel did not open a second time" would send the next reader after the panel
        // instead of after the shutdown.
        var mode = await app.WaitForLogAsync(ShutdownModeLogLine, Patience);
        Assert.True(
            mode.Contains(EndsOnlyWhenAsked, StringComparison.Ordinal),
            $"the Linux head is not on an explicit shutdown mode, so closing the hidden panel on "
            + $"the next open ends the process the way issue #312 did: \"{mode}\"");

        // A connection of this test's own, which is how a notification area clicks, see the
        // click test above.
        using var area = new DBusConnection(SessionBusAddress());
        await area.ConnectAsync();

        await app.WaitForAsync(
            Activate(area, item), Patience, "the item to answer the first left click");
        await app.WaitForAsync(
            WaitForLogLineAsync(app, PanelOpenedLogLine, occurrence: 1, PanelPatience),
            PanelPatience + Grace,
            "the panel to open the first time");

        var dismissed = await DismissThePanelAsync(app);

        await app.WaitForAsync(
            Activate(area, item), Patience, "the item to answer the second left click");

        var reopened = await app.WaitForAsync(
            WaitForLogLineAsync(app, PanelOpenedLogLine, occurrence: 2, PanelPatience),
            PanelPatience + Grace,
            dismissed
                ? "the panel to open again after it had been dismissed"
                : "the panel to open a second time");

        // The same check the click test makes, for the same reason: the second open builds a
        // window too, and it must be the UI thread that does it (issue #143).
        Assert.True(
            reopened.Contains(OpenedByTheUiThread, StringComparison.Ordinal),
            $"the panel was not reopened by the UI thread: \"{reopened}\"");

        // For a reader, not for the assertion below: the display with the second panel on it.
        Photograph(TheSecondOpen);

        await Task.Delay(TheShutdownWouldHaveLanded);
        Assert.False(
            app.HasExited,
            $"o-view ended itself {TheShutdownWouldHaveLanded.TotalSeconds:0} s after the panel was "
            + "opened a second time, which is issue #312: closing the dismissed panel closed the "
            + "last window, and the shutdown that started there outlived the panel it had just built.");

        // And the one way out that is meant to work. An explicit shutdown mode means nothing but
        // Shutdown() ends this process, so if Exit O-view were ever to stop calling it, this is
        // the fix that would have made the app unquittable.
        await ExitFromTheMenuAsync(app, area, item);
    }

    /// <summary>
    /// Puts the panel away the way a click on the desktop does, and says whether it took.
    ///
    /// <para><b>Best effort, deliberately.</b> A dismissal that does not take leaves the panel on
    /// screen, and the next open closes it just the same, which is the defect. So a hide that
    /// never arrives is recorded rather than failed on; what it must not do is go unsaid, which
    /// would leave a reader believing a panel was dismissed when it was not.</para>
    /// </summary>
    private async Task<bool> DismissThePanelAsync(LinuxAppProcess app)
    {
        var how = await DismissWithTheFocusAsync();

        try
        {
            var hidden = await app.WaitForAsync(
                WaitForLogLineAsync(app, PanelHiddenLogLine, occurrence: 1, DismissalPatience),
                DismissalPatience + Grace,
                "the panel to hide once the focus had gone");

            SaveToArtifacts($"the panel was dismissed, {how}, and the app wrote: {hidden}");
            return true;
        }
        catch (Exception ex)
        {
            SaveToArtifacts(
                $"the panel was NOT dismissed, {how}, and no \"{PanelHiddenLogLine}\" line arrived "
                + $"within {DismissalPatience.TotalSeconds:0} s ({ex.GetType().Name}: {ex.Message})."
                + $"{Environment.NewLine}The second open below therefore closes a panel that is still "
                + "on screen rather than a hidden one. Either way the close is what ended the app in "
                + "issue #312, so the test carries on, but the dismissal half of it is unproven.");
            return false;
        }
    }

    /// <summary>
    /// Dismisses the panel with the keyboard focus, and says what it did either way.
    ///
    /// <para><b>Both halves of a click away.</b> The focus arrives somewhere else, and the panel
    /// deactivates. <c>PanelWindow</c> hides only on a deactivation that follows an activation,
    /// deliberately, because a compositor that refuses the activation would otherwise hide the
    /// panel in the frame it appeared (<c>PanelDismissal</c>), so the focus is put on the panel
    /// first and then taken off it.</para>
    ///
    /// <para><b>Why by hand.</b> This display has no window manager, which is also why the panel
    /// does not end up focused by itself: Avalonia asks the window manager to activate a window,
    /// and here there is nobody to ask. The job installs no <c>xdotool</c> and no XTEST either, so
    /// these are the X calls a desktop would have made.</para>
    ///
    /// <para>Focus is then given to no window rather than to the pointer's root, which would hand
    /// it straight back if the pointer happened to be over the panel.</para>
    /// </summary>
    private static async Task<string> DismissWithTheFocusAsync()
    {
        var display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero)
        {
            return "the X display could not be opened, so the focus was never moved";
        }

        try
        {
            if (LargestWindow(display) is not { } panel)
            {
                return "no window big enough to be the panel was on the display, so the focus was "
                    + "never moved";
            }

            XSetInputFocus(display, panel, RevertToPointerRoot, CurrentTime);
            XFlush(display);

            // The toolkit has to get round to the focus change before the next one undoes it: a
            // deactivation arriving with no activation behind it is the one PanelWindow suppresses.
            await Task.Delay(BetweenFocusChanges);

            XSetInputFocus(display, NoWindow, RevertToPointerRoot, CurrentTime);
            XFlush(display);

            return $"the focus was put on window 0x{panel.ToString("x")} and then taken off it";
        }
        finally
        {
            XCloseDisplay(display);
        }
    }

    /// <summary>
    /// The biggest window on the display, which is the panel: nothing here can ask which window
    /// belongs to which process, and the panel is the only one with a size a person would call a
    /// window. Anything smaller than <see cref="SmallestPanel"/> either way is one of the toolkit's
    /// own helpers rather than something a user looks at.
    /// </summary>
    private static IntPtr? LargestWindow(IntPtr display)
    {
        if (XQueryTree(display, XDefaultRootWindow(display), out _, out _, out var children, out var count) == 0
            || children == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            IntPtr? largest = null;
            long largestArea = 0;

            for (var i = 0; i < count; i++)
            {
                var window = Marshal.ReadIntPtr(children, i * IntPtr.Size);

                if (XGetGeometry(
                        display, window, out _, out _, out _,
                        out var width, out var height, out _, out _) == 0)
                {
                    continue;
                }

                var area = (long)width * height;
                if (width < SmallestPanel || height < SmallestPanel || area <= largestArea)
                {
                    continue;
                }

                largest = window;
                largestArea = area;
            }

            return largest;
        }
        finally
        {
            XFree(children);
        }
    }

    /// <summary>
    /// Writes what the dismissal did where the <c>linux-runtime</c> job collects what the runtime
    /// tests capture, beside the app's own log.
    ///
    /// <para><b>Why a file and not the test's output.</b> On a passing run xunit keeps a test's
    /// output to itself, and whether the panel was really dismissed is exactly the thing a reader
    /// of a green run needs to know: the assertions below hold whether it was or not, and a
    /// dismissal nobody can check is a step this test only claims to have taken.</para>
    ///
    /// <para>Does nothing outside the job, which does not set the variable.</para>
    /// </summary>
    private void SaveToArtifacts(string what)
    {
        output.WriteLine(what);

        if (Environment.GetEnvironmentVariable("OVIEW_RUNTIME_ARTIFACTS") is not { Length: > 0 } directory)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, $"{TheSecondOpen}.dismissal.txt"), what);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException)
        {
            output.WriteLine(
                $"what the dismissal did could not be saved to {directory}: "
                + $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Clicks Exit O-view the way a notification area does, a <c>com.canonical.dbusmenu</c>
    /// <c>Event</c> against the row's own id, and waits for the process to go.
    ///
    /// <para>The row is found by reading the menu rather than by assuming an id: the ids are
    /// Avalonia's to choose. The click's reply is not waited on, because the app is shutting down
    /// while it would be written and an answer that never comes is not a failure here; the process
    /// ending is the whole assertion.</para>
    /// </summary>
    private async Task ExitFromTheMenuAsync(LinuxAppProcess app, DBusConnection area, RegisteredItem item)
    {
        using var panel = await DBusMenuClient.ConnectAsync();
        var menu = await app.WaitForAsync(
            panel.OpenAsync(item, Patience), Patience + Grace, "the tray menu to open");

        var exit = menu.Rows.FirstOrDefault(row =>
            row.Visible && row.Label == DBusMenuRow.Readable(MenuText.ExitRow));

        Assert.True(exit is not null,
            $"\"{MenuText.ExitRow}\" is not a row a person could click, so there is no way to end "
            + $"the app now that nothing else does.{Environment.NewLine}{menu.Describe()}");

        var click = area.GetMessageWriter();
        click.WriteMethodCallHeader(
            destination: item.BusName,
            path: menu.Path,
            @interface: DBusMenuClient.MenuInterface,
            member: "Event",
            signature: "isvu",
            flags: MessageFlags.None);
        click.WriteInt32(exit!.Id);
        click.WriteString("clicked");
        click.WriteVariant(VariantValue.Int32(0));
        click.WriteUInt32((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        try
        {
            await area.CallMethodAsync(click.CreateMessage(), static (_, _) => true);
        }
        catch (Exception ex)
        {
            // An app that has already gone cannot answer. Said, not failed on: the exit is what
            // is under test, and it is asserted below.
            output.WriteLine(
                $"the click on \"{MenuText.ExitRow}\" was not answered, which is what an app "
                + $"shutting down mid-call does ({ex.GetType().Name}: {ex.Message}).");
        }

        var deadline = DateTime.UtcNow + ExitPatience;
        while (!app.HasExited && DateTime.UtcNow < deadline)
        {
            await Task.Delay(BetweenLogReads);
        }

        Assert.True(
            app.HasExited,
            $"o-view was still running {ExitPatience.TotalSeconds:0} s after \"{MenuText.ExitRow}\" "
            + "was clicked. On an explicit shutdown mode nothing but Shutdown() ends this process, "
            + "so a menu row that no longer calls it leaves the app unquittable.");
    }

    /// <summary>
    /// Waits for the <paramref name="occurrence"/>-th line in the app's log containing
    /// <paramref name="containing"/>, and returns it.
    ///
    /// <para>Here rather than on <see cref="LinuxAppProcess"/> because that one answers with the
    /// first line that matches, which cannot tell the second open from the first, and the second
    /// is this test's whole subject. The log is read the way it reads it: shared, because the app
    /// holds the file open for appending.</para>
    ///
    /// <para>Every caller hands this to <see cref="LinuxAppProcess.WaitForAsync"/> on a slightly
    /// longer budget, so an app that has gone fails at once with its own log attached, and a
    /// timeout is reported by the message below, which says how many lines did arrive.</para>
    /// </summary>
    private static async Task<string> WaitForLogLineAsync(
        LinuxAppProcess app, string containing, int occurrence, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;

        while (true)
        {
            var found = LogLines(app)
                .Where(line => line.Contains(containing, StringComparison.Ordinal))
                .ToList();

            if (found.Count >= occurrence)
            {
                return found[occurrence - 1];
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"{occurrence} log line(s) containing \"{containing}\" were expected within "
                    + $"{within.TotalSeconds:0.#} s; {found.Count} arrived.");
            }

            await Task.Delay(BetweenLogReads);
        }
    }

    /// <summary>
    /// The app's log as it stands. Shared read, and an empty list while the file is being written
    /// to, the ordinary state between two appends.
    /// </summary>
    private static IReadOnlyList<string> LogLines(LinuxAppProcess app)
    {
        try
        {
            using var stream = new FileStream(
                app.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
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

    /// <summary>
    /// Photographs the display until it stops looking the way it did before the click, giving up
    /// after <see cref="PaintPatience"/> and keeping the last photograph either way.
    ///
    /// <para><b>Why not photograph once and be done with it.</b> That is what this test did at
    /// first, and the picture came back uniformly black, a truthful photograph of the display,
    /// taken in the moment between the window being shown and its first frame reaching the X
    /// server, and worth nothing to the reader it exists for. <c>panel opened</c> is written when
    /// the window has been shown; nothing the app writes marks the frame after it, and here that
    /// frame is drawn on the CPU because the runner has no usable GLX.</para>
    ///
    /// <para><b>Still not an assertion.</b> A display that never changes does not fail this test.
    /// Whether the panel is up has already been settled by the app's own log, and a photograph
    /// that disagrees with it is a reason for a person to open the screenshot, not grounds for
    /// this test to call a click broken over a picture. It is said in the test's output instead,
    /// where the run that produced the photograph will carry it.</para>
    /// </summary>
    private async Task PhotographTheOpenPanelAsync(byte[]? beforeTheClick)
    {
        var deadline = DateTime.UtcNow + PaintPatience;

        while (true)
        {
            // Not best effort: on the passing path this photograph is half of what the test
            // produces, so scrot failing to take it is a failure.
            DisplayCapture.CaptureTo(AfterTheClick);

            // Null off the runtime job, where capturing is deliberately a no-op, there is
            // nothing to wait for and nothing to compare, so the first pass is the last.
            var afterTheClick = ReadPhotograph(AfterTheClick);
            if (beforeTheClick is null || afterTheClick is null
                || !afterTheClick.AsSpan().SequenceEqual(beforeTheClick))
            {
                return;
            }

            if (DateTime.UtcNow >= deadline)
            {
                output.WriteLine(
                    $"{PaintPatience.TotalSeconds:0} seconds after the panel was opened the display "
                    + $"still looks exactly as it did before the click, so {AfterTheClick}.png is "
                    + "probably showing an empty screen rather than the panel. The click itself "
                    + "passed on the app's own log; it is the photograph that is worth doubting.");
                return;
            }

            await Task.Delay(BetweenPhotographs);
        }
    }

    /// <summary>
    /// Photographs the display, returning what landed on disk, and swallows a failure to take it:
    /// every caller of this one has something better to report than a screenshot that failed.
    /// </summary>
    private byte[]? Photograph(string name)
    {
        try
        {
            DisplayCapture.CaptureTo(name);
            return ReadPhotograph(name);
        }
        catch (Exception ex)
        {
            output.WriteLine($"the display could not be photographed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The bytes of a photograph just taken, or null where none was, off the runtime job
    /// <c>OVIEW_RUNTIME_ARTIFACTS</c> is unset and <see cref="DisplayCapture"/> writes nothing.
    /// The path is rebuilt from the same two pieces DisplayCapture puts it together from, because
    /// it photographs for the sake of the artifact folder and does not report where it wrote.
    /// </summary>
    private static byte[]? ReadPhotograph(string name)
    {
        var artifacts = Environment.GetEnvironmentVariable("OVIEW_RUNTIME_ARTIFACTS");
        if (string.IsNullOrEmpty(artifacts)) return null;

        var path = Path.Combine(artifacts, $"{name}.png");
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    // ── the X calls a dismissal needs ────────────────────────────────────────────────
    //
    // Classic DllImport, as the Windows head's native methods are, and for the same reason given
    // there: these are a handful of calls with no marshalling to speak of. They are reached only
    // from inside the linux-runtime job, this assembly also builds and runs on Windows, where
    // libX11 does not exist and nothing here is called.

    /// <summary>X's <c>None</c>: the focus belongs to no window, so nothing has it.</summary>
    private static readonly IntPtr NoWindow = IntPtr.Zero;

    /// <summary>Where the focus reverts if the window holding it goes away.</summary>
    private const int RevertToPointerRoot = 1;

    /// <summary>X's <c>CurrentTime</c>, let the server timestamp it.</summary>
    private static readonly IntPtr CurrentTime = IntPtr.Zero;

    /// <summary>
    /// Smaller than this, either way, and a window is one of the toolkit's own rather than the
    /// panel, which is 400 dip wide and taller than that.
    /// </summary>
    private const uint SmallestPanel = 100;

    /// <summary>How long the toolkit has to notice the focus arriving before it is taken away.</summary>
    private static readonly TimeSpan BetweenFocusChanges = TimeSpan.FromMilliseconds(500);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XOpenDisplay(IntPtr name);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XDefaultRootWindow(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XQueryTree(
        IntPtr display, IntPtr window, out IntPtr root, out IntPtr parent, out IntPtr children, out uint count);

    [DllImport("libX11.so.6")]
    private static extern int XGetGeometry(
        IntPtr display, IntPtr drawable, out IntPtr root, out int x, out int y,
        out uint width, out uint height, out uint borderWidth, out uint depth);

    [DllImport("libX11.so.6")]
    private static extern int XSetInputFocus(IntPtr display, IntPtr focus, int revertTo, IntPtr time);

    [DllImport("libX11.so.6")]
    private static extern int XFlush(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XFree(IntPtr data);

    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(IntPtr display);
}
