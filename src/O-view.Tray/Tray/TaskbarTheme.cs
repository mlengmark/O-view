using System.IO;
using Microsoft.Win32;

namespace OView.Tray.Tray;

/// <summary>
/// Light/dark taskbar detection. The taskbar does not restyle icons on theme change
/// (ADR-0003 item 4), so the renderer reads this on every refresh — a registry read
/// is cheap at a 60 s cadence — and picks contrast-appropriate shades.
/// </summary>
public static class TaskbarTheme
{
    /// <summary>
    /// True when the taskbar uses the light theme. SystemUsesLightTheme governs the
    /// taskbar/tray; AppsUseLightTheme (a different value) governs app windows.
    /// Defaults to dark — the Windows 11 default — when unreadable.
    /// </summary>
    public static bool IsLight()
    {
        try
        {
            return Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "SystemUsesLightTheme", 0) is 1;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException)
        {
            return false;
        }
    }
}
