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
/// panel get the same answer, unlike Windows, where <c>SystemUsesLightTheme</c> and
/// <c>AppsUseLightTheme</c> are genuinely two settings. That is a fact about the platform,
/// said out loud here so a reader does not wonder which one was forgotten.</para>
///
/// <para><b>Reads never block the caller, and this is the whole design.</b> The seam is
/// called from the left-click handler, which arrives on Avalonia's UI thread, and a
/// synchronous D-Bus round trip there does not merely stall, it deadlocks the process
/// outright: the reply continuation is posted back to the very thread that is blocked
/// waiting for it. That was measured during the spike
/// (<see href="../../docs/findings/linux-tray-spike.md">findings</see>, item 5), stated in
/// <see cref="Tray.SniHostProbe"/>'s doc, restated in CLAUDE.md rule 5, and then shipped
/// here anyway in v0.6.1, where it froze the app on the first click on real hardware
/// (issue #124). So the bus is asked <b>only</b> off the calling thread, and callers read a
/// value that is already known.</para>
///
/// <para><b>The cache is static on purpose.</b> The colour scheme is one machine-wide
/// setting, not a property of an instance, and one shared value is what lets
/// <see cref="PrimeAsync"/> fill it from <c>Program.Main</c>, before the toolkit exists
/// and therefore at the only moment in the process's life when waiting for D-Bus is safe.
/// Every read after that is a field load.</para>
///
/// <para><b>Staying current without a restart.</b> The portal announces every change with
/// its <c>SettingChanged</c> signal, and the cache is updated when that signal arrives, on
/// the bus's own thread, so the first panel open after a switch between light and dark
/// already shows the new theme. Reads still never ask the bus. Polling was the earlier
/// answer and left every switch one open late (issue #357): each read started a refresh
/// that only the <i>next</i> read could see. It survives only where no signal can come,
/// a desktop with no portal or a bus that refused the subscription, and there a switch
/// still lands one read late, because the alternative costs the dispatcher.</para>
///
/// <para><b>Unverified on a real desktop.</b> The portal call, its signal and their variant
/// shapes are written from the specification. Both lookups fail closed to dark, which matches the tray
/// icon's own default so the two surfaces stay consistent when neither can be read.</para>
/// </summary>
public sealed class PortalThemeSource : IThemeSource
{
    private const string PortalService = "org.freedesktop.portal.Desktop";
    private const string PortalPath = "/org/freedesktop/portal/desktop";
    private const string SettingsInterface = "org.freedesktop.portal.Settings";
    private const string AppearanceNamespace = "org.freedesktop.appearance";
    private const string ColorSchemeKey = "color-scheme";

    /// <summary>Portal values for <c>color-scheme</c>: 0 no preference, 1 prefer dark, 2 prefer light.</summary>
    private const uint PreferDark = 1;
    private const uint PreferLight = 2;

    /// <summary>
    /// How long any one lookup may take before the desktop is treated as having no answer.
    ///
    /// <para>Necessary even off the UI thread. A portal service that is on the bus but
    /// cannot be activated leaves <c>CallMethodAsync</c> waiting for D-Bus's own 25 s reply
    /// timeout, and <see cref="PrimeAsync"/> is awaited during startup, so without a bound
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

    /// <summary>
    /// True while the <c>SettingChanged</c> subscription is live. Not enough on its own to
    /// stop polling: the bus accepts the subscription whether or not a portal exists to
    /// raise the signal.
    /// </summary>
    private static volatile bool _subscribed;

    /// <summary>
    /// True once changes are known to arrive as signals: the subscription is live and the
    /// portal has answered, or a signal has already come. Reads then stop polling, because a
    /// poll can only repeat what the signal said or, finishing late, contradict it.
    /// </summary>
    private static volatile bool _followingSignals;

    /// <summary>
    /// How many signals have been applied. A lookup notes it before asking and stores its
    /// answer only if it is unchanged, so a slow read overtaken by a signal cannot put the
    /// old theme back.
    /// </summary>
    private static int _signals;

    private static readonly Lock StoreGate = new();

    /// <summary>Same answer for both surfaces, Linux has one colour scheme, not two.</summary>
    public bool IsTrayLight() => Current();

    /// <inheritdoc cref="IsTrayLight"/>
    public bool IsPanelLight() => Current();

    /// <summary>
    /// Fills the cache before the first read and starts listening for changes, and is the
    /// one call here that waits.
    ///
    /// <para>Call it from <c>Program.Main</c> before the toolkit starts, alongside
    /// <see cref="Tray.SniHostProbe.CheckAsync"/> and for the same reason: there is no
    /// dispatcher yet, so there is nothing to deadlock. Awaiting it there is what makes the
    /// <i>first</i> panel open correct rather than dark-by-default.</para>
    ///
    /// <para>It subscribes before it reads, so a switch made while the read is in flight
    /// still arrives. The subscription is answered by the bus itself, not the portal, so a
    /// portal that cannot be activated still costs one timeout here, not two.</para>
    /// </summary>
    public static async Task PrimeAsync()
    {
        await SubscribeAsync().ConfigureAwait(false);
        await RefreshAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// What one <c>SettingChanged</c> signal does to the cache. On a desktop only the bus
    /// calls it, on the bus's own thread; it is public so the tests can raise one with no
    /// portal to send it.
    ///
    /// <para>A dark or light preference is stored as it stands, so the next read returns it
    /// and nothing is asked. "No preference" leaves the answer to the fallback, so it is
    /// resolved as a read would be, off the bus's thread, since gsettings is a process to
    /// wait for.</para>
    /// </summary>
    public static void OnSettingChanged(string @namespace, string key, uint scheme)
    {
        if (@namespace != AppearanceNamespace || key != ColorSchemeKey)
        {
            return;
        }

        int signal;
        lock (StoreGate)
        {
            signal = ++_signals;
            _followingSignals = true;
            if (scheme is PreferDark or PreferLight)
            {
                _isLight = scheme == PreferLight;
                return;
            }
        }

        _ = Task.Run(() => Store(Resolve(scheme), signal));
    }

    /// <summary>
    /// The cached answer. Never touches the bus on the calling thread; while no signal can
    /// be relied on, it also starts a background refresh so the next caller gets a current
    /// one.
    /// </summary>
    private static bool Current()
    {
        if (!_followingSignals)
        {
            BeginRefresh();
        }

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
                // Retried here and not only at startup, so a session bus that was not up
                // when O-view started still delivers signals once it is.
                if (!_subscribed)
                {
                    await SubscribeAsync().ConfigureAwait(false);
                }

                await RefreshAsync().ConfigureAwait(false);
            }
            finally
            {
                Volatile.Write(ref _refreshing, 0);
            }
        });
    }

    /// <summary>Asks the desktop and stores the answer, unless a signal overtook the question.</summary>
    private static async Task RefreshAsync()
    {
        var signalsBefore = Volatile.Read(ref _signals);
        var scheme = await ReadPortalAsync().ConfigureAwait(false);

        if (scheme is not null && _subscribed)
        {
            _followingSignals = true;   // a portal that answers Read is one that raises the signal
        }

        Store(Resolve(scheme), signalsBefore);
    }

    private static void Store(bool isLight, int signalsBefore)
    {
        lock (StoreGate)
        {
            if (_signals == signalsBefore)
            {
                _isLight = isLight;
            }
        }
    }

    /// <summary>The portal's preference, then gsettings, then dark. Never throws.</summary>
    private static bool Resolve(uint? scheme) => scheme switch
    {
        PreferDark => false,
        PreferLight => true,
        _ => ReadGSettings() ?? false,   // "no preference" is not an answer; dark when nothing can be read
    };

    /// <summary>
    /// Asks the bus for the portal's <c>SettingChanged</c> signal. Never throws: without the
    /// subscription, reads simply keep polling.
    /// </summary>
    private static async Task SubscribeAsync()
    {
        try
        {
            var connection = DBusConnection.Session;
            await connection.ConnectAsync().ConfigureAwait(false);

            var rule = new MatchRule
            {
                Type = MessageType.Signal,
                Sender = PortalService,
                Path = PortalPath,
                Interface = SettingsInterface,
                Member = "SettingChanged",
                Arg0 = AppearanceNamespace,
            };

            await connection.AddMatchAsync(
                    rule,
                    static (m, _) =>
                    {
                        var body = m.GetBodyReader();
                        return (Namespace: body.ReadString(), Key: body.ReadString(), Scheme: SchemeOf(body.ReadVariantValue()));
                    },
                    static notification =>
                    {
                        if (notification.HasValue)
                        {
                            var change = notification.Value;
                            OnSettingChanged(change.Namespace, change.Key, change.Scheme);
                            return;
                        }

                        // The connection is gone and no more signals will come, so reads go
                        // back to polling.
                        _subscribed = false;
                        _followingSignals = false;
                    },
                    emitOnCapturedContext: false,
                    flags: ObserverFlags.EmitOnConnectionClosed)
                .AsTask()
                .WaitAsync(LookupTimeout)
                .ConfigureAwait(false);

            _subscribed = true;
        }
        catch (Exception ex) when (IsBusFailure(ex))
        {
        }
    }

    /// <summary>The portal's <c>color-scheme</c> number, or null when the portal gave none.</summary>
    private static async Task<uint?> ReadPortalAsync()
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
            writer.WriteString(AppearanceNamespace);
            writer.WriteString(ColorSchemeKey);

            var message = writer.CreateMessage();

            return await connection.CallMethodAsync(
                    message,
                    static (m, _) => SchemeOf(m.GetBodyReader().ReadVariantValue()))
                .WaitAsync(LookupTimeout)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (IsBusFailure(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// The number inside a portal variant. <c>Read</c> nests the value inside another
    /// variant and the signal does not, so unwrap until something numeric falls out rather
    /// than assuming a depth.
    /// </summary>
    private static uint SchemeOf(VariantValue value)
    {
        while (value.Type == VariantValueType.Variant)
        {
            value = value.GetVariantValue();
        }

        return value.Type == VariantValueType.UInt32 ? value.GetUInt32() : uint.MaxValue;
    }

    private static bool IsBusFailure(Exception ex) =>
        ex is DBusExceptionBase or IOException or InvalidOperationException
            or TimeoutException or OperationCanceledException;

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
            return null;   // gsettings not installed, normal on a non-GNOME desktop
        }
    }
}
