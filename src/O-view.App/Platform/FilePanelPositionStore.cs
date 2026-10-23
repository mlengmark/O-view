using OView.App.Rendering;
using OView.Core.Storage;

namespace OView.App.Platform;

/// <summary>
/// The file-backed <see cref="IPanelPositionStore"/> both heads hand the shared placement rule:
/// <see cref="PanelPositionFile"/> plus the one translation Core cannot do for itself, between
/// its bare-string corner and <see cref="WorkAreaCorner"/>. One class in App rather than one
/// per head, for the reason <see cref="PanelPlacementMemory"/> gives: a Windows user and a
/// Linux user expect a remembered place to be read and kept the same way.
///
/// <para>The file lands in O-view's own data folder (<c>%LOCALAPPDATA%\O-view</c> on Windows,
/// <c>~/.local/share/O-view</c> on Linux, which is where .NET maps <c>LocalApplicationData</c>)
/// and never among Claude's files, which O-view only ever reads.</para>
///
/// <para>A corner the file does not name degrades to "nothing remembered", exactly as an
/// unreadable file already does: a place O-view cannot interpret is not a place, and guessing a
/// corner would put the panel somewhere the user never left it. <c>Enum.IsDefined</c> sits
/// beside the parse because <c>TryParse</c> also accepts a bare number (<c>"7"</c> would hand
/// back a corner that does not exist), and a hand-edited or half-written file is exactly where
/// that comes from.</para>
/// </summary>
public sealed class FilePanelPositionStore(PanelPositionFile file) : IPanelPositionStore
{
    public FilePanelPositionStore() : this(new PanelPositionFile()) { }

    public PanelPlace? Read(string displayId) =>
        file.Read(displayId) is { } place
        && Enum.TryParse<WorkAreaCorner>(place.Corner, out var corner) && Enum.IsDefined(corner)
            ? new PanelPlace(corner, place.OffsetXDip, place.OffsetYDip)
            : null;

    public void Save(string displayId, PanelPlace place) =>
        file.Save(displayId, new PanelPositionFile.Place(
            place.Corner.ToString(), place.OffsetXDip, place.OffsetYDip));

    /// <summary>
    /// Forgets this display's place, so the panel opens at the work-area corner again: the
    /// menu's "Reset panel position" row. The entry is removed from the file, so the choice
    /// survives a restart where a flag in memory would not.
    /// </summary>
    public void Forget(string displayId) => file.Forget(displayId);
}
