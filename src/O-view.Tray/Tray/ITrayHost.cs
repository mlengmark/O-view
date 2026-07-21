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

    /// <summary>Left-click on the icon — opens the popup.</summary>
    event EventHandler? IconClicked;

    /// <summary>Right-click on the icon — opens the context menu.</summary>
    event EventHandler? IconRightClicked;

    /// <summary>Threshold notification. Balloon-tip path — legacy but dependency-free (ADR-0005).</summary>
    void ShowNotification(string title, string message);
}
