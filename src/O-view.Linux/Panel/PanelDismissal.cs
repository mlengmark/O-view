namespace OView.Linux.Panel;

/// <summary>
/// When a deactivation should actually dismiss the panel.
///
/// <para><b>The rule: a panel that has never been focused must not dismiss itself.</b>
/// "Click away to close" means the user moved focus somewhere else, which presupposes they
/// had it here first. A deactivation arriving before the panel was ever activated is not the
/// user clicking away — it is the window manager declining to give the window focus at
/// all.</para>
///
/// <para><b>Why this is not hypothetical.</b> The Linux panel is opened from a tray click and
/// then calls <c>Activate()</c>. Compositors are entitled to refuse that: KDE's
/// focus-stealing prevention is the specific suspect named in the v0.6.4 release notes on
/// issue #84, and under XWayland it applies to exactly this shape of request — a window
/// raised by a process that does not own the foreground. The old handler was an unguarded
/// <c>Deactivated += (_, _) =&gt; Hide();</c>, so a refused activation dismissed the panel in
/// the same frame it appeared: one flash, then nothing. The user sees a tray icon that does
/// not work, and the log says <c>panel opened</c>, because it did.</para>
///
/// <para><b>Why the failure is silent.</b> That log line is the reason this is worth guarding
/// pre-emptively rather than waiting for the hardware report. <c>panel opened</c> is written
/// after <c>ShowWith</c> returns and cannot distinguish "shown and stayed" from "shown and
/// hidden 16 ms later" — so the report that comes back reads identically to the #124
/// deadlock that was just fixed. <see cref="SuppressedDeactivations"/> exists to separate
/// them in one round trip instead of two.</para>
///
/// <para><b>What this trades away, deliberately.</b> If a compositor never activates the
/// window at all, the panel stops closing on click-away and Esc becomes the only dismissal.
/// That is the better failure: a panel that stays until dismissed is usable and obviously
/// imperfect, where a panel that vanishes before it can be read is indistinguishable from a
/// broken app — the same reasoning ADR-0013 applied to docking, and <c>ForegroundWindow</c>
/// records the mirror-image bug on the Windows side.</para>
///
/// <para>Kept separate from <see cref="PanelWindow"/> because it is the only part of the
/// dismissal path that can be tested without a desktop: Avalonia will not raise
/// <c>Activated</c> or <c>Deactivated</c> without a windowing subsystem, and CI has none.
/// The Windows head reached the same conclusion for the same reason — see
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
