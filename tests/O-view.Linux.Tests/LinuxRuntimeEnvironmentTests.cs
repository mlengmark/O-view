namespace OView.Linux.Tests;

/// <summary>
/// The linux-runtime CI job runs tests tagged <c>LinuxRuntime</c> under a virtual display
/// (Xvfb) and a session bus (dbus-run-session), so they can start the Linux head and drive it
/// the way a desktop would. Phase 0b adds the tests that click the tray icon through a
/// stand-in tray host; this one proves the environment those tests need actually comes up.
///
/// <para>Elsewhere, Windows, or the ordinary test job, there is no display or bus to check,
/// so it returns early. The job sets <c>OVIEW_LINUX_RUNTIME=1</c>.</para>
/// </summary>
[Trait("Category", "LinuxRuntime")]
public class LinuxRuntimeEnvironmentTests
{
    private static bool InRuntimeJob => Environment.GetEnvironmentVariable("OVIEW_LINUX_RUNTIME") == "1";

    [Fact]
    public void AVirtualDisplayIsAvailable()
    {
        if (!InRuntimeJob) return;

        Assert.False(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")),
            "DISPLAY is not set: the job is not running under xvfb-run");
    }

    [Fact]
    public void ASessionBusIsAvailable()
    {
        if (!InRuntimeJob) return;

        Assert.False(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")),
            "DBUS_SESSION_BUS_ADDRESS is not set: the job is not running under dbus-run-session");
    }
}
