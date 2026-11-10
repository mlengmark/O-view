namespace OView.Linux.Panel;

/// <summary>
/// When a deactivation should actually dismiss the panel.
///
/// <para><b>The rule: a panel that has never been focused must not dismiss itself.</b>
/// "Click away to close" means the user moved focus somewhere else, which presupposes they
/// had it here first. A deactivation arriving before the panel was ever activated is not the
/// user clicking away, it is the window manager declining to give the window focus at
/// all.</para>
///
/// <para><b>Why this is not hypothetical.</b> The Linux panel is opened from a tray click and
/// then calls <c>Activate()</c>. Compositors are entitled to refuse that: KDE's
/// focus-stealing prevention is the specific suspect named in the v0.6.4 release notes on
/// issue #84, and under XWayland it applies to exactly this shape of request, a window
/// raised by a process that does not own the foreground. The old handler was an unguarded
/// <c>Deactivated += (_, _) =&gt; Hide();</c>, so a refused activation dismissed the panel in
/// the same frame it appeared: one flash, then nothing. The user sees a tray icon that does
/// not work, and the log says <c>panel opened</c>, because it did.</para>
///
/// <para><b>Why the failure is silent.</b> That log line is the reason this is worth guarding
/// pre-emptively rather than waiting for the hardware report. <c>panel opened</c> is written
/// after <c>ShowWith</c> returns and cannot distinguish "shown and stayed" from "shown and
/// hidden 16 ms later", so the report that comes back reads identically to the #124
/// deadlock that was just fixed. <see cref="SuppressedDeactivations"/> exists to separate
/// them in one round trip instead of two.</para>
///
/// <para><b>What this trades away, deliberately.</b> If a compositor never activates the
/// window at all, the panel stops closing on click-away and Esc becomes the only dismissal.
/// That is the better failure: a panel that stays until dismissed is usable and obviously
/// imperfect, where a panel that vanishes before it can be read is indistinguishable from a
/// broken app, the same reasoning ADR-0013 applied to docking, and <c>ForegroundWindow</c>
/// records the mirror-image bug on the Windows side.</para>
///
/// <para>Kept separate from <see cref="PanelWindow"/> because it is the only part of the
/// dismissal path that can be tested without a desktop: Avalonia will not raise
/// <c>Activated</c> or <c>Deactivated</c> without a windowing subsystem, and CI has none.
/// The Windows head reached the same conclusion for the same reason, see
/// <c>DockedFlyout</c>, which is this idea with the docking machinery attached.</para>
/// </summary>
public sealed class PanelDismissal
{
    private bool _activated;

    /// <summary>
    /// How many deactivations were ignored because the panel had never been focused.
    ///
    /// <para>Non-zero means the compositor refused the activation. Surfaced through the
    /// <c>--log</c> file so a bug report can say which failure happened rather than only that
    /// the panel did not appear.</para>
    /// </summary>
    public int SuppressedDeactivations { get; private set; }

    /// <summary>Call as the panel is shown, before any activation can arrive.</summary>
    public void Opening()
    {
        _activated = false;
        SuppressedDeactivations = 0;
    }

    /// <summary>Call from the window's <c>Activated</c>.</summary>
    public void Activated() => _activated = true;

    /// <summary>
    /// Call from the window's <c>Deactivated</c>. Returns whether to hide.
    /// </summary>
    public bool ShouldHideOnDeactivated()
    {
        if (_activated)
        {
            return true;
        }

        SuppressedDeactivations++;
        return false;
    }
}

/// <summary>What a tray click does to the panel.</summary>
public enum TrayClickOutcome
{
    /// <summary>Nothing was open: the panel opens.</summary>
    Open,

    /// <summary>The panel was on screen: the click puts it away.</summary>
    Close,

    /// <summary>
    /// The click's own press had already taken the focus and with it the panel, so the close half
    /// of the toggle has happened and the click does nothing more.
    /// </summary>
    AlreadyClosed,
}

/// <summary>
/// Clicking the tray icon is a toggle, as on Windows: a click while the panel is open puts it away.
///
/// <para><b>Why the panel's visibility is not enough.</b> Pressing the icon can take the focus away
/// from the panel before the click reaches this app, and a panel that loses the focus hides itself.
/// By the time the click arrives the panel is already gone, and reading "not visible" as "open it"
/// made the icon a switch that could only ever open: the panel flashed away and straight back. So
/// a click arriving within <see cref="Grace"/> of a close the focus loss caused is taken as the
/// close half of the toggle, the same rule and the same 400 ms the Windows panel uses.</para>
///
/// <para><b>Either order ends closed.</b> Whether the desktop delivers the focus loss first or the
/// click first, the panel ends hidden: focus first, the click finds it just closed; click first, it
/// finds it on screen and closes it, and the focus loss after that finds nothing open to count.</para>
///
/// <para>One per app rather than per window, because <c>LinuxApp</c> builds a fresh panel at every
/// open, and the close it has to remember belongs to the panel before.</para>
/// </summary>
public sealed class TrayToggle
{
    /// <summary>
    /// How long after a focus-loss close a tray click still counts as the close half of the toggle.
    /// Long enough to cover the press-then-click ordering on a busy desktop, short enough that a
    /// deliberate second click a moment later opens the panel again. The figure the Windows panel
    /// uses, kept equal so the icon feels the same on both.
    /// </summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(400);

    private bool _open;
    private DateTimeOffset? _focusLossClose;

    /// <summary>Call when a click or the menu has opened the panel.</summary>
    public void Opened()
    {
        _open = true;
        _focusLossClose = null;
    }

    /// <summary>
    /// Call from the panel's <c>Deactivated</c>, after the panel's own handler has run, with whether
    /// the panel is still on screen. Only a deactivation that hid an open panel is remembered: one
    /// the panel ignored, or one arriving after a click already closed it, is not a click away.
    /// </summary>
    public void Deactivated(bool stillVisible, DateTimeOffset at)
    {
        if (!_open || stillVisible)
        {
            return;
        }

        _open = false;
        _focusLossClose = at;
    }

    /// <summary>
    /// What a tray click arriving at <paramref name="at"/> does, given whether the panel is on screen.
    /// A click spent as the close half is spent once: the next click opens.
    /// </summary>
    public TrayClickOutcome Clicked(bool panelVisible, DateTimeOffset at)
    {
        if (panelVisible)
        {
            _open = false;
            _focusLossClose = null;
            return TrayClickOutcome.Close;
        }

        if (_focusLossClose is { } closed && at - closed < Grace)
        {
            _focusLossClose = null;
            return TrayClickOutcome.AlreadyClosed;
        }

        return TrayClickOutcome.Open;
    }
}
