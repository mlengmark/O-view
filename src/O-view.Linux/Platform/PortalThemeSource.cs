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
/// <para><b>Reads never block the caller, and this is the whole design.</b> The seam is
/// called from the left-click handler, which arrives on Avalonia's UI thread — and a
/// synchronous D-Bus round trip there does not merely stall, it deadlocks the process
/// outright: the reply continuation is posted back to the very thread that is blocked
/// waiting for it. That was measured during the spike
/// (<see href="../../docs/findings/linux-tray-spike.md">findings</see>, item 5), stated in
/// <see cref="Tray.SniHostProbe"/>'s doc, restated in CLAUDE.md rule 5 — and then shipped
/// here anyway in v0.6.1, where it froze the app on the first click on real hardware
/// (issue #124). So the bus is asked <b>only</b> off the calling thread, and callers read a
/// value that is already known.</para>
///
/// <para><b>The cache is static on purpose.</b> The colour scheme is one machine-wide
/// setting, not a property of an instance, and one shared value is what lets
/// <see cref="PrimeAsync"/> fill it from <c>Program.Main</c> — before the toolkit exists
/// and therefore at the only moment in the process's life when waiting for D-Bus is safe.
/// Every read after that is a field load.</para>
///
/// <para><b>Staying current without a restart.</b> Each read starts a background refresh
/// for the <i>next</i> one, so a desktop switched between light and dark is picked up
/// within a poll rather than needing O-view restarted. What it costs is that the switch
/// lands one read late; the alternative costs the dispatcher.</para>
///
/// <para><b>Unverified on a real desktop.</b> The portal call and its variant shape are
/// written from the specification. Both lookups fail closed to dark, which matches the tray
/// icon's own default so the two surfaces stay consistent when neither can be read.</para>
/// </summary>
public sealed class PortalThemeSource : IThemeSource
{
    private const string PortalService = "org.freedesktop.portal.Desktop";
    private const string PortalPath = "/org/freedesktop/portal/desktop";
    private const string SettingsInterface = "org.freedesktop.portal.Settings";

    /// <summary>Portal values for <c>color-scheme</c>: 0 no preference, 1 prefer dark, 2 prefer light.</summary>
    private const uint PreferDark = 1;
    private const uint PreferLight = 2;

    /// <summary>
    /// How long any one lookup may take before the desktop is treated as having no answer.
    ///
    /// <para>Necessary even off the UI thread. A portal service that is on the bus but
    /// cannot be activated leaves <c>CallMethodAsync</c> waiting for D-Bus's own 25 s reply
    /// timeout, and <see cref="PrimeAsync"/> is awaited during startup — so without a bound
    /// here, a broken portal delays the icon appearing by almost half a minute.</para>
    /// </summary>
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The last answer the desktop gave. Dark until something says otherwise, matching the
    /// tray icon's default so the two surfaces agree before the first read lands.
    /// </summary>
    private static volatile bool _isLight;

    /// <summary>
    /// 1 while a background refresh is in flight. Reads happen on a timer and on every
    /// panel open, and without this each one would queue another bus call behind the last.
    /// </summary>
    private static int _refreshing;

    /// <summary>Same answer for both surfaces — Linux has one colour scheme, not two.</summary>
    public bool IsTrayLight() => Current();

    /// <inheritdoc cref="IsTrayLight"/>
    public bool IsPanelLight() => Current();

    /// <summary>
    /// Fills the cache before the first read, and is the one call here that waits.
    ///
    /// <para>Call it from <c>Program.Main</c> before the toolkit starts, alongside
    /// <see cref="Tray.SniHostProbe.CheckAsync"/> and for the same reason: there is no
    /// dispatcher yet, so there is nothing to deadlock. Awaiting it there is what makes the
    /// <i>first</i> panel open correct rather than dark-by-default.</para>
    /// </summary>
    public static async Task PrimeAsync() => _isLight = await ReadAsync().ConfigureAwait(false);

    /// <summary>
    /// The cached answer, plus a background refresh so the next caller gets a current one.
    /// Never touches the bus on the calling thread.
    /// </summary>
    private static bool Current()
    {
        BeginRefresh();
        return _isLight;
    }

    private static void BeginRefresh()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            return;   // one already running; a second would only queue behind it
        }

        _ = Task.Run(async () =>
        {
            try
            {
                _isLight = await ReadAsync().ConfigureAwait(false);
            }
            finally
            {
                Volatile.Write(ref _refreshing, 0);
            }
        });
    }

    /// <summary>Portal, then gsettings, then dark. Never throws.</summary>
    private static async Task<bool> ReadAsync()
    {
        if (await ReadPortalAsync().ConfigureAwait(false) is { } fromPortal)
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
            await connection.ConnectAsync().ConfigureAwait(false);

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
                    })
                .WaitAsync(LookupTimeout)
                .ConfigureAwait(false);

            return scheme switch
            {
                PreferDark => false,
                PreferLight => true,
                _ => null,      // "no preference" is not an answer; fall through
            };
        }
        catch (Exception ex) when (ex is DBusExceptionBase or IOException or InvalidOperationException
                                       or TimeoutException or OperationCanceledException)
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

            if (process is null || !process.WaitForExit((int)LookupTimeout.TotalMilliseconds))
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
