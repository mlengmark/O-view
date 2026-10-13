using Xunit.Abstractions;

namespace OView.Linux.Tests.Runtime;

/// <summary>
/// The app started on the virtual display with the session bus being written down, so the one
/// question nothing else can answer — which side gave up on the tray icon — has an answer that
/// can be read after the run.
///
/// <para><b>Evidence, not a verdict.</b> The icon does not reach the notification area
/// (<see cref="LinuxAppStartRuntimeTests"/>), and nothing here asserts that it does or does
/// not: the test passes either way, and what it leaves behind is the capture. The assertions
/// that wait for a registration belong to the slice that makes one happen, because that is
/// where they can pass; a test that failed here would only stop the capture being
/// uploaded.</para>
///
/// <para>Runs in the <c>linux-runtime</c> job, which provides the display
/// (<c>xvfb-run</c>), the bus (<c>dbus-run-session</c>) and <c>OVIEW_LINUX_RUNTIME=1</c>.
/// Elsewhere there is nothing to record, so each test returns early the way
/// <see cref="LinuxRuntimeEnvironmentTests"/> does — except the last one, which is about what
/// the recorder does when there is no bus and so runs everywhere.</para>
/// </summary>
public class BusTrafficRuntimeTests(ITestOutputHelper output)
{
    private static bool InRuntimeJob => Environment.GetEnvironmentVariable("OVIEW_LINUX_RUNTIME") == "1";

    /// <summary>
    /// How long any one step may take. Generous because this is a shared CI runner starting a
    /// GUI toolkit cold: nothing here measures a duration, it only refuses to hang for ever.
    /// </summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long the capture is left running after the app has drawn its gauge. The toolkit
    /// publishes its item around that point, so without a pause the recording could stop
    /// between the icon being drawn and the registration it should be followed by — and a
    /// capture that ends too early looks exactly like one where nothing was attempted.
    /// </summary>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(3);

    /// <summary>Messages reported per fragment. Enough to read, not the whole capture — that is saved whole.</summary>
    private const int ReportedMessages = 5;

    /// <summary>
    /// What the capture is read for, and why each one matters: how many connections were opened
    /// on the bus at all, every name anyone asked for, the tray item's own name, and the call to
    /// the watcher together with its reply.
    /// </summary>
    private static readonly string[] TellingFragments =
    [
        "member=Hello",
        "member=RequestName",
        BusTraffic.ItemNamePrefix,
        "member=RegisterStatusNotifierItem",
    ];

    /// <summary>
    /// The app's whole conversation with the session bus, from before it starts to after it has
    /// drawn its gauge, written down and saved.
    /// </summary>
    [Trait("Category", "LinuxRuntime")]
    [Fact]
    public async Task TheAppsWholeConversationWithTheSessionBusIsRecorded()
    {
        if (!InRuntimeJob) return;

        // The recorder before anything else on the bus: the toolkit's first call is the one
        // most worth having, and a recording started afterwards could not prove it never came.
        await using var traffic = await BusTraffic.StartAsync(
            nameof(TheAppsWholeConversationWithTheSessionBusIsRecorded), output);

        Assert.True(traffic.IsRecording,
            $"there is no recording, so this test has no evidence to leave behind: {traffic.State}");

        // The host first, as in LinuxAppStartRuntimeTests: the app asks the bus for a
        // notification area once, before the toolkit starts.
        await using var host = await StandInTrayHost.StartAsync();
        await using var app = await LinuxAppProcess.StartAsync(
            output, nameof(TheAppsWholeConversationWithTheSessionBusIsRecorded));

        // The toolkit alive rather than merely launched, which is the point past which a tray
        // item would have been published if it was going to be.
        await app.WaitForLogAsync(LinuxAppProcess.IconRenderedLogLine, Patience);

        // Reported, never asserted: whether the name appears is the finding the capture
        // carries, and waiting for it is also how everything after the render gets a window
        // to arrive in — it ends early if the name does turn up.
        var itemName = await traffic.WaitForMentionAsync(BusTraffic.ItemNamePrefix, Settle);

        output.WriteLine($"a {BusTraffic.ItemNamePrefix}… name was asked for: {itemName}");
        output.WriteLine($"items that reached the stand-in host: {host.Items.Count}");
        ReportTellingMessages(traffic);
    }

    /// <summary>
    /// The capture reaches the folder the job uploads on a passing run too — the run this slice
    /// exists for, where nothing failed and the recording is the only thing worth keeping.
    /// </summary>
    [Trait("Category", "LinuxRuntime")]
    [Fact]
    public async Task ThePassingRunsCaptureIsSavedWhereTheJobCollectsIt()
    {
        if (!InRuntimeJob) return;

        var label = nameof(ThePassingRunsCaptureIsSavedWhereTheJobCollectsIt);
        var artifacts = Path.Combine(Path.GetTempPath(), $"oview-bus-traffic-{Guid.NewGuid():N}");
        var original = Environment.GetEnvironmentVariable("OVIEW_RUNTIME_ARTIFACTS");

        try
        {
            // A folder of this test's own rather than the job's, so what is checked here cannot
            // be satisfied by a capture another test saved. Nothing else is running meanwhile:
            // this assembly's tests are serialised (see LinuxAppStartRuntimeTests).
            Environment.SetEnvironmentVariable("OVIEW_RUNTIME_ARTIFACTS", artifacts);

            await using (var traffic = await BusTraffic.StartAsync(label, output))
            {
                Assert.True(traffic.IsRecording, traffic.State);

                // Traffic with a shape that can be recognised afterwards: taking
                // org.kde.StatusNotifierWatcher is a name request nothing else on this bus makes.
                await using var host = await StandInTrayHost.StartAsync();

                Assert.True(
                    await traffic.WaitForMentionAsync(StandInTrayHost.WatcherService, Patience),
                    $"the stand-in host took {StandInTrayHost.WatcherService} and the recorder never reported it");
            }

            var path = Path.Combine(artifacts, $"{label}.dbus.txt");
            Assert.True(File.Exists(path), $"{path} was not written, although nothing failed");

            var saved = File.ReadAllText(path);
            Assert.Contains(StandInTrayHost.WatcherService, saved);

            // The header as well as the messages: a capture that cannot say whether it was
            // really recording is not evidence of anything.
            Assert.Contains("recording every message", saved);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OVIEW_RUNTIME_ARTIFACTS", original);
            TryDelete(artifacts);
        }
    }

    /// <summary>
    /// With no session bus — the ordinary test job, and a developer's own machine — there is
    /// nothing to record, and asking for a recording is quietly nothing rather than a failure.
    /// </summary>
    [Fact]
    public async Task WithNoSessionBusItRecordsNothingAndThrowsNothing()
    {
        var bus = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
        var artifacts = Environment.GetEnvironmentVariable("OVIEW_RUNTIME_ARTIFACTS");

        try
        {
            // Both cleared rather than assumed unset, so this says the same thing wherever it
            // runs — and with no artifacts folder, nothing can be written anywhere either.
            Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", null);
            Environment.SetEnvironmentVariable("OVIEW_RUNTIME_ARTIFACTS", null);

            await using var traffic = await BusTraffic.StartAsync(
                nameof(WithNoSessionBusItRecordsNothingAndThrowsNothing), output);

            Assert.False(traffic.IsRecording);
            Assert.Equal(0, traffic.MessageCount);
            Assert.Contains("no session bus", traffic.State);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", bus);
            Environment.SetEnvironmentVariable("OVIEW_RUNTIME_ARTIFACTS", artifacts);
        }
    }

    /// <summary>
    /// The messages worth reading in the job's own log, so a capture that has to be downloaded
    /// is not the only way to see what happened.
    /// </summary>
    private void ReportTellingMessages(BusTraffic traffic)
    {
        output.WriteLine($"──── {traffic.MessageCount} messages captured ────");

        foreach (var fragment in TellingFragments)
        {
            var found = traffic.Mentioning(fragment);
            output.WriteLine($"──── {fragment}: {found.Count} ────");

            foreach (var message in found.Take(ReportedMessages))
            {
                output.WriteLine(message);
            }

            if (found.Count > ReportedMessages)
            {
                output.WriteLine($"… and {found.Count - ReportedMessages} more in the saved capture");
            }
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A temporary folder left behind is untidy, not a failure.
        }
    }
}
