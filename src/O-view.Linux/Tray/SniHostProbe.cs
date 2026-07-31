using Tmds.DBus.Protocol;

namespace OView.Linux.Tray;

/// <summary>What the session bus says about a notification-area host.</summary>
public enum TrayHostState
{
    /// <summary>A StatusNotifierWatcher owns its name — something is there to draw the icon.</summary>
    Present,

    /// <summary>No watcher on the bus. Stock GNOME, with no AppIndicator extension installed.</summary>
    Absent,

    /// <summary>The bus could not be asked. Distinct from Absent — never claim more than was observed.</summary>
    Unknown,
}

/// <summary>
/// Asks the session bus whether anything is there to display a tray icon.
///
/// <para><b>Why this exists at all.</b> The spike measured that an Avalonia
/// <c>TrayIcon</c> reports <c>IsVisible = true</c> whether or not a host exists — the app's
/// own output is identical either way. Stock Ubuntu ships GNOME, which provides no host, so
/// trusting the toolkit means being silently invisible on the most likely Linux
/// configuration. That is exactly what CLAUDE.md rule 6 forbids, and ADR-0013 decision 2
/// makes this probe mandatory rather than optional.
/// (<see href="../../docs/findings/linux-tray-spike.md">findings</see>)</para>
///
/// <para><b>Never call this synchronously from the UI thread.</b> The spike found that
/// blocking the dispatcher on a D-Bus round trip deadlocks the app outright: it printed its
/// startup lines and hung until CI killed it. Probe before the toolkit starts, or await it
/// properly.</para>
/// </summary>
public sealed class SniHostProbe
{
    /// <summary>The well-known name a notification-area host owns.</summary>
    public const string WatcherService = "org.kde.StatusNotifierWatcher";

    /// <summary>
    /// Whether a host is on the session bus right now.
    ///
    /// <para>Returns <see cref="TrayHostState.Unknown"/> rather than Absent when the bus
    /// itself cannot be reached — "I could not ask" and "I asked and nothing was there" are
    /// different facts, and only one of them justifies telling the user to install an
    /// extension.</para>
    /// </summary>
    public static async Task<TrayHostState> CheckAsync()
    {
        try
        {
            var connection = DBusConnection.Session;
            await connection.ConnectAsync();

            var services = await connection.ListServicesAsync();
            return services.Contains(WatcherService, StringComparer.Ordinal)
                ? TrayHostState.Present
                : TrayHostState.Absent;
        }
        catch (Exception ex) when (ex is DBusExceptionBase or IOException or InvalidOperationException)
        {
            return TrayHostState.Unknown;
        }
    }

    /// <summary>
    /// Completes when a notification-area host owns the name — immediately if one already
    /// does. The Linux equivalent of Windows' <c>TaskbarCreated</c> re-registration
    /// (ADR-0003 item 5).
    ///
    /// <para>This matters more here than on Windows: a GNOME user can install the
    /// AppIndicator extension <i>while O-view is running</i>, and an app that only looked
    /// once would stay invisible until restarted, with no hint that restarting would help.</para>
    ///
    /// <para>Returns false if the bus could not be watched. The icon still works in that
    /// case; it simply will not recover on its own if a host appears later, which is worth
    /// degrading over rather than refusing to start.</para>
    /// </summary>
    public static async Task<bool> WaitForHostAsync(CancellationToken cancellation = default)
    {
        try
        {
            var connection = DBusConnection.Session;
            await connection.ConnectAsync();

            using var watcher = await connection.WatchNameOwnerAsync(WatcherService);

            if (!string.IsNullOrEmpty(watcher.GetCurrentOwner()))
            {
                return true;
            }

            var owner = await watcher.WaitForOwnerAsync(cancellation);
            return !string.IsNullOrEmpty(owner);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex) when (ex is DBusExceptionBase or IOException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// What to tell the user, stating what was observed rather than asserting anything
    /// about their machine (CLAUDE.md rule 6). "No host on the bus" is a fact; "GNOME is
    /// missing an extension" is a guess about why.
    /// </summary>
    public static string Explain(TrayHostState state) => state switch
    {
        TrayHostState.Present => "A notification-area host is running.",
        TrayHostState.Absent =>
            "O-view found no notification-area host on the session bus, so its icon has "
            + "nowhere to appear. GNOME does not provide one by default — installing an "
            + "AppIndicator/KStatusNotifierItem extension adds it, and O-view will pick it "
            + "up without needing a restart.",
        _ => "O-view could not reach the session bus to check for a notification-area host.",
    };
}
