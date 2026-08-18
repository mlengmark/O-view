using System.Windows.Threading;
using OView.App.Platform;

namespace OView.Tray;

/// <summary>
/// Hands a poll's result back to WPF's UI thread.
///
/// <para>Pairs with <see cref="DispatcherTimerFactory"/>: that puts the <i>tick</i> on the
/// UI thread, this brings the <i>result</i> back to it after the read has run on the thread
/// pool. <see cref="TrayController.Render"/> subscribes to the engine's events directly and
/// touches the tray icon from them, so publishing off the UI thread would be a
/// thread-affinity violation.</para>
///
/// <para>Windows has never reported the freeze that prompted this (issue #125) — the
/// exposure is identical, and no Windows machine has yet arrived with a transcript history
/// large enough to make a first ingest visible. Wiring both heads the same way is what
/// keeps it that way, and stops the two drifting apart on something neither user should
/// have to think about.</para>
/// </summary>
public sealed class WpfUiDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    public void Post(Action work) => _dispatcher.BeginInvoke(work);
}
