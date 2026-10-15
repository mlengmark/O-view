namespace OView.Presentation.Panel;

/// <summary>
/// One statistics tile and both of its faces: the total a reader sees first, and the
/// per-model split behind it (GitHub issue #37).
///
/// <para><b>Both faces, in one object, because they describe one tile.</b> The caveat is the
/// clearest case — the same sentence belongs on the summary and on the breakdown, and the two
/// heads each had their own code deciding that. A tile whose faces were built separately is
/// one edit away from a total that carries its coverage caveat and a breakdown of the same
/// total that does not.</para>
///
/// <para>Every segment arrives measured and worded. What stays with each head is the chart
/// itself: how a share becomes pixels, how few of them a thin segment may shrink to, and
/// whether a hover card can exist on that surface at all.</para>
/// </summary>
/// <param name="Label">The tile's name, above the figure.</param>
/// <param name="Value">The total, formatted — the summary face's headline and, on the
/// breakdown face, the figure the segments add up to.</param>
/// <param name="Note">
/// The caveat under the figure, or the empty string when there is none. Empty rather than
/// null because every text member that can have nothing to say returns one, and the decision
/// to draw the row belongs with the text rather than being re-derived at each call site.
/// </param>
/// <param name="Segments">
/// The per-model split, largest first, already folded and capped by
/// <see cref="Core.Models.ModelBreakdown.Segments"/>. Empty when there is nothing to break
/// down — see <see cref="CanFlip"/>.
/// </param>
/// <param name="Caption">The line under the chart: <c>by model</c>, or what it excludes.</param>
/// <param name="CaptionHint">
/// The hover card naming the models a value chart had to leave out, or null when it left none
/// out. Null is the usual case, and a head with nowhere to put a card simply shows
/// <see cref="Caption"/>, which already states that something is missing (rule 6).
/// </param>
public sealed record PanelTileView(
    string Label,
    string Value,
    string Note,
    IReadOnlyList<PanelTileSegmentView> Segments,
    string Caption,
    string? CaptionHint)
{
    /// <summary>
    /// Whether this tile has a second face at all.
    ///
    /// <para>A tile with nothing to break down must not offer the flip: an affordance that
    /// leads nowhere is worse than none. Asked of the segments rather than of the raw
    /// per-model list, because a list can be non-empty and still produce no chart — every
    /// model in it measuring zero on this tile's measure, or an unpriced model on a value
    /// tile, both of which <see cref="Core.Models.ModelBreakdown.Segments"/> drops.</para>
    /// </summary>
    public bool CanFlip => Segments.Count > 0;
}

/// <summary>
/// One segment of a tile's per-model chart: its share of the bar, the colour slot it takes,
/// and the two lines naming it.
/// </summary>
/// <param name="Name">
/// The legend's text — the model's published name, or the word standing in for the folded
/// remainder. Name only: the figure sits in the segment's own hover card, and dropping it here
/// shortens every entry enough that the legend usually stops wrapping.
/// </param>
/// <param name="Figure">
/// This segment's measure, formatted — tokens on a token tile, money on a value tile. The
/// hover card's headline, so a reader pointing at a segment learns what it is worth rather
/// than only how wide it is.
/// </param>
/// <param name="Identity">
/// The line under the figure: the raw model id, or how many models were folded into the
/// remainder. <b>Always present</b> — it was once suppressed when it matched
/// <see cref="Name"/>, which is exactly the case for an unrecognised model, so those cards
/// showed a number with nothing identifying it.
/// </param>
/// <param name="Share">
/// This segment's fraction of the tile's total, 0–1, for proportioning the bar. Computed here
/// because it is arithmetic on the data; turning it into a width — and deciding the floor
/// below which a segment would vanish — is the head's.
/// </param>
/// <param name="Slot">
/// Which of the panel's chromatic slots paints this segment, or -1 for the neutral remainder.
///
/// <para><b>The slot, never the colour</b> — the same rule <see cref="PanelMeterView.Band"/>
/// follows, and for the same reason: the heads paint from different palettes and a view model
/// carrying a brush would have to carry a toolkit type. The slot follows the MODEL, from the
/// panel-wide order, never the segment's rank in this tile. Colouring by rank is what made
/// Opus 5 blue where it happened to be largest and orange where it was second, so the four
/// tiles could not be read against one another.</para>
/// </summary>
public sealed record PanelTileSegmentView(
    string Name,
    string Figure,
    string Identity,
    double Share,
    int Slot);

/// <summary>
/// The four statistics tiles, in the order both panels place them: today's pair above the
/// 31-day pair, tokens on the left and value on the right.
///
/// <para>Named fields rather than a list. Both heads lay the four out in fixed positions —
/// Windows in markup, Linux in two rows — so an index would be a position each head had to
/// know the meaning of, which is the kind of agreement that drifts.</para>
/// </summary>
public sealed record PanelTilesView(
    PanelTileView TokensToday,
    PanelTileView EstToday,
    PanelTileView Tokens31Days,
    PanelTileView Est31Days);
