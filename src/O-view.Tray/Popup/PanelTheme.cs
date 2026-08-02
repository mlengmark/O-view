using System.Windows;
using OView.App.Rendering;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace OView.Tray.Popup;

/// <summary>
/// The single palette both flyout surfaces draw from â€” the detail panel and the tray
/// menu (issue #33). They are one product and open in the same corner, so a colour
/// defined twice is a colour that eventually disagrees with itself; the menu was added
/// against this shared set rather than a copy of the panel's.
///
/// Theme follows <c>AppsUseLightTheme</c> â€” the app-window setting, which is distinct
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
        // Values and the reasoning behind them live in OView.App.Rendering.PanelPalette,
        // shared with the Linux panel. The accent contrast ratios and the CVD separation
        // figures for the categorical series are recorded there — restating the hex here
        // would carry the colour but not the constraint that produced it (issue #52).
        foreach (var (key, hex) in PanelPalette.All(light))
        {
            Set(resources, key, hex);
        }
    }
    private static void Set(ResourceDictionary resources, string key, string hex) =>
        resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

    /// <summary>
    /// One palette entry, for code with no resource tree to resolve through â€” the sample
    /// renderers composite their cards onto a flat backdrop and need the same colour the
    /// app itself would paint there.
    ///
    /// <para>They used to restate those colours as hex literals, which is precisely the
    /// failure this class exists to prevent: a colour defined twice is a colour that
    /// eventually disagrees with itself, and a verification image that has quietly stopped
    /// matching the app is worse than no image at all (GitHub issue #52).</para>
    /// </summary>
    public static System.Windows.Media.Brush Brush(string key, bool light)
    {
        var resources = new ResourceDictionary();
        Apply(resources, light);
        return (System.Windows.Media.Brush)resources[key];
    }
}
