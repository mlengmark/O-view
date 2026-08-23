namespace OView.App.Rendering;

/// <summary>
/// How tightly the detail panel packs itself, so a display too short for the natural layout
/// gets a denser one rather than a clipped one.
///
/// <para><b>Why density rather than scrolling or hiding.</b> The panel is
/// <c>SizeToContent</c> and docks to a work-area corner, so growing it past the work area
/// puts the bottom under the taskbar — <see cref="WorkAreaPlacement"/> pins the top and the
/// remainder is simply unreachable. Measured 2026-08-23 the panel is 662 DIP closed and 734
/// open, against roughly 680 DIP of work area on a 1920×1080 display at 150% scaling. Of
/// that 734, about 400 is text and cannot move; ~232 is spacing and 86 is a hard-coded graph
/// canvas, and those can.</para>
///
/// <para><b>Nothing is hidden and nothing scrolls.</b> Every figure, caveat and section is
/// present at either density — this only changes how much air sits between them. A panel that
/// silently dropped a section to fit would be the rule-6 failure this project keeps finding:
/// a number quietly absent reads as a number that is zero.</para>
///
/// <para>Shared because it is a statement about what the panel looks like, which a Windows
/// user and a Linux user would expect to match — the same reasoning that moved the corner
/// arithmetic here (issues #55, #56, #144).</para>
/// </summary>
/// <param name="RootPadding">Inset between the panel's border and its content.</param>
/// <param name="SeparatorGap">Vertical margin above and below each horizontal rule.</param>
/// <param name="CreditsSeparatorBottom">
/// Gap below the off-plan rule specifically, which ships 2 px tighter than every other
/// separator. The asymmetry is arbitrary but it is what the panel currently looks like, and
/// folding it into <paramref name="SeparatorGap"/> made every normal-density render 2 DIP
/// taller than the shipped one — a change nobody asked for, arriving inside a change about
/// fitting on short screens. <see cref="Normal"/> reproduces the shipped layout exactly.
/// </param>
/// <param name="GraphHeight">
/// The 31-day chart's canvas. The single largest fixed cost in the panel, and the one that
/// ignored how much room existed — it was a hard-coded 86 regardless of display.
/// </param>
/// <param name="GraphHeadingTop">Gap above the "Usage · last 31 days" heading.</param>
/// <param name="GraphHeadingBottom">Gap between that heading and the chart.</param>
/// <param name="TilePaddingX">Horizontal inset inside a stat tile.</param>
/// <param name="TilePaddingY">Vertical inset inside a stat tile — doubled across two rows.</param>
/// <param name="TileGridTop">Gap above the 2×2 tile grid.</param>
/// <param name="SectionGap">Gap between stacked sections that carry their own margin.</param>
public sealed record PanelDensity(
    double RootPadding,
    double SeparatorGap,
    double CreditsSeparatorBottom,
    double GraphHeight,
    double GraphHeadingTop,
    double GraphHeadingBottom,
    double TilePaddingX,
    double TilePaddingY,
    double TileGridTop,
    double SectionGap)
{
    /// <summary>The shipped layout, unchanged. Anything with room gets exactly this.</summary>
    public static readonly PanelDensity Normal = new(
        RootPadding: 16,
        SeparatorGap: 12,
        CreditsSeparatorBottom: 10,
        GraphHeight: 86,
        GraphHeadingTop: 14,
        GraphHeadingBottom: 6,
        TilePaddingX: 10,
        TilePaddingY: 8,
        TileGridTop: 14,
        SectionGap: 12);

    /// <summary>
    /// Roughly 94 DIP tighter, which takes the expanded panel from 734 to about 640 — inside
    /// a 680 DIP work area with margin to spare.
    ///
    /// <para>The reductions are deliberately uneven. The graph canvas gives up the most (30)
    /// because it is the only element whose height was arbitrary; text gaps give up the least,
    /// because a line of 11 px text with 3 px above it is already at the point where the panel
    /// stops reading as sections and starts reading as a wall.</para>
    /// </summary>
    public static readonly PanelDensity Compact = new(
        RootPadding: 10,
        SeparatorGap: 6,
        CreditsSeparatorBottom: 6,
        GraphHeight: 64,
        GraphHeadingTop: 8,
        GraphHeadingBottom: 4,
        TilePaddingX: 8,
        TilePaddingY: 5,
        TileGridTop: 8,
        SectionGap: 8);

    /// <summary>
    /// The density to lay out at, given what the panel wants and what the display allows.
    ///
    /// <para>Compares against the natural height measured with <see cref="Normal"/>, so the
    /// caller must lay out once before asking. A threshold on the work area alone would be a
    /// guess: the panel's height varies with whether a banner, an off-plan section or an
    /// expanded explanation is present, and it is the actual overflow that matters.</para>
    /// </summary>
    public static PanelDensity For(double naturalHeightDip, double availableHeightDip) =>
        naturalHeightDip > availableHeightDip ? Compact : Normal;

    /// <summary>Whether this is the tightened layout — for logging and for verification renders.</summary>
    public bool IsCompact => ReferenceEquals(this, Compact) || Equals(this, Compact);

    /// <summary>
    /// The chart's height relative to the natural layout — 1.0 at <see cref="Normal"/>.
    ///
    /// <para>For heads whose natural chart is not the Windows panel's 86 px. The Linux graph
    /// is a 60 px bar strip, and giving it <see cref="GraphHeight"/> directly would resize it
    /// on displays with plenty of room — a change to a head nobody can test, arriving inside a
    /// change about short screens. The <i>ratio</i> is the shared decision; the absolute is
    /// each head's own.</para>
    /// </summary>
    public double GraphScale => GraphHeight / Normal.GraphHeight;

    /// <summary>
    /// Generic spacing relative to the natural layout — 1.0 at <see cref="Normal"/>. Same
    /// reasoning as <see cref="GraphScale"/>, for the gaps a head expresses as its own
    /// constants rather than as the named fields here.
    /// </summary>
    public double SpacingScale => SectionGap / Normal.SectionGap;
}
