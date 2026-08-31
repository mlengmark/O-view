using OView.App.Rendering;
using OView.Core.Models;

namespace OView.App.Tests;

/// <summary>
/// The composition bar's widths (GitHub issue #253).
///
/// <para>These exist because the bar is the one place in the panel that is deliberately
/// <b>not</b> to scale. Everything else refuses to draw a figure it cannot stand behind; this
/// floors two segments so they are visible at all, and what makes that safe is the legend and
/// the cards carrying the exact numbers. The floor is therefore a decision with a stated
/// justification, and it is pinned here so it cannot be quietly tuned into something else.</para>
/// </summary>
public class TokenBarGeometryTests
{
    /// <summary>
    /// One real UTC day from the dev machine, de-duplicated by request id: 446,148 tokens,
    /// 398,121 of them cache reads. The same fixture the Core tests measure.
    /// </summary>
    private static readonly TokenComposition MeasuredDay = new(
        Input: 14, CacheCreation: 44_347, CacheRead: 398_121, Output: 3_666);

    /// <summary>The track a 400 px panel affords at the shipped padding.</summary>
    private const double Track = 366;

    [Fact]
    public void SegmentsRunInDisplayOrderWithOutputAtTheOrigin()
    {
        var segments = TokenBarGeometry.Segments(MeasuredDay, Track);

        Assert.Equal(
            [TokenKind.Output, TokenKind.Input, TokenKind.CacheWrite, TokenKind.CacheRead],
            segments.Select(s => s.Kind));
    }

    /// <summary>
    /// The measurement the design rests on. Drawn strictly to scale, output is a hairline and
    /// input does not render at all — which is why the tiles headline output instead of this
    /// total, and why the floor exists.
    /// </summary>
    [Fact]
    public void WithoutTheFloorOutputIsAHairlineAndInputIsInvisible()
    {
        Assert.Equal(3.0, MeasuredDay.ShareOf(MeasuredDay.Output) * Track, 1);
        Assert.Equal(0.01, MeasuredDay.ShareOf(MeasuredDay.Input) * Track, 2);
    }

    /// <summary>
    /// The floor applies, and <b>says it applied</b>. A caller that cannot tell a floored
    /// segment from a proportional one cannot caveat it.
    /// </summary>
    [Fact]
    public void SubPixelSegmentsAreFlooredAndFlagged()
    {
        var segments = TokenBarGeometry.Segments(MeasuredDay, Track);

        var input = segments.Single(s => s.Kind == TokenKind.Input);
        Assert.Equal(TokenBarGeometry.MinimumSegmentPx, input.Width);
        Assert.True(input.Floored);

        // Output clears the floor on its own at this width, so it must NOT be flagged —
        // a flag that is always on says nothing.
        var output = segments.Single(s => s.Kind == TokenKind.Output);
        Assert.False(output.Floored);
        Assert.True(output.Width > TokenBarGeometry.MinimumSegmentPx);
    }

    /// <summary>
    /// The share is the truth and the width is the drawing. A caller reading the share back
    /// off the width would print "0.82%" for a kind that is 0.003% of the window.
    /// </summary>
    [Fact]
    public void AFlooredSegmentKeepsItsExactShareNotItsDrawnOne()
    {
        var input = TokenBarGeometry.Segments(MeasuredDay, Track)
            .Single(s => s.Kind == TokenKind.Input);

        Assert.Equal(MeasuredDay.ShareOf(MeasuredDay.Input), input.Share);
        Assert.NotEqual(input.Width / Track, input.Share, 4);
    }

    /// <summary>
    /// The segments fill the track exactly, at every width. Rounding four independent widths
    /// and hoping is how a 1 px gap appears at one DPI scale and not another.
    /// </summary>
    [Theory]
    [InlineData(120)]
    [InlineData(366)]
    [InlineData(370)]
    [InlineData(1024)]
    public void SegmentsAlwaysSumToTheTrack(double width)
    {
        var segments = TokenBarGeometry.Segments(MeasuredDay, width);

        Assert.Equal(width, segments.Sum(s => s.Width), 6);
    }

    /// <summary>
    /// A kind that did not happen gets no segment — not a zero-width one, which would be
    /// indistinguishable from a floored sliver. The same line the graph draws for
    /// pre-install days (ADR-0006).
    /// </summary>
    [Fact]
    public void AKindWithNoTokensIsAbsentRatherThanZeroWidth()
    {
        var outputOnly = new TokenComposition(Input: 0, CacheCreation: 0, CacheRead: 0, Output: 500);

        var segments = TokenBarGeometry.Segments(outputOnly, Track);

        Assert.Equal([TokenKind.Output], segments.Select(s => s.Kind));
        Assert.Equal(Track, segments[0].Width);
    }

    /// <summary>Nothing to draw yields nothing, rather than a track full of zero-width marks.</summary>
    [Theory]
    [InlineData(366)]
    [InlineData(0)]
    public void AnEmptyCompositionDrawsNoSegments(double width)
    {
        Assert.Empty(TokenBarGeometry.Segments(TokenComposition.Empty, width));
    }

    /// <summary>
    /// A track too narrow to afford the floors drops them rather than overflowing. Keeping
    /// them would push the bar past the panel it sits in, which is a layout break rather than
    /// a legibility compromise — and at this width nothing on the bar is legible either way.
    /// </summary>
    [Fact]
    public void ADegeneratelyNarrowTrackGoesProportionalRatherThanOverflowing()
    {
        var segments = TokenBarGeometry.Segments(MeasuredDay, 4);

        Assert.All(segments, s => Assert.True(s.Width >= 0));
        Assert.Equal(4, segments.Sum(s => s.Width), 6);
        Assert.DoesNotContain(segments, s => s.Floored);
    }

    /// <summary>Every kind has a palette key, so a new kind cannot render as transparent.</summary>
    [Theory]
    [InlineData(TokenKind.Output)]
    [InlineData(TokenKind.Input)]
    [InlineData(TokenKind.CacheWrite)]
    [InlineData(TokenKind.CacheRead)]
    public void EveryKindResolvesToAPaletteColourInBothThemes(TokenKind kind)
    {
        var key = TokenBarGeometry.PaletteKey(kind);

        Assert.NotEmpty(PanelPalette.Get(key, light: true));
        Assert.NotEmpty(PanelPalette.Get(key, light: false));
    }

    /// <summary>
    /// The ramp is achromatic on purpose: the categorical trio means "which model", panel-wide,
    /// and reusing those hues here would put blue on Opus 5 in a flipped tile and on cache read
    /// in the bar beneath it. A grey has equal channels — that is the whole check.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheKindRampCarriesNoHue(bool light)
    {
        foreach (var kind in Enum.GetValues<TokenKind>())
        {
            var hex = PanelPalette.Get(TokenBarGeometry.PaletteKey(kind), light);
            var r = Convert.ToInt32(hex.Substring(1, 2), 16);
            var g = Convert.ToInt32(hex.Substring(3, 2), 16);
            var b = Convert.ToInt32(hex.Substring(5, 2), 16);

            Assert.Equal(r, g);
            Assert.Equal(g, b);
        }
    }

    /// <summary>
    /// Brightness is the encoding, ordered by how much the reader cares — so the steps must be
    /// strictly separated, and in that order. Two kinds landing on the same grey would make the
    /// bar unreadable while every test above still passed.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheRampStepsAwayFromTheSurfaceInOrder(bool light)
    {
        var steps = Enum.GetValues<TokenKind>()
            .Select(k => Convert.ToInt32(
                PanelPalette.Get(TokenBarGeometry.PaletteKey(k), light).Substring(1, 2), 16))
            .ToList();

        // Output is the loudest step in both themes: darkest on light, lightest on dark.
        Assert.Equal(light ? steps.Min() : steps.Max(), steps[0]);

        // And every step moves the same way, with a real gap between them.
        for (var i = 1; i < steps.Count; i++)
        {
            var gap = light ? steps[i] - steps[i - 1] : steps[i - 1] - steps[i];
            Assert.True(gap >= 24, $"steps {i - 1} and {i} are {gap} apart");
        }
    }
}
