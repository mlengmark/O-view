using OView.App.Rendering;

namespace OView.App.Tests;

/// <summary>
/// <see cref="PanelFit"/> is the shared answer to "how tall may the window be, and must its
/// content scroll" — the question each head used to ask the work area alone and stop at,
/// which on a 1024×768 desktop left the Linux panel running off the bottom of the screen
/// (Maximilian's B13 report on OVIAA-7, VM run 2026-10-15 16:05–16:25 CEST).
/// </summary>
public class PanelFitTests
{
    // ── the basic contract ──────────────────────────────────────────────────────────

    [Fact]
    public void ContentThatFitsIsNotScrolled()
    {
        var result = PanelFit.Resolve(contentHeightDip: 600, availableHeightDip: 700);

        Assert.Equal(600, result.WindowHeightDip);
        Assert.False(result.MustScroll);
    }

    [Fact]
    public void ContentTallerThanTheWorkAreaIsCappedAndScrolled()
    {
        var result = PanelFit.Resolve(contentHeightDip: 900, availableHeightDip: 700);

        Assert.Equal(700, result.WindowHeightDip);
        Assert.True(result.MustScroll);
    }

    /// <summary>Exactly fitting is not scrolling, matching <see cref="PanelDensity.For"/>'s
    /// own exact-fit rule.</summary>
    [Fact]
    public void ExactlyFittingContentDoesNotScroll()
    {
        Assert.False(PanelFit.Resolve(700, availableHeightDip: 700).MustScroll);
        Assert.True(PanelFit.Resolve(701, availableHeightDip: 700).MustScroll);
    }

    /// <summary>The one invariant the whole slice exists for: the window never grows past
    /// what the work area leaves, no matter how tall the content wants to be.</summary>
    [Theory]
    [InlineData(700, 10_000)]
    [InlineData(1_200, 10_000)]
    [InlineData(50, 10_000)]
    public void TheWindowNeverExceedsTheAvailableHeight(double availableHeightDip, double contentHeightDip)
    {
        var result = PanelFit.Resolve(contentHeightDip, availableHeightDip);

        Assert.True(result.WindowHeightDip <= availableHeightDip);
    }

    // ── the real panels, against the work areas that matter ────────────────────────
    //
    // Content heights measured from the goldens on 2026-10-15: Linux 710-1008 DIP, Windows
    // 696-906 DIP (Windows renders at scale 2; these are already converted to DIP). Work
    // areas: 712 DIP (a 1024x768 desktop — Maximilian's B13 run), 830 DIP (1080p at 125%)
    // and 1032 DIP (1080p at 100%). PanelDensity.Compact saves about 94 DIP off the natural
    // height measured here; applying that approximation is what turns "the content is this
    // tall" into "the content is still this tall once each head has already compacted" —
    // the fact the two head slices (18, 19) build their window sizing on.

    private const double CompactSavingDip = 94;

    public static TheoryData<string, double, double, double, bool> RealPanelsAfterCompacting => new()
    {
        // panel, contentHeightDip, availableHeightDip, expectedWindowHeightDip, expectedMustScroll

        // 712 DIP work area (1024x768 desktop): both panels' tallest states still overflow
        // even compacted; both panels' shortest states fit.
        { "Linux shortest", 710, 712, 710 - CompactSavingDip, false },
        { "Linux tallest", 1008, 712, 712, true },
        { "Windows shortest", 696, 712, 696 - CompactSavingDip, false },
        { "Windows tallest", 906, 712, 712, true },

        // 830 DIP work area (1080p at 125%): Linux's tallest state still overflows compacted;
        // everything else now fits.
        { "Linux shortest", 710, 830, 710 - CompactSavingDip, false },
        { "Linux tallest", 1008, 830, 830, true },
        { "Windows shortest", 696, 830, 696 - CompactSavingDip, false },
        { "Windows tallest", 906, 830, 906 - CompactSavingDip, false },

        // 1032 DIP work area (1080p at 100%): everything fits compacted, nothing scrolls.
        { "Linux shortest", 710, 1032, 710 - CompactSavingDip, false },
        { "Linux tallest", 1008, 1032, 1008 - CompactSavingDip, false },
        { "Windows shortest", 696, 1032, 696 - CompactSavingDip, false },
        { "Windows tallest", 906, 1032, 906 - CompactSavingDip, false },
    };

    [Theory]
    [MemberData(nameof(RealPanelsAfterCompacting))]
    public void StatesThatStillOverflowAfterCompactingAreTheOnesThatScroll(
        string panel, double contentHeightDip, double availableHeightDip,
        double expectedWindowHeightDip, bool expectedMustScroll)
    {
        var result = PanelFit.Resolve(contentHeightDip - CompactSavingDip, availableHeightDip);

        Assert.Equal(expectedWindowHeightDip, result.WindowHeightDip);
        Assert.True(expectedMustScroll == result.MustScroll, panel);
    }
}
