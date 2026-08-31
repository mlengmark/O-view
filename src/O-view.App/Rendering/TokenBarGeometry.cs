using OView.Core.Models;

namespace OView.App.Rendering;

/// <summary>
/// One drawn segment of a composition bar: the kind it represents, the figures that are
/// true of it, and the width it is actually drawn at.
/// </summary>
/// <param name="Share">
/// The kind's <b>exact</b> fraction of the window — never re-derived from
/// <paramref name="Width"/>. The width is floored (see <see cref="TokenBarGeometry"/>);
/// the share is not, and every label the panel prints comes from here.
/// </param>
/// <param name="Floored">
/// True when <paramref name="Width"/> is the minimum rather than the proportional width, so
/// a caller can say so rather than leaving the reader to assume the bar is to scale.
/// </param>
public readonly record struct TokenBarSegment(
    TokenKind Kind, long Tokens, double Share, decimal? EstUsd, double Width, bool Floored);

/// <summary>
/// Widths for the two composition bars, computed once for both heads.
///
/// <para>This is here rather than in each panel for the reason <c>TrayIconGeometry</c> is:
/// the segment order and the floor are <b>measured decisions</b>, and two copies of a
/// measured decision is how they drift (issues #55, #56). The WPF panel lays these out with
/// a <c>Grid</c> and the Avalonia panel with a <c>StackPanel</c>; only the numbers are
/// shared.</para>
///
/// <para><b>Why a floor exists at all.</b> Drawn strictly to scale on the 370 px track a
/// 402 px panel affords, a real day's composition gives cache read 327.9 px, cache write
/// 38.1 px, output <b>4.0 px</b> and input <b>0.01 px</b> — input does not render, and
/// output is a hairline. That is the honest width, and it is why the tiles no longer
/// headline this total (issue #253). The bar's job here is to show what else was billed,
/// which it cannot do while two of the four kinds are invisible.</para>
///
/// <para><b>The floor is a stated compromise, not a fix.</b> At 3 px, input is overstated by
/// roughly 300×. The bar therefore stops being strictly proportional, and what makes that
/// safe is that <b>every exact figure is on screen beside it</b> — the legend names each
/// kind with its token count, the breakdown view carries the share to two decimals, and the
/// hover card carries all three plus the estimated value. That is the same relief the
/// per-model tile charts already run on (<c>PanelPalette</c>, series contrast): identity and
/// magnitude never rest on the drawing alone.</para>
/// </summary>
public static class TokenBarGeometry
{
    /// <summary>
    /// Narrowest a present segment may be drawn. Three device-independent pixels is the
    /// smallest block that reads as a block rather than as a border artefact at 100% scale,
    /// and it survives the 125% and 150% scales Windows 11 ships by default.
    /// </summary>
    public const double MinimumSegmentPx = 3;

    /// <summary>
    /// The <see cref="PanelPalette"/> key a kind is drawn in. Here rather than in either
    /// head so the ramp cannot be half-applied on one platform.
    /// </summary>
    public static string PaletteKey(TokenKind kind) => kind switch
    {
        TokenKind.Output => "TokenKindOutput",
        TokenKind.Input => "TokenKindInput",
        TokenKind.CacheWrite => "TokenKindCacheWrite",
        TokenKind.CacheRead => "TokenKindCacheRead",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a token kind."),
    };

    /// <summary>
    /// Segments for one window's composition, in <see cref="TokenComposition.InDisplayOrder"/>
    /// order — output first, at the origin, where a 4 px block is still legible because the
    /// eye starts there.
    ///
    /// <para><b>A kind with no tokens gets no segment.</b> Not a zero-width one: zero and
    /// "floored to 3 px" would be indistinguishable in the output, and a kind that did not
    /// happen is not the same as a kind too small to draw — the distinction the graph already
    /// makes for pre-install days (ADR-0006).</para>
    ///
    /// <para>The last present segment takes whatever the floors left rather than its own
    /// proportional width, so the segments always sum to exactly
    /// <paramref name="trackWidth"/>. Rounding four independent widths and hoping is how a
    /// 1 px gap appears at one DPI scale and not another.</para>
    /// </summary>
    public static IReadOnlyList<TokenBarSegment> Segments(
        TokenComposition composition, double trackWidth)
    {
        if (!composition.HasTokens || trackWidth <= 0)
        {
            return [];
        }

        var present = composition.InDisplayOrder.Where(s => s.Tokens > 0).ToList();

        // On a track too narrow to afford the floors they are dropped and the bar goes
        // strictly proportional. Overflowing instead would push the bar past the panel it
        // sits in — a layout break rather than a legibility compromise — and at a width where
        // three minimum segments do not fit, nothing on the bar is legible anyway.
        var segments = Lay(present, trackWidth, withFloors: true);
        if (segments.Count > 1 && segments[^1].Width <= 0)
        {
            segments = Lay(present, trackWidth, withFloors: false);
        }

        return segments;
    }

    private static List<TokenBarSegment> Lay(
        List<TokenKindSlice> present, double trackWidth, bool withFloors)
    {
        var segments = new List<TokenBarSegment>(present.Count);
        var consumed = 0d;

        for (var i = 0; i < present.Count; i++)
        {
            var slice = present[i];

            double width;
            bool floored;

            if (i == present.Count - 1)
            {
                // The remainder, so the row fills the track exactly rather than leaving a gap
                // that appears at one DPI scale and not another.
                width = trackWidth - consumed;
                floored = false;
            }
            else
            {
                var proportional = slice.Share * trackWidth;
                floored = withFloors && proportional < MinimumSegmentPx;
                width = floored ? MinimumSegmentPx : proportional;
            }

            consumed += width;
            segments.Add(new TokenBarSegment(
                slice.Kind, slice.Tokens, slice.Share, slice.EstUsd, width, floored));
        }

        return segments;
    }
}
