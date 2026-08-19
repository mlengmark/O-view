using OView.App;
using OView.App.Platform;

namespace OView.Linux.Tray;

/// <summary>
/// Wraps a callback the session bus raises so it runs on the UI thread.
///
/// <para><b>Why this exists.</b> Avalonia's StatusNotifierItem backend raises
/// <c>TrayIcon.Clicked</c> and <c>NativeMenuItem.Click</c> on whatever thread Tmds.DBus
/// delivered the method call on — <i>not</i> the UI thread. Read out of the shipped
/// <c>Avalonia.FreeDesktop</c> 12.1.1 assembly rather than assumed:
/// <c>DBusTrayIconImpl</c>'s activation handler is <c>OnClicked?.Invoke()</c> with no
/// dispatcher call anywhere in the type, and <c>DBusHelper.TryCreateNewConnection</c>
/// deliberately clears the <c>SynchronizationContext</c> around the connection so its
/// continuations do not come back to the UI thread.</para>
///
/// <para>The Linux head built and showed an Avalonia <c>Window</c> straight from that
/// callback. Xlib is not thread-safe without <c>XInitThreads</c>, so touching it from a
/// foreign thread is a <b>SIGSEGV, not an exception</b> — which is why the <c>try/catch</c>
/// around the panel saw nothing and the user got
/// <c>segmentation fault (core dumped)</c> on their first left click (issue #143).</para>
///
/// <para><b>A named type rather than a lambda,</b> so the wiring is assertable without a
/// live bus: a handler built here has <c>Method.DeclaringType == typeof(BusCallback)</c>,
/// which a structural test can check on the real delegate the head attaches. No test on this
/// machine can reach a session bus and a dispatcher together — that gap is what let the #124
/// deadlock ship — so "the marshalling is still there" is the guarantee worth having.</para>
///
/// <para><see cref="IUiDispatcher.Post"/> and not a blocking invoke: the D-Bus reply must not
/// wait on the UI thread. That is the #124 deadlock pointed the other way.</para>
/// </summary>
public sealed class BusCallback(IUiDispatcher dispatcher, Action work, IAppLog? log)
{
    /// <summary>An <see cref="EventHandler"/> that hands <paramref name="work"/> to the UI thread.</summary>
    public static EventHandler For(IUiDispatcher dispatcher, Action work, IAppLog? log = null) =>
        new BusCallback(dispatcher, work, log).Invoke;

    private void Invoke(object? sender, EventArgs e)
    {
        try
        {
            dispatcher.Post(work);
        }
        catch (Exception ex)
        {
            // The dispatcher is gone — the app is shutting down, which is reachable here
            // because "Exit O-view" is itself one of these callbacks. Dropping the work is
            // correct; there is no longer a UI thread to run it on. Logged rather than
            // swallowed silently, so it cannot be mistaken for a click that did nothing.
            log?.Write($"bus callback dropped {ex.GetType().Name}: {ex.Message}");
        }
    }
}
