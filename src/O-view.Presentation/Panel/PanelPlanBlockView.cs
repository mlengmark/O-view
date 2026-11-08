namespace OView.Presentation.Panel;

/// <summary>
/// The panel's first block: its meters, the off-plan row that closes it, and the banner that
/// sits above both blocks, built once for both heads.
///
/// <para><b>The banner is here because hiding it is a view choice.</b> Whether it shows
/// follows the <see cref="PanelViewChoice"/> the head passed in, so the rule that a hidden
/// notice draws nothing is stated once, beside the notice, rather than in each head.</para>
/// </summary>
/// <param name="Title">The block's title, as words; capitals are the head's styling.</param>
/// <param name="Meters">
/// Session, weekly, then one per model with a weekly limit of its own, in the order they are
/// drawn. The block carries no reset note of its own, each meter states its own.
/// </param>
/// <param name="OffPlanRow">
/// The block's last row, or null when nothing fell outside the plan, which is the usual state.
/// </param>
/// <param name="Banner">
/// The live off-plan banner, or null when the window is on-plan or the reader hid it.
/// </param>
/// <param name="HideBannerLabel">What the banner's hide control is called.</param>
public sealed record PanelPlanBlockView(
    string Title,
    IReadOnlyList<PanelMeterView> Meters,
    PanelOffPlanRowView? OffPlanRow,
    PanelOffPlanBannerView? Banner,
    string HideBannerLabel);

/// <summary>
/// The off-plan row: a heading with its source badge, what the figure is, and the figure with
/// the caveat that qualifies it. No bar, because there is no limit to fill against.
/// </summary>
/// <param name="Title">The row's heading.</param>
/// <param name="Badge">States where the figure comes from.</param>
/// <param name="Caption">What the figure is, under the heading: the estimate and its window, or Claude's own.</param>
/// <param name="Value">
/// The figure, or "unknown" when it could not be priced or read: usage O-view cannot value is
/// not the same as no usage (rule 6).
/// </param>
/// <param name="Coverage">
/// The caveat directly under the figure, or empty when the window is fully covered. It sits
/// here and nowhere else, beside the one figure it qualifies.
/// </param>
/// <param name="Hint">What the figure is and is not, for a head that can show it on hover.</param>
public sealed record PanelOffPlanRowView(
    string Title,
    string Badge,
    string Caption,
    string Value,
    string Coverage,
    string Hint);
