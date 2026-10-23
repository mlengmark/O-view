using System.Windows;
using System.Windows.Controls;
using OView.Core.Models;
using OView.Presentation.Panel;
using OView.Presentation.Text;

namespace OView.Tray.Popup;

/// <summary>
/// Fills the four statistics tiles, each with the per-model split behind its total
/// (issue #37), and the scope note beneath them.
///
/// Both today tiles share ModelsToday and both 31-day tiles share Models31Days,
/// one list per window, split two ways, because a token breakdown and a value
/// breakdown of the same usage are the same rows measured differently.
/// </summary>
internal sealed class PopupTiles(
    TextBlock scopeLine, StatTile tokensToday, StatTile estToday, StatTile tokens31, StatTile est31,
    TextBlock scopeNote)
{
    /// <param name="note">
    /// The scope note beneath the tiles, or null when it does not apply; decided in
    /// <see cref="PanelSections.ScopeNote"/>.
    /// </param>
    public void Populate(PanelStatistics stats, string? note)
    {
        // Always shown, unlike the per-tile caveats. Partial history and an unpriced model are
        // conditions that pass; the tiles excluding chat and cloud sessions is the standing shape
        // of the data, and a note that appeared only sometimes would teach a reader that its
        // absence means full coverage (issue #235). It sits under the block rather than on a
        // tile, which is why it is not part of a tile's view model.
        scopeLine.Text = TokenText.TokenScopeCaveat;

        // Both faces of all four tiles, including which caveat each carries and the colour slot
        // every model takes. This piece only places them.
        var tiles = PanelSections.Tiles(stats);

        tokensToday.Populate(tiles.TokensToday);
        estToday.Populate(tiles.EstToday);
        tokens31.Populate(tiles.Tokens31Days);
        est31.Populate(tiles.Est31Days);

        if (note is not null)
        {
            scopeNote.Text = note;
            scopeNote.Visibility = Visibility.Visible;
        }
        else
        {
            scopeNote.Visibility = Visibility.Collapsed;
        }
    }
}
