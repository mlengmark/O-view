using OView.App.Rendering;

namespace OView.App.Tests;

/// <summary>
/// The detail panel is <c>SizeToContent</c> and docks to a work-area corner, so a panel taller
/// than the work area has its bottom under the taskbar — <see cref="WorkAreaPlacement"/> pins
/// the top and the rest is unreachable. That was reported after the token explanation gained a
/// disclosure: the panel fitted closed and did not fit open.
///
/// <para>Measured 2026-08-23: 662 DIP closed, 734 open, against roughly 680 DIP of work area
/// on a 1920×1080 display at 150% scaling. Density is the answer — the window still sizes to
/// its content and still expands, the content just packs tighter when it must.</para>
/// </summary>
public class PanelDensityTests
{
    /// <summary>Measured heights of the real panel, so the thresholds are not invented.</summary>
    private const double ClosedDip = 662;

    private const double ExpandedDip = 734;

    private const double CompactExpandedDip = 648;

    // ── choosing ────────────────────────────────────────────────────────────────────

    /// <summary>A display with room gets the shipped layout, untouched.</summary>
    [Fact]
    public void ARoomyDisplayGetsTheNaturalLayout()
    {
        // 2560×1440: 1392 DIP of work area, vastly more than the panel wants.
        Assert.Same(PanelDensity.Normal, PanelDensity.For(ExpandedDip, availableHeightDip: 1368));
    }

    /// <summary>
    /// The reported case: a panel that fitted closed and did not fit open. Density follows the
    /// panel's actual height, not a property of the display, so the same screen gets both
    /// answers depending on whether the explanation is expanded.
    /// </summary>
    [Fact]
    public void AShortDisplayGetsCompactOnlyOnceItActuallyOverflows()
    {
        const double available = 700;

        Assert.Same(PanelDensity.Normal, PanelDensity.For(ClosedDip, available));
        Assert.Same(PanelDensity.Compact, PanelDensity.For(ExpandedDip, available));
    }

    /// <summary>
    /// 1920×1080 at 150% scaling leaves ~680 DIP of work area, and 656 once both flyout
    /// margins come out — less than the 662 the panel wants <i>closed</i>. So that display,
    /// which is a very ordinary Windows laptop, compacts before anything is expanded at all.
    /// The disclosure did not create this; it made an existing overflow reachable.
    /// </summary>
    [Fact]
    public void ACommonScaledLaptopCompactsEvenWithNothingExpanded()
    {
        const double available = 680 - (2 * WorkAreaPlacement.DefaultMarginPx);   // 656

        Assert.True(ClosedDip > available, "the closed panel already overflows this display");
        Assert.Same(PanelDensity.Compact, PanelDensity.For(ClosedDip, available));
    }

    /// <summary>
    /// Exactly fitting is not overflowing. An off-by-one the other way would put every panel
    /// on a display it precisely fits into compact for no reason.
    /// </summary>
    [Fact]
    public void APanelThatExactlyFitsIsNotCompacted()
    {
        Assert.Same(PanelDensity.Normal, PanelDensity.For(700, availableHeightDip: 700));
        Assert.Same(PanelDensity.Compact, PanelDensity.For(701, availableHeightDip: 700));
    }

    // ── what compact actually buys ──────────────────────────────────────────────────

    /// <summary>
    /// The saving has to be enough for the case it exists for. Rendered at 648 against 734,
    /// so compact clears a 680 DIP work area with both margins — the display that prompted
    /// this. A change that quietly reduced the saving would still pass a "compact is smaller"
    /// test and fail the user.
    /// </summary>
    [Fact]
    public void CompactSavesEnoughToClearAShortDisplay()
    {
        Assert.True(CompactExpandedDip + (2 * WorkAreaPlacement.DefaultMarginPx) <= 680,
            "compact must fit a 1080p display at 150% scaling");
        Assert.True(ExpandedDip - CompactExpandedDip >= 80,
            "the saving is what makes this worth doing at all");
    }

    /// <summary>Every dimension tightens or holds; none grows.</summary>
    [Fact]
    public void NothingGetsLargerInCompact()
    {
        var n = PanelDensity.Normal;
        var c = PanelDensity.Compact;

        Assert.True(c.RootPadding <= n.RootPadding);
        Assert.True(c.SeparatorGap <= n.SeparatorGap);
        Assert.True(c.CreditsSeparatorBottom <= n.CreditsSeparatorBottom);
        Assert.True(c.GraphHeight <= n.GraphHeight);
        Assert.True(c.GraphHeadingTop <= n.GraphHeadingTop);
        Assert.True(c.GraphHeadingBottom <= n.GraphHeadingBottom);
        Assert.True(c.TilePaddingX <= n.TilePaddingX);
        Assert.True(c.TilePaddingY <= n.TilePaddingY);
        Assert.True(c.TileGridTop <= n.TileGridTop);
        Assert.True(c.SectionGap <= n.SectionGap);
    }

    /// <summary>
    /// Nothing collapses to nothing. A zero gap is not a denser panel, it is two sections
    /// touching — and a zero graph is a chart with no bars.
    /// </summary>
    [Fact]
    public void CompactKeepsEveryDimensionPositive()
    {
        var c = PanelDensity.Compact;

        Assert.True(c.RootPadding > 0);
        Assert.True(c.SeparatorGap > 0);
        Assert.True(c.CreditsSeparatorBottom > 0);
        Assert.True(c.GraphHeight > 0);
        Assert.True(c.TilePaddingY > 0);
        Assert.True(c.SectionGap > 0);
    }

    /// <summary>
    /// The off-plan rule ships 2 px tighter below than above. Folding that asymmetry away made
    /// every normal-density render 2 DIP taller than the shipped panel — an unrequested visual
    /// change arriving inside a change about short screens.
    /// </summary>
    [Fact]
    public void NormalPreservesTheShippedSeparatorAsymmetry()
    {
        Assert.Equal(12, PanelDensity.Normal.SeparatorGap);
        Assert.Equal(10, PanelDensity.Normal.CreditsSeparatorBottom);
        Assert.NotEqual(PanelDensity.Normal.SeparatorGap, PanelDensity.Normal.CreditsSeparatorBottom);
    }

    // ── the ratios the Linux head uses ──────────────────────────────────────────────

    /// <summary>
    /// Normal must be a no-op for a head that scales its own constants, or a display with
    /// plenty of room would silently get a resized chart on a platform nobody can test.
    /// </summary>
    [Fact]
    public void TheScalesAreExactlyOneAtNormal()
    {
        Assert.Equal(1.0, PanelDensity.Normal.GraphScale);
        Assert.Equal(1.0, PanelDensity.Normal.SpacingScale);
    }

    /// <summary>Compact scales down, and not to nothing — a 60 px strip must stay a strip.</summary>
    [Fact]
    public void TheScalesTightenWithoutCollapsing()
    {
        Assert.InRange(PanelDensity.Compact.GraphScale, 0.5, 1.0);
        Assert.InRange(PanelDensity.Compact.SpacingScale, 0.5, 1.0);

        // The Linux head's own natural values, through the shared ratio.
        Assert.InRange(60 * PanelDensity.Compact.GraphScale, 30, 60);
        Assert.InRange(10 * PanelDensity.Compact.SpacingScale, 5, 10);
    }

    [Fact]
    public void IsCompactIdentifiesTheTwoLayouts()
    {
        Assert.True(PanelDensity.Compact.IsCompact);
        Assert.False(PanelDensity.Normal.IsCompact);
    }
}
