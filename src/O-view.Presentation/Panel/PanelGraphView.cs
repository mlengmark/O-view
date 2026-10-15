namespace OView.Presentation.Panel;

/// <summary>
/// The 31-day usage chart, measured and worded for both heads (docs/ui-spec.md §2, GitHub
/// issues #4, #5).
///
/// <para><b>Why one view model.</b> The two heads drew two different charts from the same
/// rollups. Windows shaded each bar by its intensity within its own plan week, drew the reset
/// gridlines at their true fractional position and put a hover card on every bar and every
/// line; Linux drew flat bars, no gridlines, no dates and no cards. Nothing about that
/// difference was a toolkit limitation — it was the week bands, the boundary placement and
/// the card wording living inside one head, where the other could not reach them.</para>
///
/// <para>What stays with each head is what a toolkit has an opinion about: how a fraction
/// becomes pixels, how few of them a bar may shrink to, how a rotated label is placed, and
/// whether a hover card can exist on that surface at all. Every number here is a
/// <b>fraction</b> for that reason — a view model carrying a height in pixels would be
/// deciding the chart's size for a head whose strip is a different one.</para>
/// </summary>
/// <param name="Heading">The line above the chart, naming the window it covers.</param>
/// <param name="Columns">One per day, oldest first — the order both heads draw them in.</param>
/// <param name="Boundaries">
/// The weekly-reset gridlines, each one placed inside the column it falls in. Empty when none
/// falls in the plotted range.
/// </param>
public sealed record PanelGraphView(
    string Heading,
    IReadOnlyList<PanelGraphColumnView> Columns,
    IReadOnlyList<PanelGraphBoundaryView> Boundaries);

/// <summary>
/// One day's column: how tall its bar stands, how dark it is, the date under it, and what a
/// reader pointing at it is told.
/// </summary>
/// <param name="DateLabel">The date under the column, short enough to sit under one of 31.</param>
/// <param name="Recorded">
/// Whether O-view was running on this day. A day before the first recorded one is drawn muted:
/// it has no data, not zero data.
/// </param>
/// <param name="Height">
/// This day's tokens as a fraction, 0–1, of the busiest day in the window — the bar's height
/// before the head turns it into pixels. <b>Zero means no bar at all</b>, which is both the
/// pre-install days and a recorded day that genuinely used nothing: a zero-height bar would
/// claim a measured day of no usage on a day that was never measured (rule 6).
/// </param>
/// <param name="Intensity">
/// This day's tokens as a fraction, 0–1, of the busiest day <em>of its own week</em> — the
/// colour ramp's position, so the busiest day of every week is the darkest one in that band
/// (issue #5). The week is the plan's own week wherever it is known, the same boundaries
/// <see cref="PanelGraphView.Boundaries"/> is drawn at, so the shading and the gridlines can
/// never describe different bands.
/// </param>
/// <param name="CardFigure">
/// The hover card's headline: this day's tokens. Empty when there is no bar to point at.
/// </param>
/// <param name="CardCaption">
/// The line under the figure: the day in full, which the cramped <see cref="DateLabel"/>
/// cannot be. Empty alongside an empty <see cref="CardFigure"/>.
/// </param>
public sealed record PanelGraphColumnView(
    string DateLabel,
    bool Recorded,
    double Height,
    double Intensity,
    string CardFigure,
    string CardCaption)
{
    /// <summary>
    /// Whether this column draws a bar. Asked of the height rather than of
    /// <see cref="Recorded"/>, because a recorded day that used nothing has no bar either —
    /// and asked here rather than at four call sites that would each have to pick the same
    /// comparison.
    /// </summary>
    public bool HasBar => Height > 0;
}

/// <summary>
/// One week-boundary gridline: where it falls, and what it marks.
/// </summary>
/// <param name="Column">The index into <see cref="PanelGraphView.Columns"/> it falls in.</param>
/// <param name="FractionOfDay">
/// How far through that column it sits, 0–1. A reset happens at a time of day, not at
/// midnight, so snapping the line to the nearest column edge would assert a boundary the data
/// does not have. Taken in the column's own frame — a local day, of whatever length the
/// timezone says it was — so the line cannot drift against the bar it annotates from a DST
/// change onwards (issue #211). The Monday fallback lands on 0, because midnight is a column
/// edge, and the two are meant to be visibly different.
/// </param>
/// <param name="CardFigure">
/// The hover card's headline: the reset's own local time. <b>Empty when this is the Monday
/// fallback</b>, which has no instant to state — nothing has been observed, and inventing one
/// is the fabricated number this card exists to avoid (rule 6).
/// </param>
/// <param name="CardCaption">
/// What the line marks: the plan's weekly reset, or the sentence saying this is a calendar
/// reference standing in for a boundary O-view has not seen yet.
/// </param>
public sealed record PanelGraphBoundaryView(
    int Column,
    double FractionOfDay,
    string CardFigure,
    string CardCaption);
