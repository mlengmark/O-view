namespace OView.App.Platform;

/// <summary>
/// Whether O-view starts with the user's session.
///
/// <para>Each platform has one per-user mechanism that needs no elevation: the
/// <c>HKCU\...\CurrentVersion\Run</c> key on Windows (ADR-0003 item 6), an XDG autostart
/// <c>.desktop</c> file on Linux. In both cases <b>that mechanism is the single source of
/// truth</b> — the state is never duplicated into <c>settings.json</c>, so the two can
/// never disagree, and an external editor (Task Manager's startup page, or deleting the
/// desktop file) stays authoritative.</para>
/// </summary>
public interface IStartupRegistration
{
    bool IsEnabled();

    /// <summary>Registers the running executable. Returns whether it succeeded.</summary>
    bool Enable();

    /// <summary>Returns whether it succeeded. Removing something already absent is success.</summary>
    bool Disable();

    /// <summary>
    /// Applies the requested state and returns the state as it <b>actually stands
    /// afterwards</b>, not the state that was asked for.
    ///
    /// <para>A registry write or a file write can fail, and a settings tick that claimed
    /// otherwise would be a fabricated fact about the user's machine (CLAUDE.md rule 6).
    /// Shared here so both heads cannot get it subtly different.</para>
    /// </summary>
    bool Apply(bool enable)
    {
        var ok = enable ? Enable() : Disable();
        return ok ? enable : IsEnabled();
    }
}
