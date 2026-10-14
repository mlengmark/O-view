using OView.App.Platform;
using OView.Core.Models;

namespace OView.App;

/// <summary>
/// The shell as a skin meets it: the one place the engine is built, started and disposed, and
/// the hop that puts every push to the skin on the skin's own thread.
///
/// <para>A skin asks through <see cref="ISkinToShell"/> and is told through
/// <see cref="IShellToSkin"/>, so what the shell is made of — the engine, its providers, its
/// timers — stays the shell's business and no skin names any of it (ADR-0012,
/// docs/architecture.md).</para>
///
/// <para><b>Every push goes through the dispatcher, including the ones that already arrive on
/// the UI thread.</b> A skin that may assume the UI thread on three callbacks and has to check
/// on the fourth is the thread-affinity bug waiting to be written — and both skins touch their
/// tray icon straight from these calls. One rule for all four is the only one a skin can
/// actually rely on (issue #125).</para>
/// </summary>
public sealed class ShellHost : IDisposable
{
    private readonly UsageEngine _engine;
    private readonly IShellToSkin _skin;
    private readonly IUiDispatcher _dispatcher;

    /// <param name="options">What the engine is composed of — the shell's own business.</param>
    /// <param name="dispatcher">
    /// The skin's thread. Construct this on that thread: the WPF implementation captures the
    /// dispatcher it is created on.
    /// </param>
    /// <param name="skin">
    /// Built from the shell it will ask through, rather than passed in ready-made, because the
    /// skin needs <see cref="Shell"/> and the shell needs the skin. Handing the factory the
    /// shell closes that circle at the one moment both exist, so neither can be left
    /// half-attached and no push can reach a skin that is still being built.
    /// </param>
    public ShellHost(
        UsageEngineOptions options,
        IUiDispatcher dispatcher,
        Func<ISkinToShell, IShellToSkin> skin)
    {
        _engine = new UsageEngine(options);
        _dispatcher = dispatcher;
        _skin = skin(_engine);

        _engine.SnapshotUpdated += PostSnapshot;
        _engine.NotificationRequested += PostNotification;
        _engine.UpdateCheckDue += PostUpdateCheckDue;
        _engine.RateCheckDue += PostRateCheckDue;
    }

    /// <summary>What a skin asks of the shell. The same object for the shell's whole life.</summary>
    public ISkinToShell Shell => _engine;

    /// <summary>
    /// Starts polling.
    ///
    /// <para>The dispatcher is handed to the engine as well, and not only used for the hop
    /// below: a scheduled read runs off the UI thread and publishes back onto it, which is the
    /// engine's own arrangement and not something this class can do on its behalf (issue
    /// #125).</para>
    /// </summary>
    public void Start(ITimerFactory timers) => _engine.Start(timers, _dispatcher);

    private void PostSnapshot(UsageSnapshot snapshot) =>
        _dispatcher.Post(() => _skin.ShowSnapshot(snapshot));

    private void PostNotification(AppNotification notification) =>
        _dispatcher.Post(() => _skin.ShowNotification(notification));

    private void PostUpdateCheckDue() => _dispatcher.Post(_skin.UpdateCheckDue);

    private void PostRateCheckDue() => _dispatcher.Post(_skin.RateCheckDue);

    /// <summary>
    /// Stops every timer and closes the rollup store. The four handlers go with the engine —
    /// nothing outside this class holds them.
    /// </summary>
    public void Dispose() => _engine.Dispose();
}
