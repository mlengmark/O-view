namespace OView.App.Rendering;

/// <summary>
/// The layout numbers both heads draw the panel with, stated once so a Windows user and a
/// Linux user see the same panel. Each head still lays its own tree out; this is only the
/// set of figures the two were restating separately, which is how a drifted number would
/// have gone unnoticed. Device-independent units throughout, so the panel reads the same at
/// every scale factor.
/// </summary>
public static class PanelMetrics
{
    /// <summary>The panel's width; its height follows its content and the work area.</summary>
    public const double PanelWidthDip = 400;

    /// <summary>
    /// The scroll bar's width. Thin on purpose: it only ever appears on a display that is
    /// already short of room, so it costs the content as little width as it can while still
    /// reading as something to grab.
    /// </summary>
    public const double ScrollBarWidthDip = 6;

    /// <summary>Gap between the content and the scroll bar, so a right-aligned figure does not read as touching it.</summary>
    public const double ScrollBarGapDip = 6;

    /// <summary>
    /// Shortest the scroll thumb may get. A thumb kept strictly proportional to very tall
    /// content shrinks to a few pixels, which is neither visible nor grabbable.
    /// </summary>
    public const double ScrollThumbMinHeightDip = 24;

    /// <summary>Gap between a stat tile's bar segments, drawn in the surface colour.</summary>
    public const double TileSegmentGapDip = 2;

    /// <summary>Rounding on a tile bar's data end; the baseline end stays square.</summary>
    public const double TileDataEndRadiusDip = 4;

    /// <summary>
    /// The tile's flip glyph at rest, under the pointer and while the breakdown is open: the
    /// affordance should be findable and never louder than the number it sits beside.
    /// </summary>
    public const double TileGlyphIdleOpacity = 0.35;

    /// <inheritdoc cref="TileGlyphIdleOpacity"/>
    public const double TileGlyphHoverOpacity = 0.75;

    /// <inheritdoc cref="TileGlyphIdleOpacity"/>
    public const double TileGlyphOpenOpacity = 0.9;

    /// <summary>A day with any usage draws at least this tall, so a tiny day is still a bar and not a gap.</summary>
    public const double GraphMinBarHeightDip = 2;

    /// <summary>How far a weekly-reset gridline runs past the foot of the bars, into the date labels.</summary>
    public const double GraphBoundaryOverhangDip = 3;

    /// <summary>The gridline's dash: on for this long, then off for <see cref="GraphBoundaryDashOffDip"/>.</summary>
    public const double GraphBoundaryDashOnDip = 2;

    /// <inheritdoc cref="GraphBoundaryDashOnDip"/>
    public const double GraphBoundaryDashOffDip = 2;

    /// <summary>The rotated date label under each column: small, but still legible.</summary>
    public const double GraphLabelFontSize = 8;

    /// <summary>
    /// A date with no recorded data is dimmed rather than left blank: the column needs its
    /// date for the reader to see which days are missing.
    /// </summary>
    public const double GraphUnrecordedLabelOpacity = 0.5;

    // ── The two-block panel ─────────────────────────────────────────────────────────
    //
    // Measured at the panel's width with the roomy density's padding. Font sizes are in
    // DIP like every other size here; letter spacing is in em so it follows the size it
    // is applied to.

    /// <summary>
    /// Inset between the panel's border and its content, at the roomy density
    /// (<see cref="PanelDensity"/>), the one the column widths below add up against.
    /// </summary>
    public const double PanelPaddingDip = 16;

    /// <summary>The width the blocks and their tables share: the panel less its padding on both sides.</summary>
    public const double ContentWidthDip = PanelWidthDip - 2 * PanelPaddingDip;

    // Blocks and their lines

    /// <summary>
    /// Space above and below a block's content. Blocks have no side padding and no fill: they
    /// sit directly on the panel, the full content width.
    /// </summary>
    public const double BlockPaddingYDip = 10;

    /// <summary>
    /// How much of <c>PanelBorder</c> the hairline between blocks and every row line draws
    /// at. Soft, because on a flat surface a full-strength line reads as a box.
    /// </summary>
    public const double SoftLineOpacity = 0.6;

    /// <summary>How much of <c>PanelBorder</c> the vertical line at the left of a table's data column draws at.</summary>
    public const double ColumnLineOpacity = 0.35;

    /// <summary>A block's title: small, semibold, upper case, letter-spaced.</summary>
    public const double BlockTitleFontSize = 11;

    /// <summary>
    /// Letter spacing of a block title and of a table's column headers, in em. Spacing adds
    /// room after every letter including the last, so the same amount is added as an indent
    /// (<see cref="LetterSpacedIndentEm"/>) for the text to sit centred.
    /// </summary>
    public const double LetterSpacingEm = 0.04;

    /// <inheritdoc cref="LetterSpacingEm"/>
    public const double LetterSpacedIndentEm = 0.04;

    /// <summary>The local block's line under its title.</summary>
    public const double BlockSubtitleFontSize = 10.5;

    // Your plan

    /// <summary>A meter's name and its "n% used".</summary>
    public const double MeterLabelFontSize = 12;

    /// <summary>A meter's bar.</summary>
    public const double MeterBarHeightDip = 8;

    /// <summary>Rounding on a meter bar, half its height: both ends are round.</summary>
    public const double MeterBarRadiusDip = 4;

    /// <summary>A meter's reset line.</summary>
    public const double MeterResetFontSize = 10.5;

    /// <summary>Space above each meter.</summary>
    public const double MeterGapAboveDip = 8;

    /// <summary>Space below a meter's reset line.</summary>
    public const double MeterGapBelowDip = 2;

    /// <summary>The off-plan row's caption under its chip.</summary>
    public const double OffPlanCaptionFontSize = 10;

    /// <summary>The off-plan row's figure.</summary>
    public const double OffPlanFigureFontSize = 15;

    /// <summary>The coverage caveat under the off-plan figure.</summary>
    public const double OffPlanCaveatFontSize = 10;

    /// <summary>Space between the soft line above the off-plan row and the row itself.</summary>
    public const double OffPlanPaddingAboveDip = 9;

    /// <summary>The banner's hide control is a square this size.</summary>
    public const double BannerHideSizeDip = 16;

    /// <summary>How much of <c>WarnText</c> the hide control draws at.</summary>
    public const double BannerHideOpacity = 0.75;

    // The 31-day chart

    /// <summary>The bars' area, from the top of the tallest to the baseline.</summary>
    public const double ChartHeightDip = 96;

    /// <summary>The band under the baseline that carries the week labels.</summary>
    public const double ChartAxisBandDip = 16;

    /// <summary>How far in from each edge of its slot a bar is drawn, so neighbours do not touch.</summary>
    public const double ChartBarInsetDip = 1.2;

    /// <summary>Rounding on a chart bar.</summary>
    public const double ChartBarRadiusDip = 1.5;

    /// <summary>Opacity of the oldest bar. Bars rise from here to full at today, so age reads without a legend.</summary>
    public const double ChartOldestBarOpacity = 0.55;

    /// <summary>Opacity of today's bar, and the newest end of the fade.</summary>
    public const double ChartNewestBarOpacity = 1;

    /// <summary>The week-start dates written along the chart's gridlines.</summary>
    public const double ChartWeekLabelFontSize = 9.5;

    /// <summary>Space between the soft line under the chart and the table below it.</summary>
    public const double ChartToTableGapDip = 10;

    // Tables

    /// <summary>A table's column headers: upper case, letter-spaced, muted.</summary>
    public const double TableHeaderFontSize = 10;

    /// <summary>A table's row labels.</summary>
    public const double TableLabelFontSize = 12;

    /// <summary>A table's figures: semibold, centred in their column, tabular digits.</summary>
    public const double TableFigureFontSize = 12;

    /// <summary>A share of the total, beside its figure.</summary>
    public const double TableShareFontSize = 11;

    /// <summary>
    /// Inset left of a data cell's text. With <see cref="TableCellPadRightDip"/> it puts the
    /// text's centre on the column's centre once the column line's own pixel is allowed for.
    /// </summary>
    public const double TableCellPadLeftDip = 3;

    /// <inheritdoc cref="TableCellPadLeftDip"/>
    public const double TableCellPadRightDip = 4;

    /// <summary>The Output table's two data columns, Today and 31 days.</summary>
    public const double OutputColumnDip = 112;

    /// <summary>The Overall table's four data columns, Today, Share, 31 days, Share.</summary>
    public const double OverallColumnDip = 64;

    /// <summary>The model table's four data columns, Tokens and Est. value for each window.</summary>
    public const double ModelColumnDip = 62;

    /// <summary>The Output table's label column: what its two data columns leave.</summary>
    public const double OutputLabelColumnDip = ContentWidthDip - 2 * OutputColumnDip;

    /// <summary>The Overall table's label column: what its four data columns leave.</summary>
    public const double OverallLabelColumnDip = ContentWidthDip - 4 * OverallColumnDip;

    /// <summary>The model table's label column, chip and name: what its four data columns leave.</summary>
    public const double ModelLabelColumnDip = ContentWidthDip - 4 * ModelColumnDip;

    // The est. value pill

    /// <summary>Rounding on the pill an Overall est. value sits in.</summary>
    public const double PillRadiusDip = 10;

    /// <summary>The pill's border; its colour is <c>PanelBorder</c> at <see cref="SoftLineOpacity"/>.</summary>
    public const double PillBorderDip = 1;

    /// <summary>Space above and below the text inside the pill.</summary>
    public const double PillPaddingYDip = 1;

    /// <summary>Space left and right of the text inside the pill.</summary>
    public const double PillPaddingXDip = 12;

    /// <summary>Space between the last table row and the pill.</summary>
    public const double PillGapAboveDip = 7;

    /// <summary>Space between the pill and what follows it.</summary>
    public const double PillGapBelowDip = 2;

    // Footer, switch and flip

    /// <summary>The footer's caveat line, and the labels of the Output | Overall switch beside it.</summary>
    public const double FooterFontSize = 10.5;

    /// <summary>The flip tag's text, "by model" and "totals".</summary>
    public const double FlipTagFontSize = 10;

    /// <summary>How much of <c>PanelBorder</c> the outline round the block draws at while the pointer is over it.</summary>
    public const double FlipOutlineOpacity = 0.35;

    /// <summary>The hover outline's line.</summary>
    public const double FlipOutlineThicknessDip = 1;

    /// <summary>How far outside the block the hover outline is drawn.</summary>
    public const double FlipOutlineOutsetDip = 6;

    /// <summary>Rounding on the hover outline.</summary>
    public const double FlipOutlineRadiusDip = 6;

    // The model face

    /// <summary>A doughnut's square.</summary>
    public const double DoughnutSizeDip = 104;

    /// <summary>The ring's radius, measured to the middle of its stroke.</summary>
    public const double DoughnutRingRadiusDip = 38;

    /// <summary>The ring's thickness.</summary>
    public const double DoughnutStrokeDip = 13;

    /// <summary>The gap left between neighbouring sections of a ring.</summary>
    public const double DoughnutSectionGapDip = 1.5;

    /// <summary>The window's total, in the middle of the ring.</summary>
    public const double DoughnutCentreTotalFontSize = 13;

    /// <summary>"output" or "tokens", under the total.</summary>
    public const double DoughnutCentreUnitFontSize = 9.5;

    /// <summary>"TODAY" and "LAST 31 DAYS" under each ring.</summary>
    public const double DoughnutLabelFontSize = 10;

    /// <summary>Space between the doughnuts and the model table.</summary>
    public const double DoughnutsToTableGapDip = 16;

    /// <summary>A model's token count in the model table.</summary>
    public const double ModelTokensFontSize = 11.5;

    /// <summary>A model's est. value in the model table.</summary>
    public const double ModelValueFontSize = 11;
}
