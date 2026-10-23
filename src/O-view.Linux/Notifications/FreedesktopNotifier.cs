using OView.Presentation.Panel;
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
    /// Shows a notification and returns the id the desktop gave it, or null if it was not
    /// shown. Never throws — a desktop with no notification daemon is a working desktop, and
    /// failing to tell the user something must not take the app down.
    ///
    /// <para>The id is kept rather than dropped because of one notice that can be overtaken
    /// by events: O-view saying it has nowhere to show its icon, on a desktop whose
    /// notification area then turns up. <see cref="CloseAsync"/> is what takes it back off
    /// the screen, and the id is the only handle the spec offers for that.</para>
    /// </summary>
    public async Task<uint?> ShowAsync(string title, string body, CancellationToken cancellation = default)
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

            writer.WriteString(PanelHeaderView.AppName);        // app_name
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

            // Notify answers with the id it filed the notification under.
            return await connection.CallMethodAsync(
                message,
                static (m, _) => m.GetBodyReader().ReadUInt32());
        }
        catch (Exception ex) when (ex is DBusExceptionBase or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Takes a notification back off the screen, by the id <see cref="ShowAsync"/> returned.
    /// Returns whether the desktop accepted the call.
    ///
    /// <para>Never throws, for the same reason: a notice that cannot be withdrawn is a
    /// cosmetic problem, not a reason to end the app. An id the desktop has already forgotten
    /// — the usual case, since these expire on their own — is answered with an error by the
    /// spec rather than ignored, and that lands here as false. The notification is gone
    /// either way, which is the thing wanted.</para>
    /// </summary>
    public async Task<bool> CloseAsync(uint id, CancellationToken cancellation = default)
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
                member: "CloseNotification",
                signature: "u",
                flags: MessageFlags.None);

            writer.WriteUInt32(id);

            await connection.CallMethodAsync(writer.CreateMessage());
            return true;
        }
        catch (Exception ex) when (ex is DBusExceptionBase or IOException or InvalidOperationException)
        {
            return false;
        }
    }
}
