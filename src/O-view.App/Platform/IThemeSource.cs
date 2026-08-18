namespace OView.App.Platform;

/// <summary>
/// Light/dark detection for the two surfaces O-view draws on.
///
/// <para><b>They are genuinely two settings, not one read twice.</b> On Windows the
/// notification area follows <c>SystemUsesLightTheme</c> while app windows follow
/// <c>AppsUseLightTheme</c>, and a user can set them independently — a light taskbar with
/// dark apps is a normal configuration. Collapsing them into a single "is dark" would
/// render the tray icon against the wrong background for those users, which is precisely
/// the contrast problem ADR-0003 item 4 exists to avoid.</para>
///
/// <para>Linux desktops generally expose one <c>color-scheme</c> for everything, so a
/// Linux implementation is expected to return the same value from both — that is a fact
/// about the platform, and it should say so in its own doc comment rather than leaving a
/// reader wondering whether it forgot one.</para>
///
/// <para><b>Two obligations, and neither prescribes how.</b> A theme change must never
/// need a restart, and neither method may block the calling thread — both are called from
/// the head's UI thread, on every tray render and on every panel open.</para>
///
/// <para>This used to read "read on every use rather than cached: the reads are cheap at a
/// 60 s cadence", which was true of the registry and false of a D-Bus round trip. Taken at
/// its word on Linux it produced a synchronous portal call on Avalonia's dispatcher, whose
/// own reply continuation was posted back to that blocked thread — the app froze on the
/// first left click, on the first machine that ever ran it (issue #124). An interface says
/// what implementations must guarantee; how they meet it is theirs to decide.</para>
/// </summary>
public interface IThemeSource
{
    /// <summary>The surface the tray icon sits on.</summary>
    bool IsTrayLight();

    /// <summary>The app's own windows — the panel, menu and dialogs.</summary>
    bool IsPanelLight();
}
