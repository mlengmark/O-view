using Xunit.Abstractions;

namespace OView.Linux.Tests.Runtime;

/// <summary>
/// Where the tooltip text actually ends up on the session bus, asked of the real app by a real
/// notification area.
///
/// <para><b>Why this exists.</b> Hovering the icon on GNOME shows nothing
/// (<a href="https://github.com/mlengmark/O-view/issues/314">#314</a>), and the report could
/// only say what one VM did on one evening. The text is built correctly — that is held by
/// <c>TooltipFormatterTests</c> — and the head sets it on every poll, so the question left over
/// is which <c>org.kde.StatusNotifierItem</c> property carries it once the toolkit has
/// published the item. That is a fact about the bus, and this is the only place in the
/// repository that can read one.</para>
///
/// <para><b>What it found.</b> The text arrives as <c>Title</c>. <c>ToolTip</c> — the property a
/// notification area draws on hover — is published empty, because Avalonia's tray backend
/// hard-codes it so: <c>StatusNotifierItemDbusObj</c> answers <c>ToolTip</c> with
/// <c>("", [], "", "")</c> and its one setter, <c>SetTitleAndTooltip</c>, fills <c>Id</c>,
/// <c>Category</c> and <c>Title</c> and nothing else (Avalonia 12.1.3,
/// <c>src/Avalonia.FreeDesktop/DBusTrayIconImpl.cs</c>). Nothing the head can set reaches it.</para>
///
/// <para><b>Why that is not worked around.</b> The property is published by the toolkit's own
/// bus object, which O-view does not own, and the desktop the report came from would not draw
/// it anyway: Ubuntu's AppIndicators extension has <c>ToolTip</c> and <c>NewToolTip</c>
/// commented out of its interface description — "<i>We disable this as we don't support
/// tooltip</i>" — so it never asks for the property at all. The README's support matrix says
/// so, and names the panel as where the exact figures live on that desktop.</para>
///
/// <para><b>Both assertions are meant to fail if that changes.</b> A filled <c>ToolTip</c> from
/// a later Avalonia is good news, and the README line and #314 then need revisiting — which is
/// a red test, not a quiet one.</para>
///
/// <para>Runs only in the <c>linux-runtime</c> job, which provides the display
/// (<c>xvfb-run</c>), the bus (<c>dbus-run-session</c>) and <c>OVIEW_LINUX_RUNTIME=1</c>.
/// Elsewhere there is no bus to ask, so it returns early the way the other runtime tests
/// do.</para>
/// </summary>
[Trait("Category", "LinuxRuntime")]
public class TrayTooltipRuntimeTests(ITestOutputHelper output)
{
    private static bool InRuntimeJob => Environment.GetEnvironmentVariable("OVIEW_LINUX_RUNTIME") == "1";

    /// <summary>
    /// How long any one step may take. Generous because this is a shared CI runner starting a
    /// GUI toolkit cold: nothing here measures a duration, it only refuses to hang for ever.
    /// </summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The text the head sets on the icon before the first snapshot exists
    /// (<c>LinuxApp.StartTray</c>). Reading it back would mean the live text never reached the
    /// bus at all, which is a different failure from the one under test.
    /// </summary>
    private const string PlaceholderTooltip = "O-view";

    /// <summary>
    /// The tooltip text as a notification area sees it: published, and in which property.
    ///
    /// <para>One test rather than two, because they are one reading. A host that has the item
    /// asks it for both properties over the same connection in the same breath, and splitting
    /// them would start the app twice in a job that has fifteen minutes for everything.</para>
    /// </summary>
    [Fact]
    public async Task TheTooltipTextReachesTheBusAsTitleAndTheToolTipPropertyIsEmpty()
    {
        if (!InRuntimeJob) return;

        // Recording from before the app starts, so the capture the job uploads carries the
        // property reads below and their replies — the evidence for issue #314, in the bus's
        // own words rather than this test's.
        await using var traffic = await BusTraffic.StartAsync(
            nameof(TheTooltipTextReachesTheBusAsTitleAndTheToolTipPropertyIsEmpty), output);

        await using var host = await StandInTrayHost.StartAsync();
        await using var app = await LinuxAppProcess.StartAsync(
            output, nameof(TheTooltipTextReachesTheBusAsTitleAndTheToolTipPropertyIsEmpty));

        var registered = await app.WaitForAsync(
            host.WaitForItemAsync(Patience), Patience, "the icon to reach the stand-in host");

        // After the registration, deliberately: the head sets the live text on every render,
        // and reading before one has happened would find the placeholder and prove nothing.
        await app.WaitForLogAsync(LinuxAppProcess.IconRenderedLogLine, Patience);

        var title = await host.ReadTitleAsync(registered);
        var tooltip = await host.ReadToolTipAsync(registered);

        output.WriteLine($"Title   : {title ?? "(could not be read)"}");
        output.WriteLine($"ToolTip : {Describe(tooltip)}");

        // The text is on the bus, and it is the rendered one rather than the placeholder the
        // icon was built with.
        Assert.False(string.IsNullOrWhiteSpace(title),
            "the item published no Title, so the tooltip text reaches the bus nowhere at all");
        Assert.NotEqual(PlaceholderTooltip, title);

        // And it is not where a host would look for it. Published-but-empty rather than
        // absent: the toolkit answers the property, with nothing in it.
        Assert.NotNull(tooltip);
        Assert.True(tooltip.IsEmpty,
            $"ToolTip is no longer empty ({Describe(tooltip)}). If the toolkit has started " +
            "publishing it, the README's Linux tooltip row and issue #314 both need revisiting.");

        // A reading that killed the app would make the two answers above worthless.
        Assert.False(app.HasExited, "o-view exited while its properties were being read");
    }

    private static string Describe(TrayIconToolTip? tooltip) => tooltip switch
    {
        null => "(could not be read)",
        { IsEmpty: true } => "(empty)",
        var filled => $"icon={filled.IconName} pixmaps={filled.IconPixmap.Count} " +
                      $"title={filled.Title} description={filled.Description}",
    };
}
