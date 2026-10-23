namespace OView.App.Rendering;

/// <summary>
/// How tall the flyout window may actually be, and whether its content has to scroll to fit —
/// the one shared answer both heads ask the same question for.
///
/// <para><b>Why this exists.</b> Each head used to size its window from the content's own
/// height and stop there, leaving <see cref="WorkAreaPlacement.Place"/> to pin a too-tall
/// result to the top-left of the work area the window manager already capped. That keeps the
/// header on screen but leaves the rest unreachable — on a 1024×768 desktop the Linux panel ran
/// off the bottom of the screen with nothing to scroll it back into view (seen on the Ubuntu
/// 24.04 VM run of 2026-10-15).</para>
///
/// <para><see cref="PanelDensity.Compact"/> is the first answer, and on its own is not always
/// enough: it buys back about 94 DIP, while both panels measure more than 150 DIP too tall
/// against a 768 px screen's work area. <see cref="Resolve"/> is the second and last resort —
/// once a head has already chosen its density and measured what the content wants at that
/// density, this caps the window at what the work area allows and says whether the content has
/// to scroll inside it instead of running off the edge.</para>
///
/// <para>Never answers with a height larger than <c>availableHeightDip</c> — a scroll viewport
/// the window manager still has to clip is the same unreachable-content bug with extra code in
/// front of it.</para>
/// </summary>
public static class PanelFit
{
    /// <summary>
    /// <paramref name="WindowHeightDip"/> is what the flyout window should be made, capped at
    /// the work area. <paramref name="MustScroll"/> is true exactly when the content is taller
    /// than that — the caller then has to lay the content out inside a scrollable viewport
    /// rather than directly, so the part past the cap is still reachable rather than clipped.
    /// </summary>
    public readonly record struct Result(double WindowHeightDip, bool MustScroll);

    /// <summary>
    /// <paramref name="contentHeightDip"/> is the content's own desired height, measured by
    /// whichever head is asking, already at whatever <see cref="PanelDensity"/> it chose.
    /// <paramref name="availableHeightDip"/> is what <see cref="WorkAreaPlacement.AvailableHeightPx"/>
    /// leaves once both flyout margins are taken, converted to DIP by the caller.
    /// </summary>
    public static Result Resolve(double contentHeightDip, double availableHeightDip) =>
        contentHeightDip <= availableHeightDip
            ? new Result(contentHeightDip, MustScroll: false)
            : new Result(availableHeightDip, MustScroll: true);
}
