using OView.App.Platform;

namespace OView.Tray.Tray;

/// <summary>
/// Single-instance on Windows via a named mutex, where the kernel releases the name when
/// the owning process dies however it dies — so the failure mode that pushes Linux towards
/// a lock file (<see cref="FileLockSingleInstanceGuard"/>) does not arise here.
///
/// <para><b>The name is a compatibility surface.</b> An installed build already holds
/// <c>OView.Tray.SingleInstance</c>; changing it would let an old and a new instance run
/// side by side during an in-place update — two icons, double polling, and a very
/// confusing bug report. It stays exactly as it is.</para>
/// </summary>
public sealed class MutexSingleInstanceGuard : ISingleInstanceGuard
{
    /// <summary>Frozen. See the class remarks before touching it.</summary>
    public const string MutexName = "OView.Tray.SingleInstance";

    private Mutex? _mutex;

    public bool TryAcquire()
    {
        if (_mutex is not null)
        {
            return true;
        }

        // Two instances would mean two icons and double polling (ADR-0003 item 7).
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            mutex.Dispose();
            return false;
        }

        _mutex = mutex;
        return true;
    }

    public void Dispose()
    {
        _mutex?.Dispose();
        _mutex = null;
    }
}
