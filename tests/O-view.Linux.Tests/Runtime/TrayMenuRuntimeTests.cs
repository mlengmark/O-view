using Xunit.Abstractions;

namespace OView.Linux.Tests.Runtime;

/// <summary>
/// The tray menu opened the way a panel opens it, with the real app on the other end of the real
/// bus, and timed.
///
/// <para><b>Why this exists.</b> On v0.6.1 the menu took tens of seconds to appear (issue #125).
/// The rows themselves were never wrong — they are built in-process and read correctly in every
/// unit test there is — so what shipped was a menu that was perfect and unusable. The delay was
/// in answering <c>com.canonical.dbusmenu</c>, which nothing in the repository could reach: it
/// needs the shipped binary, a session bus and a second connection asking it questions.</para>
///
/// <para><b>Where the two seconds start.</b> When the panel first asks — the moment after which
/// a person is waiting. The app is already warm by then: the menu is only opened once the icon
/// has been drawn, which is the point at which the engine's first snapshot has reached the
/// toolkit, so what is measured is answering the bus and not starting up or ingesting. Every
/// round trip is reported either way, so a slow run says which call was slow.</para>
///
/// <para><b>What an empty menu does.</b> Fails. A menu with no rows answers <c>GetLayout</c>
/// instantly, which is the one way a timing test can pass while the thing it is about is
/// broken, so the rows the head builds — "Run at startup" and "Exit O-view" — have to be in
/// what came back.</para>
///
/// <para><b>Clicking a row is not in this.</b> "Exit O-view" ends the app, and the rest of the
/// menu's rows change this machine's autostart entry or the usage cache. Reading the layout is
/// what issue #125 was about; driving a row is a separate slice.</para>
///
/// <para>The timed test runs only in the <c>linux-runtime</c> job, which provides the display
/// (<c>xvfb-run</c>), the bus (<c>dbus-run-session</c>) and <c>OVIEW_LINUX_RUNTIME=1</c>.
/// Elsewhere it returns early, the way the other runtime tests do. The rest are about how a
/// menu is read rather than about reading one, and run everywhere.</para>
/// </summary>
public class TrayMenuRuntimeTests(ITestOutputHelper output)
{
    /// <summary>
    /// The rows <c>LinuxApp.BuildMenu</c> builds visible. The third one it builds —
    /// "resume usage refresh" — starts hidden and is corrected on a later poll, so it is
    /// deliberately not expected here.
    /// </summary>
    private static readonly string[] TheRowsTheHeadBuilds = ["Run at startup", "Exit O-view"];

    /// <summary>
    /// How long the menu has, for all three round trips together. The bound this test exists to
    /// hold: a person who right-clicks a tray icon and waits longer than this has been failed,
    /// and in issue #125 they waited tens of seconds.
    /// </summary>
    private static readonly TimeSpan MenuPatience = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How much longer than its own budget the menu is waited for, so the client's timeout is
    /// the one that fires and its message — naming the call that was outstanding — is what gets
    /// reported, rather than a bare "waited 2 s".
    /// </summary>
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long anything that is not the menu may take — the app starting, the icon reaching the
    /// notification area. Generous because this is a shared CI runner starting a GUI toolkit
    /// cold; neither is a duration this test measures.
    /// </summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private static bool InRuntimeJob => Environment.GetEnvironmentVariable("OVIEW_LINUX_RUNTIME") == "1";

    /// <summary>
    /// The whole thing a panel does to show the menu: a notification area is waiting, the app
    /// comes up and publishes its icon to it, and then the menu is asked for where it lives, told
    /// it is about to be shown, and read — inside two seconds, with the rows the head builds in
    /// it and the app still running.
    ///
    /// <para>One test rather than four, for the reason <see cref="TrayClickRuntimeTests"/> gives:
    /// these are stages of one gesture, a failure at any of them is the same report — "the menu
    /// does not open on Linux" — and splitting them would start the app four times in a job that
    /// has fifteen minutes for everything.</para>
    /// </summary>
    [Trait("Category", "LinuxRuntime")]
    [Fact]
    public async Task TheTrayMenuOpensWithinTwoSecondsWithTheRowsTheHeadBuilds()
    {
        if (!InRuntimeJob) return;

        // The host first: the app looks for a notification area once, before the toolkit starts,
        // so one that arrives later is a different case from the one every desktop presents.
        await using var host = await StandInTrayHost.StartAsync();
        await using var app = await LinuxAppProcess.StartAsync(
            output, nameof(TheTrayMenuOpensWithinTwoSecondsWithTheRowsTheHeadBuilds));

        // The toolkit alive and the engine's snapshot through to it, which is also what makes
        // the two seconds below a measurement of answering the bus rather than of starting up.
        await app.WaitForLogAsync(LinuxAppProcess.IconRenderedLogLine, Patience);

        var item = await app.WaitForAsync(
            host.WaitForItemAsync(Patience), Patience, "the icon to reach the notification area");

        // A connection of this test's own, which is how a panel asks: the stand-in host keeps
        // its own connection to itself, and DBusConnection.Session is the shared static the rest
        // of the run — and the app's own code — depends on.
        using var panel = await DBusMenuClient.ConnectAsync();

        // Through the app's own wait, so the app exiting mid-menu fails here and now with its
        // log attached instead of waiting the budget out.
        var menu = await app.WaitForAsync(
            panel.OpenAsync(item, MenuPatience), MenuPatience + Grace, "the tray menu to open");

        // Always, not only on a failure: the timings are what this test is for, and a run where
        // the menu came back in 1.9 s is worth seeing before it becomes a red build.
        output.WriteLine(menu.Describe());

        var missing = menu.RowsMissing(TheRowsTheHeadBuilds);
        Assert.True(missing.Count == 0,
            $"the menu opened, but it is not the menu the head builds: {string.Join("; ", missing)}."
            + $"{Environment.NewLine}{menu.Describe()}");

        // The budget is enforced by the client, which times each call against it; this says the
        // same thing about the total, so the duration a reader cares about is asserted and not
        // merely implied by nothing having thrown.
        Assert.True(menu.Took <= MenuPatience,
            $"the menu took {menu.Took.TotalMilliseconds:0} ms to open, and a person waits "
            + $"{MenuPatience.TotalSeconds:0} s.{Environment.NewLine}{menu.Describe()}");

        // Issue #143 was the click taking the app down with it. Reading a menu runs the same
        // bus-thread code, so it is worth the same check.
        Assert.False(app.HasExited, "o-view exited after its menu was read");
    }

    /// <summary>
    /// Rows are found by the text on them, and a hidden row is not one of them: the head keeps
    /// the "resume usage refresh" row in the menu with <c>IsVisible</c> false until it has
    /// something to offer, and a test that counted it would call a menu complete that a person
    /// sees two rows in.
    /// </summary>
    [Fact]
    public void RowsAreFoundByTheirTextAndHiddenOnesDoNotCount()
    {
        var menu = new DBusMenu(
            Path: "/net/avaloniaui/dbusmenu/1",
            Revision: 3,
            Rows:
            [
                // As a toolkit that marks up keyboard shortcuts would publish it.
                new DBusMenuRow(1, 0, DBusMenuRow.Readable("_Run at startup"), Enabled: true, Visible: true, "standard"),
                new DBusMenuRow(2, 0, "resume usage refresh", Enabled: true, Visible: false, "standard"),
                new DBusMenuRow(3, 0, "Exit O-view", Enabled: true, Visible: true, "standard"),
            ],
            Steps: []);

        Assert.True(menu.HasRow("Run at startup"));
        Assert.True(menu.HasRow("Exit O-view"));
        Assert.False(menu.HasRow("resume usage refresh"));

        Assert.Equal(
            new[] { "\"resume usage refresh\" is in the menu but hidden" },
            menu.RowsMissing(["Run at startup", "resume usage refresh"]));
    }

    /// <summary>
    /// An empty menu is missing every row it was asked for. The case that matters most: a menu
    /// with nothing in it answers at once, so without this the timing test would pass on the
    /// fastest possible way of being broken.
    /// </summary>
    [Fact]
    public void AnEmptyMenuIsMissingEveryRowItWasAskedFor()
    {
        var menu = new DBusMenu("/net/avaloniaui/dbusmenu/1", Revision: 0, Rows: [], Steps: []);

        Assert.Equal(
            new[]
            {
                "\"Run at startup\" is not in the menu at all",
                "\"Exit O-view\" is not in the menu at all",
            },
            menu.RowsMissing(TheRowsTheHeadBuilds));

        Assert.Contains("the menu is empty", menu.Describe());
    }

    /// <summary>
    /// With no session bus — the ordinary test job, and a developer's own machine — asking to
    /// open a menu says so plainly rather than failing somewhere further in.
    /// </summary>
    [Fact]
    public async Task WithNoSessionBusItSaysThereIsNoneToOpenAMenuOver()
    {
        var bus = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");

        try
        {
            // Cleared rather than assumed unset, so this says the same thing wherever it runs.
            Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", null);

            var refused = await Assert.ThrowsAsync<InvalidOperationException>(
                () => DBusMenuClient.ConnectAsync());

            Assert.Contains("no session bus", refused.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", bus);
        }
    }
}
