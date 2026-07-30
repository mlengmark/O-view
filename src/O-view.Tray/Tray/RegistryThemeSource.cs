using OView.App.Platform;
using OView.Tray.Popup;

namespace OView.Tray.Tray;

/// <summary>
/// Windows theme detection, reading the two settings the platform genuinely keeps apart:
/// <c>SystemUsesLightTheme</c> for the notification area and <c>AppsUseLightTheme</c> for
/// app windows. A user can set them independently, so they are read from their own places
/// rather than one standing in for the other.
/// </summary>
public sealed class RegistryThemeSource : IThemeSource
{
    public bool IsTrayLight() => TaskbarTheme.IsLight();

    public bool IsPanelLight() => PanelTheme.IsAppsLight();
}
