using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using OView.App;
using OView.App.Platform;
using OView.Core.Models;
using OView.Linux.Panel;
using OView.Linux.Platform;
using OView.Linux.Tray;
using OView.Presentation.Text;

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
    /// Where a dragged panel's place is kept, named rather than only swept for: the sweep below
    /// would be just as green if the head held the Windows head's store, or any other
    /// implementation of the seam, and what the panel reopening in the right spot depends on is
    /// this one — the file in O-view's own data folder, with the corner translation
    /// <c>PanelPositionFile</c> cannot do for itself.
    /// </summary>
    [Fact]
    public void TheHeadWiresThePanelPositionStoreToTheFileBackedImplementation()
    {
        Assert.IsType<FilePanelPositionStore>(SeamValue(typeof(IPanelPositionStore)));
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
        // The shell argument is only dereferenced when the work runs, and nothing here runs
        // it — so no store is opened and no path on this machine is touched.
        Assert.Equal(typeof(BusCallback), OpenPanelHandler().Method.DeclaringType);
    }

    /// <summary>
    /// Every menu item too. A sweep rather than one test each, so a fifth row added without
    /// the marshalling fails here instead of waiting for a hardware report.
    /// </summary>
    [Fact]
    public void EveryMenuItemsClickHandlerIsMarshalledOntoTheUiThread()
    {
        var items = Menu().Items.OfType<NativeMenuItem>().ToArray();
        Assert.NotEmpty(items);

        foreach (var item in items)
        {
            var handlers = ClickHandlersOf(item);

            Assert.NotEmpty(handlers);
            Assert.All(handlers, h => Assert.Equal(typeof(BusCallback), h.Method.DeclaringType));
        }
    }

    /// <summary>
    /// The row that opens the panel does it through the <i>same</i> handler the icon's click
    /// does (issue #315), rather than through a second call to the same private method.
    ///
    /// <para>Asserted as the work the two <see cref="BusCallback"/>s wrap, which is the only
    /// part a copy would differ in: both delegates declare <c>BusCallback</c> whatever they
    /// were built from, so the sweep above would be just as green if the row opened a panel of
    /// its own — with its own marshalling to get wrong, and its own "panel opened" line to
    /// mislead whoever read the log after the next report.</para>
    /// </summary>
    [Fact]
    public void TheRowThatOpensThePanelRunsTheIconClicksOwnHandler()
    {
        var row = Assert.IsType<NativeMenuItem>(Menu().Items[0]);
        Assert.Equal(MenuText.ShowUsageRow, row.Header);

        Assert.Equal(
            WorkBehind(OpenPanelHandler()),
            WorkBehind(Assert.Single(ClickHandlersOf(row))));
    }

    /// <summary>The handler the head attaches to the tray icon, built with no shell (see above).</summary>
    private static EventHandler OpenPanelHandler() =>
        (EventHandler)typeof(LinuxApp)
            .GetMethod("OpenPanelHandler", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(Head, [null])!;

    /// <summary>
    /// The real menu the head builds, for a session that can remember where the panel was put.
    /// Null shell, for the reason <see cref="TheTrayClickHandlerIsMarshalledOntoTheUiThread"/>
    /// gives: it is captured in closures and only dereferenced when a click runs, and nothing
    /// here runs one.
    /// </summary>
    private static NativeMenu Menu() => Menu(ClientPlacement.From("x11", null, ":0"));

    /// <summary>
    /// The menu as <paramref name="placement"/> would have it. Passed in rather than read from
    /// the machine, which is in neither Linux session — it is how the Wayland branch's missing
    /// row is assertable from Windows CI at all.
    /// </summary>
    private static NativeMenu Menu(ClientPlacement placement) =>
        (NativeMenu)typeof(LinuxApp)
            .GetMethod("BuildMenu", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(Head, [null, placement])!;

    private static Delegate[] ClickHandlersOf(NativeMenuItem item)
    {
        var clickField = typeof(NativeMenuItem).GetField("Click", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(clickField);   // the toolkit moved the event; every check on it is blind without it

        return ((Delegate?)clickField!.GetValue(item))?.GetInvocationList() ?? [];
    }

    /// <summary>
    /// What a marshalled handler will actually run, as a method rather than as a closure: two
    /// handlers built from one factory capture different objects but share this, and two built
    /// from separate lambdas cannot.
    /// </summary>
    private static MethodInfo WorkBehind(Delegate handler)
    {
        var callback = Assert.IsType<BusCallback>(handler.Target);

        var work = (Action?)callback.GetType()
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(f => f.FieldType == typeof(Action))
            .GetValue(callback);

        Assert.NotNull(work);
        return work!.Method;
    }

    // ── the shell reaches this skin on the UI thread, and only through it ────────────
    //
    // The head is the shell's skin now rather than a subscriber to its events, and what makes
    // that safe is the one hop it hands over: the shell posts every push through the
    // dispatcher it was given (O-view.App.Tests' ShellToSkinDispatchTests), and the two tests
    // below are the Linux halves of the same claim — that this head IS the skin being pushed
    // at, and that the dispatcher it hands over really lands work on Avalonia's UI thread
    // rather than running it where it was raised.
    //
    // What neither can add, on a machine with no Linux desktop, is a real click arriving from
    // the session bus. The linux-runtime job does that.

    [Fact]
    public void TheHeadIsTheSkinTheShellPushesAt()
    {
        Assert.IsAssignableFrom<IShellToSkin>(Head);

        // And holds the other half to ask back through. It is null until Program attaches the
        // shell, so the field itself is what there is to assert here.
        Assert.Contains(
            typeof(LinuxApp).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            f => f.FieldType == typeof(ISkinToShell));
    }

    [Fact]
    public void EveryShellToSkinCallReachesTheSkinOnTheUiThread()
    {
        // Claimed before the first real touch below — see AvaloniaDispatcherTestSupport for why.
        AvaloniaDispatcherTestSupport.ClaimUiThreadForThisThread();

        var dispatcher = Assert.IsType<AvaloniaUiDispatcher>(SeamValue(typeof(IUiDispatcher)));
        var skin = new ThreadRecordingSkin();

        (string Name, Action Push)[] pushes =
        [
            ("snapshot", () => skin.ShowSnapshot(UsageSnapshot.None)),
            ("notification", () => skin.ShowNotification(new AppNotification("title", "message"))),
            ("update check", skin.UpdateCheckDue),
            ("rate check", skin.RateCheckDue),
        ];

        // A fifth push added to the interface is then uncovered here until someone adds it,
        // which this says out loud rather than leaving to be noticed.
        Assert.Equal(typeof(IShellToSkin).GetMethods().Length, pushes.Length);

        foreach (var (_, push) in pushes)
        {
            dispatcher.Post(push);
        }

        // Nothing has run yet: the hop is a real queue, not a call that happens to be made on
        // the right thread. A dispatcher that invoked inline would pass the thread check below
        // while leaving a bus-raised push exactly where it started.
        Assert.Empty(skin.Calls);

        Dispatcher.UIThread.RunJobs();

        Assert.Equal(pushes.Select(p => p.Name), skin.Calls);
        Assert.True(skin.ArrivedOffTheUiThread.Count == 0,
            "these pushes reached the skin off Avalonia's UI thread, where touching the tray "
            + $"icon or a window is a SIGSEGV rather than an exception (issue #143): "
            + string.Join(", ", skin.ArrivedOffTheUiThread));
    }

    /// <summary>
    /// Records what it was told and whether the UI thread told it. Deliberately not the head
    /// itself: a push into the real one draws into a tray icon that does not exist on this
    /// machine and says nothing either way, whereas this can report the thread it arrived on.
    /// </summary>
    private sealed class ThreadRecordingSkin : IShellToSkin
    {
        public List<string> Calls { get; } = [];

        public List<string> ArrivedOffTheUiThread { get; } = [];

        public void ShowSnapshot(UsageSnapshot snapshot) => Record("snapshot");

        public void ShowNotification(AppNotification notification) => Record("notification");

        public void UpdateCheckDue() => Record("update check");

        public void RateCheckDue() => Record("rate check");

        private void Record(string call)
        {
            Calls.Add(call);
            if (!Dispatcher.UIThread.CheckAccess())
            {
                ArrivedOffTheUiThread.Add(call);
            }
        }
    }

    /// <summary>
    /// Every row this head builds takes its label from Presentation, so the rows it shares
    /// with the Windows menu cannot drift apart from it.
    ///
    /// <para>Two of them had their own copies, and nothing would have failed if one had become
    /// "Quit" — the only other check on these strings is the runtime D-Bus sweep, which asks
    /// a real desktop for the rows and so cannot run on a machine without one.</para>
    ///
    /// <para>Asserted in order, because the order is itself the point of the first row: the
    /// panel is what a person came to the menu for (issue #315), and a row for it below the
    /// two that were already there would be the thing they read last.</para>
    /// </summary>
    [Fact]
    public void EveryMenuRowTakesItsLabelFromPresentationInTheOrderTheyAreDrawn()
    {
        var headers = Menu().Items.OfType<NativeMenuItem>().Select(i => i.Header ?? "").ToArray();

        Assert.Equal(
            [
                MenuText.ShowUsageRow,

                // Directly below the row that opens the panel: someone opening the menu for
                // this has a panel somewhere they cannot get back from, so it must not be
                // something to hunt for.
                MenuText.ResetPanelPositionRow,

                MenuText.RunAtStartupRow,

                // Between the two it has something to say about and the one that ends the app,
                // and hidden until the refresh has blocked itself — so a person sees four rows.
                PanelText.UsageRefreshBlockedRow,
                MenuText.ExitRow,
            ],
            headers);
    }

    /// <summary>
    /// And on a native Wayland session the reset row is not there at all, because there is
    /// nothing for it to reset: a client cannot read its own position back, so no place is
    /// stored and the panel keeps the work-area corner. A row offering the reset would be
    /// O-view claiming a thing it cannot do.
    ///
    /// <para>Unlike the two rows above that appear only when they have something to say, this
    /// one is absent on a whole class of session rather than a passing state — which is why its
    /// absence is asserted rather than left to the row order above.</para>
    /// </summary>
    [Fact]
    public void NativeWaylandGetsNoResetRowBecauseNoPlaceIsEverStoredThere()
    {
        var wayland = ClientPlacement.From("wayland", "wayland-0", x11Display: null);
        Assert.False(wayland.Allowed);   // the branch this test is about

        var headers = Menu(wayland).Items.OfType<NativeMenuItem>().Select(i => i.Header ?? "").ToArray();

        Assert.DoesNotContain(MenuText.ResetPanelPositionRow, headers);
        Assert.Equal(
            [MenuText.ShowUsageRow, MenuText.RunAtStartupRow, PanelText.UsageRefreshBlockedRow, MenuText.ExitRow],
            headers);
    }

    /// <summary>
    /// The sentences the no-host notice sends are Presentation's; what stays in the probe is
    /// the mapping, because <see cref="TrayHostState"/> is a D-Bus observation Presentation
    /// cannot see (docs/architecture.md).
    ///
    /// <para>Asserted per case, including the one no notice currently shows: the point is
    /// that a state cannot fall through to a sentence written for a different one. "I could
    /// not ask" and "I asked and nothing was there" are different facts, and only one of them
    /// justifies telling the user to install an extension.</para>
    /// </summary>
    [Fact]
    public void EveryTrayHostStateExplainsItselfFromPresentation()
    {
        Assert.Equal(NoticeText.TrayHostPresent, SniHostProbe.Explain(TrayHostState.Present));
        Assert.Equal(NoticeText.TrayHostAbsent, SniHostProbe.Explain(TrayHostState.Absent));
        Assert.Equal(NoticeText.TrayHostUnknown, SniHostProbe.Explain(TrayHostState.Unknown));
    }

    /// <summary>
    /// The no-host notice the head builds is the one that waits, not an immediate send.
    ///
    /// <para>The structural half of the fix for a notice that fired at every GNOME login a
    /// moment before the icon appeared: what the wait does once it is over is
    /// <see cref="TrayHostNoticeTests"/>'s, and what is asserted here is that the head reaches
    /// it at all, carrying the real ten seconds rather than a test's. It cannot prove
    /// <c>StartTray</c> still routes the notice through this — nothing on a machine with no
    /// Linux desktop can — but restoring a direct <c>ShowAsync</c> beside it would leave this
    /// notice built and unused, which is the shape of the three regressions this file opens
    /// with.</para>
    /// </summary>
    [Fact]
    public void TheHeadsNoTrayHostNoticeWaitsBeforeItSpeaks()
    {
        var notice = (TrayHostNotice)typeof(LinuxApp)
            .GetMethod("BuildHostNotice", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(Head, [])!;

        Assert.Equal(TrayHostNotice.DefaultGrace, notice.Grace);
        Assert.True(notice.Grace > TimeSpan.Zero, "a notice that waits for no time is the defect");
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
