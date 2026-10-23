using System.Reflection;
using Avalonia.Threading;

namespace OView.Linux.Tests;

/// <summary>
/// The one sanctioned way a unit test may touch the real <see cref="Dispatcher.UIThread"/>.
///
/// <para><b>Why this exists.</b> <c>Dispatcher.UIThread</c> binds itself, once, to whichever
/// thread touches it first in the whole process (<c>s_uiThread ??= this</c>, with
/// <c>CheckAccess()</c> comparing the live <see cref="Thread"/> object the dispatcher captured
/// against <see cref="Thread.CurrentThread"/>) and never lets go. xunit hands each test a thread
/// from its pool, and on a busy machine the pool retires and replaces worker threads far more
/// eagerly than on an idle one, including, sometimes, reissuing a just-retired thread's managed
/// id to the brand-new thread that replaces it. The dispatcher's comparison is by object, not by
/// id, so a later test can be handed a thread that reports the very id the dispatcher bound to
/// earlier and still fail "the calling thread cannot access this object because a different
/// thread owns it": the id matches, the thread does not.</para>
///
/// <para>Avalonia ships <c>Dispatcher.ResetBeforeUnitTests()</c> for exactly this, as an
/// internal method with no public surface, reached here through reflection so that whatever
/// thread the dispatcher was bound to is forgotten, and the next touch of
/// <see cref="Dispatcher.UIThread"/> binds fresh to whichever thread calls it. That must be the
/// calling test's own thread, so call this first, before any other code in the test (including a
/// seam that posts through it) touches the real dispatcher.</para>
/// </summary>
public static class AvaloniaDispatcherTestSupport
{
    public static void ClaimUiThreadForThisThread()
    {
        var reset = typeof(Dispatcher).GetMethod(
            "ResetBeforeUnitTests",
            BindingFlags.Static | BindingFlags.NonPublic);

        // If Avalonia ever renames or removes this, the fix needs revisiting, not a silent
        // no-op that lets the race back in unnoticed.
        Assert.NotNull(reset);
        reset!.Invoke(null, null);
    }
}
