using System.Windows;
using System.Windows.Controls;
using OView.Core.Models;
using OView.Core.Providers.Jsonl;
using OView.Presentation.Panel;
using OView.Presentation.Text;

namespace OView.Tray.Popup;

/// <summary>
/// Fills the four statistics tiles, each with the per-model split behind its total
/// (issue #37), and the scope note beneath them.
///
/// Both today tiles share ModelsToday and both 31-day tiles share Models31Days —
/// one list per window, split two ways, because a token breakdown and a value
/// breakdown of the same usage are the same rows measured differently.
/// </summary>
internal sealed class PopupTiles(
    TextBlock scopeLine, StatTile tokensToday, StatTile estToday, StatTile tokens31, StatTile est31,
    TextBlock scopeNote)
{
    public void Populate(PanelStatistics stats, bool authoritative, bool hasSessionPercent, TranscriptScopeReport? scopeReport)
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

        // Nothing recorded at all, while the plan meters show real usage: the tiles are
        // measuring a source this user does not feed, not measuring zero usage. Say which
        // source, so the 0 is interpretable instead of looking broken.
        //
        // The text is derived from what the scan actually resolved, never a literal. It
        // used to name a hard-coded %USERPROFILE%\.claude\projects and only Claude Code —
        // so a Cowork user, whose transcripts ARE read, was told their source was not
        // counted, and a packaged Desktop install was pointed at a path O-view never
        // searched (issue #58; the mistake ClaudeDataRoots exists to prevent).
        //
        // Keyed on the token total, not on RecordedDays. Those meant the same thing while
        // RecordedDays counted days with usage; now that it counts days observed (issue
        // #142), a store older than the window reports full coverage while the tiles still
        // read zero — the exact case this note is for. "The tiles show nothing" is what the
        // note is about, so it is what the condition should say.
        if (stats.Tokens31Days == 0 && authoritative && hasSessionPercent)
        {
            scopeNote.Text = (scopeReport ?? TranscriptScopeReport.Inspect()).Explain();
            scopeNote.Visibility = Visibility.Visible;
        }
        else
        {
            scopeNote.Visibility = Visibility.Collapsed;
        }
    }
}
