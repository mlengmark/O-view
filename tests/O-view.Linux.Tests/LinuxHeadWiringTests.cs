using System.Reflection;
using Avalonia.Controls;
using OView.App.Platform;
using OView.Linux.Platform;
using OView.Linux.Tray;

namespace OView.Linux.Tests;

/// <summary>
/// Does the head actually <i>use</i> the shared platform seams?
///
/// <para><b>Why this file exists.</b> Three times in the v0.6.0 milestone, behaviour was
/// written in the shared layer, covered by tests, and then never wired to the Linux head:
/// the diagnostics copy, the update check, and run-at-startup. Every time the unit tests
/// passed — because the shared class genuinely works — and nothing anywhere asserted that a
/// head <i>constructs</i> it. <c>XdgAutostartRegistration</c> shipped in v0.6.0 fully
/// implemented, fully tested, and completely unreachable: no menu item, no flag, no way for
/// any user to turn it on.</para>
///
/// <para>These tests are structural, not behavioural. They cannot tell whether the icon
/// appears on a panel — nothing here can, without hardware — but they can tell whether the
/// wiring exists, which is the failure that kept recurring.</para>
///
/// <para>They assert on <b>constructed values</b> rather than declared field types, because
/// the fields are declared as the interface. Checking the declared type would pass while the
/// field held any implementation at all, including the wrong platform's.</para>
/// </summary>
public class LinuxHeadWiringTests
{
    // Constructing the app runs its field initialisers and nothing else — no toolkit, no
    // bus, no window. That is what makes the wiring inspectable without a desktop.
    private static readonly LinuxApp Head = new();

    private static object? SeamValue(Type seam) =>
        typeof(LinuxApp)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(f => seam.IsAssignableFrom(f.FieldType))
            .Select(f => f.GetValue(Head))
            .FirstOrDefault(v => v is not null);

    /// <summary>
    /// The regression this file was written for. Remove the field and run-at-startup silently
    /// becomes unreachable again, with nothing else in the suite noticing.
    /// </summary>
    [Fact]
    public void TheHeadWiresRunAtStartupToTheXdgImplementation()
    {
        Assert.IsType<XdgAutostartRegistration>(SeamValue(typeof(IStartupRegistration)));
    }

    [Fact]
    public void TheHeadWiresTheThemeSourceToThePortalImplementation()
    {
        Assert.IsType<PortalThemeSource>(SeamValue(typeof(IThemeSource)));
    }

    /// <summary>
    /// The general form: any platform seam with a Linux implementation must be reachable from
    /// the head.
    ///
    /// <para>Written as a sweep rather than one test per interface so that adding a seam to
    /// <c>O-view.App/Platform</c> and forgetting to wire it fails here automatically, instead
    /// of relying on someone remembering to add the test that would have caught it.</para>
    /// </summary>
    [Fact]
    public void EveryPlatformSeamWithALinuxImplementationIsReachableFromTheHead()
    {
        var seams = typeof(IStartupRegistration).Assembly
            .GetTypes()
            .Where(t => t.IsInterface && t.Namespace == "OView.App.Platform")
            .ToArray();

        Assert.NotEmpty(seams);

        var unwired = new List<string>();
        foreach (var seam in seams)
        {
            if (ResolvedElsewhere.Contains(seam.Name))
            {
                continue;
            }

            var implementations = new[] { typeof(LinuxApp).Assembly, seam.Assembly }
                .SelectMany(a => a.GetTypes())
                .Where(t => t is { IsInterface: false, IsAbstract: false } && seam.IsAssignableFrom(t))
                .Where(t => !t.Name.StartsWith("Registry", StringComparison.Ordinal))   // the Windows head's
                .ToArray();

            if (implementations.Length > 0 && SeamValue(seam) is null)
            {
                unwired.Add($"{seam.Name} (implemented by {string.Join(", ", implementations.Select(i => i.Name))})");
            }
        }

        Assert.True(unwired.Count == 0,
            "These platform seams have a Linux implementation that the head never constructs, so the "
            + "behaviour ships but no user can reach it:\n  " + string.Join("\n  ", unwired));
    }

    /// <summary>
    /// Seams deliberately resolved outside <see cref="LinuxApp"/>, with the reason. Kept as an
    /// explicit list so that excluding something is a decision someone wrote down, rather than
    /// a gap the sweep quietly fails to notice.
    ///
    /// <para><c>ISingleInstanceGuard</c> is claimed in <c>Program.Main</c> as a local, before
    /// the toolkit starts — a second icon and double polling is exactly what must not happen
    /// while Avalonia is initialising. It never reaches the app object, and a local is not
    /// visible to reflection at all, so <see cref="TheSingleInstanceGuardIsClaimedBeforeTheToolkitStarts"/>
    /// covers it by behaviour instead.</para>
    /// </summary>
    private static readonly HashSet<string> ResolvedElsewhere = ["ISingleInstanceGuard"];

    // ── bus callbacks run on the UI thread (issue #143) ─────────────────────────────
    //
    // Avalonia's SNI backend raises TrayIcon.Clicked and NativeMenuItem.Click on Tmds.DBus's
    // thread, not the UI thread. Building a window there segfaulted the app on the first left
    // click anyone ever gave it. These assert on the REAL delegates the head attaches, not on
    // a re-implementation — a handler that goes through BusCallback carries its declaring
    // type, so deleting the marshalling changes what the assertion sees.
    //
    // What they cannot do is prove the app no longer crashes: that needs a live session bus
    // and a dispatcher in one process, which is exactly the gap that let #124 ship.

    [Fact]
    public void TheTrayClickHandlerIsMarshalledOntoTheUiThread()
    {
        // The engine argument is only dereferenced when the work runs, and nothing here runs
        // it — so no store is opened and no path on this machine is touched.
        var handler = (EventHandler)typeof(LinuxApp)
            .GetMethod("TrayClickHandler", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(Head, [null])!;

        Assert.Equal(typeof(BusCallback), handler.Method.DeclaringType);
    }

    /// <summary>
    /// Every menu item too. A sweep rather than one test each, so a fourth row added without
    /// the marshalling fails here instead of waiting for a hardware report.
    /// </summary>
    [Fact]
    public void EveryMenuItemsClickHandlerIsMarshalledOntoTheUiThread()
    {
        var menu = (NativeMenu)typeof(LinuxApp)
            .GetMethod("BuildMenu", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(Head, null)!;

        var items = menu.Items.OfType<NativeMenuItem>().ToArray();
        Assert.NotEmpty(items);

        var clickField = typeof(NativeMenuItem).GetField("Click", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(clickField);   // the toolkit moved the event; the check below is blind without it

        foreach (var item in items)
        {
            var handlers = ((Delegate?)clickField!.GetValue(item))?.GetInvocationList() ?? [];

            Assert.NotEmpty(handlers);
            Assert.All(handlers, h => Assert.Equal(typeof(BusCallback), h.Method.DeclaringType));
        }
    }

    /// <summary>
    /// The seam the sweep above cannot see. Verified by using it the way the head does: the
    /// first claim succeeds, a second concurrent claim fails, and releasing frees it again.
    /// </summary>
    [Fact]
    public void TheSingleInstanceGuardIsClaimedBeforeTheToolkitStarts()
    {
        var directory = Directory.CreateTempSubdirectory("oview-guard-").FullName;
        try
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", directory);

            using (var first = new FileLockSingleInstanceGuard())
            {
                Assert.True(first.TryAcquire());

                using var second = new FileLockSingleInstanceGuard();
                Assert.False(second.TryAcquire());   // two icons and double polling is the thing to avoid
            }

            using var afterRelease = new FileLockSingleInstanceGuard();
            Assert.True(afterRelease.TryAcquire());
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", null);
            Directory.Delete(directory, recursive: true);
        }
    }
}
