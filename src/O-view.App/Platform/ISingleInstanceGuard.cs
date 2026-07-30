namespace OView.App.Platform;

/// <summary>
/// Ensures one O-view per user session. Two instances would mean two tray icons and
/// double polling (ADR-0003 item 7) — platform-independent reasoning, platform-specific
/// mechanism.
///
/// <para>Disposing releases the claim. A guard that has not acquired must still be safe to
/// dispose, because the losing instance shuts down through the same path as the winner.</para>
/// </summary>
public interface ISingleInstanceGuard : IDisposable
{
    /// <summary>
    /// True when this process is the first instance. Call once; the result is the
    /// process's identity for the rest of its life.
    /// </summary>
    bool TryAcquire();
}
