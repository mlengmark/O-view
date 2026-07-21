using System.Drawing;

namespace OView.Tray.Tray;

/// <summary>
/// Abstraction over the notification-area integration (ADR-0001/0005): callers never
/// touch NotifyIcon directly, so the implementation can swap to raw Shell_NotifyIcon
/// P/Invoke without touching them if WinForms interop ever proves limiting.
/// </summary>
public interface ITrayHost : IDisposable
{
    /// <summary>Replace the tray icon and tooltip. Takes ownership of nothing — the bitmap may be disposed by the caller after return.</summary>
    void Update(Bitmap icon, string tooltip);

    /// <summary>Left-click on the icon — the Phase 4 popup hook.</summary>
    event EventHandler? IconClicked;
}
