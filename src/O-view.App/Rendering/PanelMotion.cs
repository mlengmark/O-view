namespace OView.App.Rendering;

/// <summary>
/// The panel's and the menu's motion, stated once: the pace, every base duration and delay,
/// and the two curves. The Windows head turns these into WPF animations; they live here, free
/// of any toolkit type, so a test can pin the rules between them and a partial render can ask
/// where a curve is at 40%.
///
/// <para>Every figure below is a <b>base</b> figure. What runs on screen is the base times
/// <see cref="Pace"/> (<see cref="Ms"/>), so the whole app is retuned by editing one constant
/// rather than by hunting down thirty durations that were each scaled by hand.</para>
/// </summary>
public static class PanelMotion
{
    /// <summary>
    /// Multiplier on every base figure. 2 is the pace the motion was approved at; it is slow
    /// enough to read as a gesture, so it stays one edit to change.
    /// </summary>
    public const double Pace = 2;

    /// <summary>A base figure as it runs on screen, in milliseconds.</summary>
    public static double Ms(double baseMs) => baseMs * Pace;

    // ── Curves ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Things arriving or settling. Fitted to a frame-by-frame recording of Windows' own
    /// flyout (RMSE 0.0033 against the trace), which is why it is four unrounded numbers
    /// and not a named easing.
    /// </summary>
    public static readonly Curve Decelerate = new(0.02, 0.16, 0.20, 0.96);

    /// <summary>Things leaving: the mirror of <see cref="Decelerate"/>.</summary>
    public static readonly Curve Accelerate = new(0.80, 0.04, 0.98, 0.84);

    // ── Opening and closing a surface (the panel and the menu) ──────────────────────

    /// <summary>A surface arriving.</summary>
    public const double SurfaceOpenBaseMs = 230;

    /// <summary>A surface leaving.</summary>
    public const double SurfaceCloseBaseMs = 150;

    // ── Token breakdown ─────────────────────────────────────────────────────────────

    /// <summary>The Bars/Breakdown highlight sliding from the old segment to the new.</summary>
    public const double SwitchHighlightBaseMs = 180;

    /// <summary>The switch labels' text colour crossfading.</summary>
    public const double SwitchLabelsBaseMs = 140;

    /// <summary>The breakdown folding open.</summary>
    public const double FoldOpenBaseMs = 200;

    /// <summary>The breakdown folding closed.</summary>
    public const double FoldCloseBaseMs = 140;

    /// <summary>How many table rows the breakdown has.</summary>
    public const int BreakdownRowCount = 6;

    /// <summary>One table row fading in.</summary>
    public const double RowFadeBaseMs = 150;

    /// <summary>How far a table row rises into place, in DIP. Not scaled by the pace: it is a distance.</summary>
    public const double RowRiseDip = 4;

    /// <summary>When the first row starts, counted from the start of the fold.</summary>
    public const double RowFirstStartBaseMs = 50;

    /// <summary>How much later each row starts than the one above it.</summary>
    public const double RowStaggerBaseMs = 18;

    /// <summary>All rows fading out together, quick enough to be gone before the fold line reaches them.</summary>
    public const double RowsCloseFadeBaseMs = 70;

    // ── Right-click menu ────────────────────────────────────────────────────────────

    /// <summary>A row, option or pill fill fading in on hover or keyboard focus.</summary>
    public const double HoverFillInBaseMs = 70;

    /// <summary>The same fill fading out.</summary>
    public const double HoverFillOutBaseMs = 160;

    /// <summary>A tick drawing itself.</summary>
    public const double TickOnBaseMs = 160;

    /// <summary>A tick fading out.</summary>
    public const double TickOffBaseMs = 100;

    /// <summary>The threshold list folding open, and the chevron turning with it.</summary>
    public const double ThresholdOpenBaseMs = 170;

    /// <summary>The threshold list folding closed, and the chevron turning back.</summary>
    public const double ThresholdCloseBaseMs = 120;

    /// <summary>One option row fading in.</summary>
    public const double OptionFadeBaseMs = 130;

    /// <summary>When the first option row starts, counted from the start of the fold.</summary>
    public const double OptionFirstStartBaseMs = 40;

    /// <summary>How much later each option row starts than the one above it.</summary>
    public const double OptionStaggerBaseMs = 25;

    /// <summary>The option rows fading out together as the list closes.</summary>
    public const double OptionsCloseFadeBaseMs = 60;

    /// <summary>The dot on a newly chosen threshold scaling up and fading in.</summary>
    public const double ChosenDotBaseMs = 120;

    /// <summary>The dot's scale at the start of that, as a fraction of its size.</summary>
    public const double ChosenDotFromScale = 0.3;

    /// <summary>How long after the click the list starts folding closed, so the chosen dot is seen first.</summary>
    public const double ChosenCloseDelayBaseMs = 150;

    // ── Stat tile flip ──────────────────────────────────────────────────────────────

    /// <summary>The face going out.</summary>
    public const double TileFaceOutBaseMs = 90;

    /// <summary>The face coming in.</summary>
    public const double TileFaceInBaseMs = 170;

    /// <summary>When the incoming face starts, so the two faces overlap rather than hand over.</summary>
    public const double TileFaceInDelayBaseMs = 50;

    /// <summary>How far a face moves while it fades, in DIP. A distance, not scaled by the pace.</summary>
    public const double TileFaceTravelDip = 5;

    /// <summary>The model bar being revealed, left to right.</summary>
    public const double TileModelBarBaseMs = 260;

    /// <summary>When the model bar starts.</summary>
    public const double TileModelBarDelayBaseMs = 90;

    /// <summary>One legend entry fading in.</summary>
    public const double TileLegendFadeBaseMs = 140;

    /// <summary>When the first legend entry starts.</summary>
    public const double TileLegendFirstStartBaseMs = 150;

    /// <summary>How much later each legend entry starts than the one before.</summary>
    public const double TileLegendStaggerBaseMs = 35;

    /// <summary>The glyph turning half a circle.</summary>
    public const double TileGlyphTurnBaseMs = 240;

    /// <summary>The glyph's opacity moving between its resting values.</summary>
    public const double TileGlyphFadeBaseMs = 140;

    // ── Arithmetic section 3 implies ────────────────────────────────────────────────

    /// <summary>
    /// When the item at <paramref name="index"/> (0 first) of a staggered group starts, in
    /// milliseconds as it runs on screen.
    /// </summary>
    public static double StaggeredStartMs(double firstStartBaseMs, double staggerBaseMs, int index) =>
        Ms(firstStartBaseMs + staggerBaseMs * index);

    /// <summary>When breakdown row <paramref name="index"/> (0 at the top) starts, on screen.</summary>
    public static double RowStartMs(int index) =>
        StaggeredStartMs(RowFirstStartBaseMs, RowStaggerBaseMs, index);

    /// <summary>When threshold option row <paramref name="index"/> starts, on screen.</summary>
    public static double OptionStartMs(int index) =>
        StaggeredStartMs(OptionFirstStartBaseMs, OptionStaggerBaseMs, index);

    /// <summary>When tile legend entry <paramref name="index"/> starts, on screen.</summary>
    public static double LegendStartMs(int index) =>
        StaggeredStartMs(TileLegendFirstStartBaseMs, TileLegendStaggerBaseMs, index);

    /// <summary>
    /// Whether a transition runs at all. Windows' "animate controls and elements" setting off,
    /// or a zero-length transition, means the end state is applied at once instead.
    /// </summary>
    public static bool Runs(bool animationsEnabled, double durationMs) =>
        animationsEnabled && durationMs > 0;

    /// <summary>
    /// A cubic Bezier easing from (0,0) to (1,1) with control points (x1,y1) and (x2,y2), the
    /// same four numbers a CSS <c>cubic-bezier</c> or a WPF <c>KeySpline</c> takes.
    /// </summary>
    public readonly record struct Curve(double X1, double Y1, double X2, double Y2)
    {
        /// <summary>
        /// Progress at <paramref name="time"/> (0 to 1 of the duration). The curve is
        /// parametric, so this finds the parameter whose x equals the time and returns its y.
        /// </summary>
        public double ValueAt(double time)
        {
            if (time <= 0)
            {
                return 0;
            }

            if (time >= 1)
            {
                return 1;
            }

            // x(s) rises monotonically for control x values within 0..1, so bisection is
            // enough, and exact to far below a pixel of any animation.
            double lo = 0, hi = 1;
            for (var i = 0; i < 48; i++)
            {
                var mid = (lo + hi) / 2;
                if (Bezier(mid, X1, X2) < time)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid;
                }
            }

            return Bezier((lo + hi) / 2, Y1, Y2);
        }

        private static double Bezier(double s, double p1, double p2)
        {
            var u = 1 - s;
            return 3 * u * u * s * p1 + 3 * u * s * s * p2 + s * s * s;
        }
    }
}
