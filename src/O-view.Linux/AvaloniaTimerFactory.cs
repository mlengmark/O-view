using Avalonia.Threading;
using OView.App;

namespace OView.Linux;

/// <summary>
/// Backs the engine's timers with Avalonia's <see cref="DispatcherTimer"/>, so every tick
/// arrives on the UI thread and a callback that touches the tray icon needs no marshalling.
/// The WPF head does the same with its own dispatcher — which is the reason the engine
/// takes a factory rather than reaching for <c>System.Threading.Timer</c> itself.
/// </summary>
public sealed class AvaloniaTimerFactory : ITimerFactory
{
    public IAppTimer Create(TimeSpan interval, Action onTick) => new AvaloniaAppTimer(interval, onTick);

    private sealed class AvaloniaAppTimer : IAppTimer
    {
        private readonly DispatcherTimer _timer;

        public AvaloniaAppTimer(TimeSpan interval, Action onTick)
        {
            _timer = new DispatcherTimer { Interval = interval };
            _timer.Tick += (_, _) => onTick();
        }

        public TimeSpan Interval
        {
            get => _timer.Interval;
            set => _timer.Interval = value;
        }

        public void Start() => _timer.Start();

        public void Stop() => _timer.Stop();

        public void Dispose() => _timer.Stop();
    }
}
