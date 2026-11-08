using OView.Core.Models;

namespace OView.Presentation.Panel;

/// <summary>
/// The local block's totals face: the chart, and the table under it. Output and Overall differ
/// in the rows and in the chart's series, never in the grammar, so one shape serves both and
/// the head lays out whichever it is handed.
///
/// <para><b>Which total, which estimate, which series and which wording are decided in
/// <see cref="PanelSections.LocalTotals"/> and nowhere else.</b> A head that picked its own
/// would be the two-panels-disagreeing failure the shared builders exist to prevent.</para>
/// </summary>
/// <param name="Title">The block's title, as words.</param>
/// <param name="Subtitle">The window and the total behind the figures.</param>
/// <param name="Chart">The 31-day bars for the chosen measure.</param>
/// <param name="Table">The figures, today and over the window.</param>
/// <param name="WindowCaveat">
/// What qualifies the window's figures: partial history, unpriced models, assumed cache-write
/// durations, stale rates. Empty when none applies. It rides beside the 31-day column it
/// qualifies, not under the whole block and not on every figure.
/// </param>
public sealed record PanelLocalTotalsView(
    string Title,
    string Subtitle,
    PanelLocalChartView Chart,
    PanelLocalTableView Table,
    string WindowCaveat);

/// <summary>
/// The chart: one column per day, the week marks with their dates, and the label on the last
/// column. Columns are the shared <see cref="PanelGraphColumnView"/>, heights as fractions of
/// the busiest day, with no bar on a day before install (rule 6).
/// </summary>
public sealed record PanelLocalChartView(
    IReadOnlyList<PanelGraphColumnView> Columns,
    IReadOnlyList<PanelLocalWeekMarkView> WeekMarks,
    string TodayLabel);

/// <summary>One week line and the date written beside it, <c>13 Oct</c>.</summary>
public sealed record PanelLocalWeekMarkView(PanelGraphBoundaryView Boundary, string DateLabel);

/// <summary>Which of the table's row shapes a row is; each head styles them differently.</summary>
public enum PanelLocalRowKind
{
    /// <summary>A figure row: output alone, or one token kind with its chip.</summary>
    Figure,

    /// <summary>The bold total of the kinds above it.</summary>
    Total,

    /// <summary>The estimated value, drawn as a pill across each pair of columns where it spans two.</summary>
    Value,
}

/// <summary>The table: its column headings and its rows.</summary>
/// <param name="Headers">
/// One per column, the label column first. Its first entry is empty in the Output view, which
/// has no kinds to name.
/// </param>
public sealed record PanelLocalTableView(
    IReadOnlyList<string> Headers,
    IReadOnlyList<PanelLocalRowView> Rows);

/// <summary>
/// One row. <paramref name="Cells"/> follows the headings after the label, each one a figure
/// already worded. <paramref name="CellSpan"/> is how many columns each cell covers: 1 for a
/// figure, 2 for an Overall value pill, which sits across the pair of columns it belongs to.
/// </summary>
/// <param name="Kind">The token kind whose colour chip leads the row, or null for a row with none.</param>
public sealed record PanelLocalRowView(
    PanelLocalRowKind RowKind,
    TokenKind? Kind,
    string Label,
    IReadOnlyList<string> Cells,
    int CellSpan = 1);
