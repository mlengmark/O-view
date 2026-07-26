using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace OView.Tray.Popup;

/// <summary>
/// The single palette both flyout surfaces draw from — the detail panel and the tray
/// menu (issue #33). They are one product and open in the same corner, so a colour
/// defined twice is a colour that eventually disagrees with itself; the menu was added
/// against this shared set rather than a copy of the panel's.
///
/// Theme follows <c>AppsUseLightTheme</c> — the app-window setting, which is distinct
/// from the taskbar's <c>SystemUsesLightTheme</c> that the tray icon reads. It is
/// re-read on every open, so switching theme never needs a restart.
/// </summary>
internal static class PanelTheme
{
    public static bool IsAppsLight()
    {
        try
        {
            return Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1) is 1;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or System.IO.IOException)
        {
            return true;
        }
    }

    /// <summary>
    /// Writes the palette into a window's own resource dictionary, where the
    /// <c>DynamicResource</c> references in its XAML pick it up. A superset: a surface
    /// simply does not reference the keys it has no use for.
    /// </summary>
    public static void Apply(ResourceDictionary resources, bool light)
    {
        Set(resources, "PanelBg", light ? "#F9F9F9" : "#202020");
        Set(resources, "PanelBorder", light ? "#D6D6D6" : "#383838");
        Set(resources, "TextPrimary", light ? "#1A1A1A" : "#F0F0F0");
        Set(resources, "TextSecondary", light ? "#555555" : "#B5B5B5");
        Set(resources, "TextMuted", light ? "#8A8A8A" : "#8A8A8A");
        Set(resources, "TileBg", light ? "#EFEFEF" : "#2B2B2B");
        Set(resources, "BarTrack", light ? "#DDDDDD" : "#3A3A3A");
        Set(resources, "BadgeBg", light ? "#E4DCF5" : "#3A3355");
        Set(resources, "BadgeText", light ? "#4A3A85" : "#C7BDEB");
        Set(resources, "WarnBg", light ? "#F7EBD4" : "#453A22");
        Set(resources, "WarnText", light ? "#8A5D00" : "#E3B858");
        // Menu rows: one step off the panel for hover, two for pressed — the same
        // separation Windows 11's own flyouts use for list rows.
        Set(resources, "RowHover", light ? "#ECECEC" : "#2E2E2E");
        Set(resources, "RowPressed", light ? "#E0E0E0" : "#3A3A3A");
    }

    private static void Set(ResourceDictionary resources, string key, string hex) =>
        resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
}
