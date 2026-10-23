using OView.App.Rendering;

namespace OView.App.Platform;

/// <summary>
/// A remembered place: the work-area corner a dragged panel was released nearest to, and the
/// offset inward from it, in device-independent units.
/// </summary>
public readonly record struct PanelPlace(WorkAreaCorner Corner, double OffsetXDip, double OffsetYDip);

/// <summary>
/// Where <see cref="PanelPlacementMemory"/> keeps the place it remembers. A seam rather than a
/// direct dependency on a concrete store, so the placement rule's many edge cases, an
/// unplugged display, a resolution change, nothing stored, can be exercised against an
/// in-memory fake instead of real file I/O, the same way <see cref="IUiDispatcher"/> and the
/// other interfaces in this folder stand in for something a test should not have to do for
/// real.
///
/// <para>Both heads construct <see cref="FilePanelPositionStore"/>, which translates
/// <see cref="OView.Core.Storage.PanelPositionFile"/>'s bare-string corner to and from
/// <see cref="WorkAreaCorner"/>.</para>
/// </summary>
public interface IPanelPositionStore
{
    /// <summary>The remembered place for <paramref name="displayId"/>, or null when none is stored.</summary>
    PanelPlace? Read(string displayId);

    /// <summary>Remembers <paramref name="place"/> for <paramref name="displayId"/>.</summary>
    void Save(string displayId, PanelPlace place);

    /// <summary>
    /// Forgets <paramref name="displayId"/>'s place, so the next open goes back to the
    /// work-area corner: the menu's "Reset panel position" row. Forgetting a display that was
    /// never saved is not an error.
    /// </summary>
    void Forget(string displayId);
}
