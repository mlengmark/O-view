using Tmds.DBus.Protocol;

namespace OView.Linux.Notifications;

/// <summary>
/// Desktop notifications over <c>org.freedesktop.Notifications</c> — the Linux counterpart
/// of the Windows balloon tip.
///
/// <para><b>Not <c>notify-send</c>.</b> That is a separate package which is not installed
/// everywhere, and shelling out to a binary that may be missing is a silent failure. The
/// D-Bus interface is part of the desktop itself.</para>
///
/// <para><b>Notifications are a different service from the tray.</b> This matters: GNOME
/// ships no StatusNotifierItem host, but it does implement notifications — so on the one
/// configuration where O-view's icon cannot appear, this is still a working channel for
/// telling the user why (ADR-0013 decision 2).</para>
/// </summary>
public sealed class FreedesktopNotifier
{
    private const string Service = "org.freedesktop.Notifications";
    private const string ObjectPath = "/org/freedesktop/Notifications";

    /// <summary>Icon name from the installed hicolor theme; falls back to nothing if absent.</summary>
    private const string IconName = "o-view";

    /// <summary>
    /// Shows a notification. Never throws — a desktop with no notification daemon is a
    /// working desktop, and failing to tell the user something must not take the app down.
    /// </summary>
    public async Task<bool> ShowAsync(string title, string body, CancellationToken cancellation = default)
    {
        try
        {
            var connection = DBusConnection.Session;
            await connection.ConnectAsync();

            var writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(
                destination: Service,
                path: ObjectPath,
                @interface: Service,
                member: "Notify",
                signature: "susssasa{sv}i",
                flags: MessageFlags.None);

            writer.WriteString("O-view");        // app_name
            writer.WriteUInt32(0);               // replaces_id — 0 means "a new one"
            writer.WriteString(IconName);        // app_icon
            writer.WriteString(title);           // summary
            writer.WriteString(body);            // body
            writer.WriteArray(Array.Empty<string>());   // actions — none; O-view is not interactive here

            // hints: empty. Deliberately no urgency hint — these are informational, and
            // marking usage warnings "critical" would make them persist on screen.
            var hints = writer.WriteDictionaryStart();
            writer.WriteDictionaryEnd(hints);

            writer.WriteInt32(-1);               // expire_timeout — let the desktop decide

            var message = writer.CreateMessage();
            await connection.CallMethodAsync(message);
            return true;
        }
        catch (Exception ex) when (ex is DBusExceptionBase or IOException or InvalidOperationException)
        {
            return false;
        }
    }
}
