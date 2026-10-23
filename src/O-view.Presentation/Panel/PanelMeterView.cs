using OView.Core.Models;

namespace OView.Presentation.Panel;

/// <summary>
/// One of the two usage meters, as a head has to place it: a label, the figure beside it, the
/// fill, the line under it, and — on the weekly meter — the boost chip.
///
/// <para><b>Why the figure and the fill are separate.</b> <see cref="Value"/> is what the row
/// says; <see cref="FillPercent"/> is how wide to draw. They part company exactly where it
/// matters: with no percentage the row states why (<see cref="Text.PanelBanner"/>'s
/// placeholder) and the fill is absent rather than zero, because a zero-width fill beside a
/// number would read as "no usage" when the truth is "not known" (rule 6).</para>
/// </summary>
/// <param name="Label">The meter's name, above the bar.</param>
/// <param name="Percent">
/// The reported figure, or null when there is none. The figure as stated, not as drawn — a
/// head sizes its fill from <see cref="FillPercent"/>.
/// </param>
/// <param name="Value">
/// The figure as the row states it: <c>47% used</c>, or the placeholder explaining why there
/// is no figure.
/// </param>
/// <param name="Band">
/// Which usage band the figure falls in (<see cref="UsageLevels"/>), or null with no figure.
/// The band, not a colour: both heads paint it through the tray icon's theme-aware rule, and a
/// view model carrying the colour would have to carry a toolkit type.
/// </param>
/// <param name="FillPercent">
/// <see cref="Percent"/> clamped to 0–100 for drawing, or null when no fill is drawn. Clamped
/// here because a figure outside the range is a data question, not a layout one; how few
/// pixels a 1% fill may shrink to is the layout question, and stays with each head.
/// </param>
/// <param name="ResetLine">
/// The line under the bar, or null when the row is not shown at all. For a surface that can
/// put <see cref="ResetHint"/> on hover.
/// </param>
/// <param name="ResetLineWithAction">
/// The same line for a surface with no hover, where the action in <see cref="ResetHint"/> has
/// to ride on the line itself or never appear. Identical to <see cref="ResetLine"/> except in
/// the one state that has an action to offer — an unknown weekly reset.
/// </param>
/// <param name="ResetHint">The hover card for that line, or null when it needs none.</param>
/// <param name="Chip">The boost chip beside the label, or null — which is the usual case.</param>
public sealed record PanelMeterView(
    string Label,
    int? Percent,
    string Value,
    UsageLevel? Band,
    int? FillPercent,
    string? ResetLine,
    string? ResetLineWithAction,
    string? ResetHint,
    PanelBoostChipView? Chip);

/// <summary>
/// The boost chip and its card (issue #254). The chip carries O-view's summary; the card
/// carries Claude's own sentence, unedited, for a surface that has somewhere to put it.
/// </summary>
public sealed record PanelBoostChipView(string Text, string Card);
