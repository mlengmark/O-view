using Avalonia.Threading;
using OView.App.Platform;

namespace OView.Linux;

/// <summary>
/// Hands a poll's result back to Avalonia's UI thread.
///
/// <para>Pairs with <see cref="AvaloniaTimerFactory"/>: that puts the <i>tick</i> on the UI
/// thread, this brings the <i>result</i> back to it after the read has run on the thread
/// pool. Without the pair, a first ingest over a large transcript history holds the
/// dispatcher for its whole duration — and on Linux the dispatcher also serves the
/// StatusNotifierItem and its DBusMenu, so the icon and the menu freeze with it
/// (issue #125).</para>
/// </summary>
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public void Post(Action work) => Dispatcher.UIThread.Post(work);
}
