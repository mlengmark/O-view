using OView.Core.Models;

namespace OView.Presentation.Panel;

/// <summary>
/// The view the reader has chosen: which measure the figures count, which face the local block
/// shows, and whether the off-plan banner was hidden.
///
/// <para><b>Presentation stores nothing.</b> The head keeps the choice (and, on Windows,
/// remembers it across restarts), hands it to each builder, and takes the next one from the
/// switch's options, which carry the choice with one field changed. The default, all zero
/// values, is a fresh install's view: Output, totals, banner shown.</para>
/// </summary>
public readonly record struct PanelViewChoice(
    PanelMeasure Measure = PanelMeasure.Output,
    PanelFace Face = PanelFace.Totals,
    bool BannerHidden = false);

/// <summary>
/// The local block's footer: the one standing caveat, and the Output | Overall switch.
/// </summary>
/// <param name="Caveat">Which surfaces the figures are made of, and which are not counted.</param>
/// <param name="Options">The switch's segments, in the order they are drawn.</param>
public sealed record PanelLocalFooterView(string Caveat, IReadOnlyList<PanelMeasureOption> Options);

/// <summary>One segment of the switch.</summary>
/// <param name="Label">What the segment is called.</param>
/// <param name="Selected">Whether this is the measure the figures currently count.</param>
/// <param name="Choice">
/// The view pressing this segment gives: the current one with only the measure changed, so a
/// head cannot lose the face or the hidden banner on the way.
/// </param>
public sealed record PanelMeasureOption(string Label, bool Selected, PanelViewChoice Choice);
