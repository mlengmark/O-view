using OView.Linux.Tray;
using Xunit.Abstractions;

namespace OView.Linux.Tests.Runtime;

/// <summary>
/// A notification area that goes away and comes back while O-view runs, and the one icon it
/// should find when it does.
///
/// <para><b>Why this exists.</b> On a GNOME desktop, switching the AppIndicator extension off,
/// starting O-view and switching it back on left two O-view icons in the top bar, one process
/// behind both, each with a working menu (issue #356). The toolkit's tray backend registers the
/// icon again by itself whenever the watcher's name changes hands, and the head re-showed the
/// icon on top of that, which a host takes as a second item. At login neither happens twice, so
/// the defect needed the host to be away first and arrive while the app was already
/// running, which is what this arranges, with the stand-in host on a real bus.</para>
///
/// <para><b>What is counted.</b> Every <c>RegisterStatusNotifierItem</c> call the returning
/// host receives. One is the icon coming back; a second is the second icon, however a real
/// desktop then draws it.</para>
///
/// <para>The runtime test runs only in the <c>linux-runtime</c> job, which provides the
/// display, the bus and <c>OVIEW_LINUX_RUNTIME=1</c>. The rest are about how a listed entry is
/// read, and run everywhere.</para>
/// </summary>
public class TrayHostReturnRuntimeTests(ITestOutputHelper output)
{
    /// <summary>
    /// What the head logs once it has decided what a returning host needs. Every one of its
    /// decisions starts this way, and the line before the fix did too, so the wait below holds
    /// for the code before the fix and after it alike.
    /// </summary>
    private const string HostArrivedLogLine = "tray host appeared, ";

    /// <summary>The decision this test is after: the host had the icon, so nothing was added.</summary>
    private const string AlreadyListedLogLine = "icon already listed, not registered again";

    /// <summary>Two rows a working menu has whatever session it is in, read off the bus.</summary>
    private static readonly string[] RowsAWorkingMenuHas = ["Show usage", "Exit O-view"];

    /// <summary>
    /// How long a second registration is given to arrive once the head has decided. Before the
    /// fix it followed the decision within milliseconds; this is the margin for a busy runner.
    /// </summary>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);

    /// <summary>How long the menu has. Not what this test measures, so generous.</summary>
    private static readonly TimeSpan MenuPatience = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long anything else may take, the app starting, the icon reaching the host. Generous
    /// because this is a shared CI runner starting a GUI toolkit cold.
    /// </summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private static bool InRuntimeJob => Environment.GetEnvironmentVariable("OVIEW_LINUX_RUNTIME") == "1";

    /// <summary>
    /// The report's steps: a notification area there and then gone, O-view started without
    /// one, and the area back while it runs. Exactly one icon reaches it, and its menu opens.
    /// Then the area leaves and returns once more, which is the toolkit's own recovery alone,
    /// and that also ends with exactly one.
    ///
    /// <para>One test rather than two, because the second return is only meaningful after the
    /// first: it is what shows the head's one-off recovery has not turned into something that
    /// re-registers on every return.</para>
    /// </summary>
    [Trait("Category", "LinuxRuntime")]
    [Fact]
    public async Task AHostThatLeavesAndReturnsIsHandedExactlyOneIconWithAWorkingMenu()
    {
        if (!InRuntimeJob) return;

        // The extension switched off: a host that was there and has gone, before the app starts.
        var leaving = await StandInTrayHost.StartAsync();
        await leaving.DisposeAsync();

        await using var app = await LinuxAppProcess.StartAsync(
            output, nameof(AHostThatLeavesAndReturnsIsHandedExactlyOneIconWithAWorkingMenu));

        var startup = await app.WaitForLogAsync(LinuxAppProcess.StartupLogLine, Patience);
        Assert.Contains($"trayHost={TrayHostState.Absent}", startup);

        // The toolkit up and the icon drawn, so the host returns to an app that is running
        // rather than one still starting, which is the case the report describes.
        await app.WaitForLogAsync(LinuxAppProcess.IconRenderedLogLine, Patience);

        // The extension switched back on.
        await using (var returned = await StandInTrayHost.StartAsync())
        {
            var item = await app.WaitForAsync(
                returned.WaitForItemAsync(Patience), Patience, "the icon to reach the returning host");

            await app.WaitForLogAsync(HostArrivedLogLine, Patience);
            await Task.Delay(Settle);

            AssertExactlyOneRegistration(returned, "when the host first returned");
            await AssertTheMenuWorksAsync(app, item, "after the host first returned");

            // Why there was one: the host was asked and had it. Without this, a head that never
            // re-showed the icon at all would pass the count above, and so would one whose read
            // of the list always came back unknown.
            await app.WaitForLogAsync(AlreadyListedLogLine, Patience);
        }

        // And again, with the head's own recovery already spent.
        await using var again = await StandInTrayHost.StartAsync();

        var itemAgain = await app.WaitForAsync(
            again.WaitForItemAsync(Patience), Patience, "the icon to reach the host returning a second time");
        await Task.Delay(Settle);

        AssertExactlyOneRegistration(again, "when the host returned a second time");
        await AssertTheMenuWorksAsync(app, itemAgain, "after the host returned a second time");

        Assert.False(app.HasExited, "o-view exited while its notification area came and went");
    }

    /// <summary>
    /// The connection is read off the front of an entry, whichever shape the watcher lists it
    /// in: the name the item registered under, or the connection behind it, with or without the
    /// object path after it.
    /// </summary>
    [Theory]
    [InlineData(":1.45/StatusNotifierItem", ":1.45")]
    [InlineData(":1.45/org/ayatana/NotificationItem/o_view", ":1.45")]
    [InlineData("org.kde.StatusNotifierItem-4242-0/StatusNotifierItem", "org.kde.StatusNotifierItem-4242-0")]
    [InlineData("org.kde.StatusNotifierItem-4242-0", "org.kde.StatusNotifierItem-4242-0")]
    public void AnEntryIsReadAsTheBusNameItStartsWith(string entry, string expected) =>
        Assert.Equal(expected, TrayHostListing.BusNameOf(entry));

    /// <summary>
    /// An entry that is only a path names no connection, so nothing about it can say whose
    /// icon it is, and it is passed over rather than asked about.
    /// </summary>
    [Theory]
    [InlineData("/StatusNotifierItem")]
    [InlineData("")]
    public void AnEntryWithNoBusNameNamesNobody(string entry) =>
        Assert.Null(TrayHostListing.BusNameOf(entry));

    /// <summary>
    /// With no session bus there is no list to read, and that is said as unknown, never as
    /// "not listed": the head re-shows the icon only on the second, and a second icon is the
    /// defect this is about.
    /// </summary>
    [Fact]
    public async Task WithNoSessionBusTheListIsUnknownRatherThanEmpty()
    {
        var bus = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");

        try
        {
            Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", null);

            Assert.Equal(
                TrayHostListingState.Unknown,
                await TrayHostListing.WaitForProcessAsync(Environment.ProcessId, TimeSpan.Zero));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", bus);
        }
    }

    private static void AssertExactlyOneRegistration(StandInTrayHost host, string when)
    {
        var items = host.Items;

        Assert.True(items.Count == 1,
            $"{when}, the host was handed {items.Count} registrations, and each is an icon: "
            + string.Join("; ", items.Select(i => $"{i.Service} from {i.BusName}")));
    }

    private async Task AssertTheMenuWorksAsync(LinuxAppProcess app, RegisteredItem item, string when)
    {
        using var panel = await DBusMenuClient.ConnectAsync();

        var menu = await app.WaitForAsync(
            panel.OpenAsync(item, MenuPatience), MenuPatience + MenuPatience, $"the tray menu to open {when}");

        output.WriteLine(menu.Describe());

        var missing = menu.RowsMissing(RowsAWorkingMenuHas);
        Assert.True(missing.Count == 0,
            $"{when}, the menu opened without the rows it always has: {string.Join("; ", missing)}."
            + $"{Environment.NewLine}{menu.Describe()}");
    }
}
