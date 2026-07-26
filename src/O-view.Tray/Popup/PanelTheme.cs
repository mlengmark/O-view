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

        // Clickable stat tiles (issue #37): same one-step/two-step separation off
        // TileBg, so "this responds to a click" reads the same way everywhere.
        Set(resources, "TileHover", light ? "#E5E5E5" : "#343434");
        Set(resources, "TilePressed", light ? "#DADADA" : "#3E3E3E");

        // Hover cards float ABOVE both the panel and the tiles, so they step away from
        // each rather than matching either — otherwise a card over a tile reads as part
        // of it. Light goes brighter than the panel, dark goes lighter than the tile.
        Set(resources, "TooltipBg", light ? "#FFFFFF" : "#333333");
        Set(resources, "TooltipBorder", light ? "#CFCFCF" : "#4A4A4A");

        // ── categorical series, for the per-model tile charts (issue #37) ─────────
        //
        // Not decoration and not free choice: these are validated, and the dark column
        // is the same three hues re-stepped for the dark surface, not an auto-flip.
        // Checked with the six-check validator against THESE surfaces (TileBg), on the
        // all-pairs list — segment order follows the data, so any two can end up
        // adjacent:
        //
        //   light #EFEFEF : CVD ΔE 9.2 (target 8) · normal-vision ΔE 17.3 (floor 15)
        //   dark  #2B2B2B : CVD ΔE 9.4            · normal-vision ΔE 16.5
        //
        // On the LIGHT surface, Series2 (2.78:1), Series3 (2.45:1) and SeriesOther
        // (3.00:1) sit at or below the 3:1 contrast line. That is a documented relief
        // case, not a dismissable warning: the tile MUST ship visible labels, which is
        // why the breakdown always draws a legend naming each model and its value, and
        // a tooltip carrying every model exactly. Identity never rests on hue alone.
        //
        // Do not add a fourth chromatic slot. The next hue in the validated order is
        // yellow, which fails the all-pairs floors beside Series2's orange — that is
        // exactly why ModelBreakdown folds a fourth model into "Other" instead.
        Set(resources, "Series1", light ? "#2A78D6" : "#3987E5");
        Set(resources, "Series2", light ? "#EB6834" : "#D95926");
        Set(resources, "Series3", light ? "#1BAF7A" : "#199E70");
        // Neutral by design — it is a residual bucket, not an identity, so it is the one
        // slot deliberately below the chroma floor. Its separation IS still verified:
        // worst CVD ΔE 11.0 light / 12.2 dark against the two named slots it appears with.
        Set(resources, "SeriesOther", "#8A8A8A");
    }

    private static void Set(ResourceDictionary resources, string key, string hex) =>
        resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
}
