using System.Windows.Threading;
using OView.App;

namespace OView.Tray;

/// <summary>
/// Backs the engine's timers with WPF's <see cref="DispatcherTimer"/>, which keeps every
/// callback on the UI thread — so a tick that surfaces a balloon or a modal dialog needs no
/// marshalling. That property is why the engine takes a timer factory rather than reaching
/// for <c>System.Threading.Timer</c> itself.
/// </summary>
public sealed class DispatcherTimerFactory : ITimerFactory
{
    public IAppTimer Create(TimeSpan interval, Action onTick) => new DispatcherAppTimer(interval, onTick);

    private sealed class DispatcherAppTimer : IAppTimer
    {
        private readonly DispatcherTimer _timer;

        public DispatcherAppTimer(TimeSpan interval, Action onTick)
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
