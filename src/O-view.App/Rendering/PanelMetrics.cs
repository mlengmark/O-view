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
}
