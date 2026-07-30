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
/// <para>Read on every use rather than cached: a theme change must never need a restart,
/// and the reads are cheap at a 60 s cadence.</para>
/// </summary>
public interface IThemeSource
{
    /// <summary>The surface the tray icon sits on.</summary>
    bool IsTrayLight();

    /// <summary>The app's own windows — the panel, menu and dialogs.</summary>
    bool IsPanelLight();
}
