using System.Diagnostics;
using OView.App.Platform;
using Tmds.DBus.Protocol;

namespace OView.Linux.Platform;

/// <summary>
/// Light/dark from the desktop, via the XDG desktop portal.
///
/// <para><b>The portal first, not <c>gsettings</c>.</b> <c>org.freedesktop.appearance</c>
/// is the cross-desktop answer and works under GNOME and KDE, X11 and Wayland;
/// <c>gsettings</c> is GNOME-specific and simply wrong on Plasma. It is kept only as a
/// fallback for a desktop with no portal.</para>
///
/// <para>Linux exposes <b>one</b> colour scheme for everything, so the tray surface and the
/// panel get the same answer — unlike Windows, where <c>SystemUsesLightTheme</c> and
/// <c>AppsUseLightTheme</c> are genuinely two settings. That is a fact about the platform,
/// said out loud here so a reader does not wonder which one was forgotten.</para>
///
/// <para><b>Unverified on a real desktop.</b> The portal call and its variant shape are
/// written from the specification; nothing here has run against a live session. Both
/// lookups fail closed to dark, which matches the tray icon's own default so the two
/// surfaces stay consistent when neither can be read.</para>
/// </summary>
public sealed class PortalThemeSource : IThemeSource
{
    private const string PortalService = "org.freedesktop.portal.Desktop";
    private const string PortalPath = "/org/freedesktop/portal/desktop";
    private const string SettingsInterface = "org.freedesktop.portal.Settings";

    /// <summary>Portal values for <c>color-scheme</c>: 0 no preference, 1 prefer dark, 2 prefer light.</summary>
    private const uint PreferDark = 1;
    private const uint PreferLight = 2;

    /// <summary>Same answer for both surfaces — Linux has one colour scheme, not two.</summary>
    public bool IsTrayLight() => IsLight();

    /// <inheritdoc cref="IsTrayLight"/>
    public bool IsPanelLight() => IsLight();

    private bool IsLight()
    {
        if (ReadPortalAsync().GetAwaiter().GetResult() is { } fromPortal)
        {
            return fromPortal;
        }

        return ReadGSettings() ?? false;   // dark when nothing can be read
    }

    private static async Task<bool?> ReadPortalAsync()
    {
        try
        {
            var connection = DBusConnection.Session;
            await connection.ConnectAsync();

            var writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(
                destination: PortalService,
                path: PortalPath,
                @interface: SettingsInterface,
                member: "Read",
                signature: "ss",
                flags: MessageFlags.None);
            writer.WriteString("org.freedesktop.appearance");
            writer.WriteString("color-scheme");

            var message = writer.CreateMessage();

            var scheme = await connection.CallMethodAsync(
                message,
                static (m, _) =>
                {
                    // Read returns a variant; the portal nests the value inside another,
                    // so unwrap until something numeric falls out rather than assuming a
                    // depth.
                    var value = m.GetBodyReader().ReadVariantValue();
                    while (value.Type == VariantValueType.Variant)
                    {
                        value = value.GetVariantValue();
                    }
                    return value.Type == VariantValueType.UInt32 ? value.GetUInt32() : uint.MaxValue;
                });

            return scheme switch
            {
                PreferDark => false,
                PreferLight => true,
                _ => null,      // "no preference" is not an answer; fall through
            };
        }
        catch (Exception ex) when (ex is DBusExceptionBase or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// GNOME-only fallback, for a desktop with no portal. Deliberately not the primary
    /// path: it reports nothing useful on Plasma.
    /// </summary>
    private static bool? ReadGSettings()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(
                "gsettings", "get org.gnome.desktop.interface color-scheme")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null || !process.WaitForExit(2000))
            {
                return null;
            }

            var value = process.StandardOutput.ReadToEnd();
            if (value.Contains("prefer-dark", StringComparison.Ordinal))
            {
                return false;
            }

            return value.Contains("prefer-light", StringComparison.Ordinal) ? true : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;   // gsettings not installed — normal on a non-GNOME desktop
        }
    }
}
