using OView.Core.Models;
using OView.Linux.Rendering;

namespace OView.Linux.Tests;

/// <summary>
/// The Skia backend draws the shared measurements in <c>TrayIconGeometry</c>, which have
/// their own tests. What is checked here is that this backend actually applies them —
/// that the arc responds to the percentage, the bands change colour, and the states that
/// must not show a fill do not.
///
/// <para>Pixel-level legibility is a judgement call and cannot be asserted; it is checked
/// by eye from <c>--samples</c> output at the sizes SNI hosts request.</para>
/// </summary>
public class SkiaIconRendererTests
{
    private static UsageSnapshot Live(int percent) =>
        new(DataSource.Live, percent, null, null, DateTimeOffset.UnixEpoch);

    private static byte[] Render(int size, UsageSnapshot snapshot, bool light = false) =>
        SkiaIconRenderer.RenderPng(size, snapshot, light);

    [Theory]
    [InlineData(16)]
    [InlineData(22)]
    [InlineData(24)]
    [InlineData(32)]
    [InlineData(48)]
    public void RendersAValidPngAtEverySizeAHostMightRequest(int size)
    {
        var png = Render(size, Live(47));

        Assert.NotEmpty(png);
        // PNG signature — proves it is an encoded image rather than a buffer of zeroes.
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], png[..4]);
    }

    [Fact]
    public void CommonSizesCoverTheLinuxPanelRangeNotJustTheWindowsOne()
    {
        // Linux hosts commonly ask for 22-24 px, and 48 px on HiDPI. The original
        // legibility evidence only covers 16/20/24, so these sizes must be rendered for it
        // to be checked at all.
        Assert.Contains(22, SkiaIconRenderer.CommonSizes);
        Assert.Contains(48, SkiaIconRenderer.CommonSizes);
    }

    [Fact]
    public void TheArcRespondsToThePercentage()
    {
        // If the sweep were ignored these would be identical, and the gauge would be
        // decoration rather than a reading.
        Assert.NotEqual(Render(24, Live(10)), Render(24, Live(90)));
    }

    [Fact]
    public void BandBoundariesChangeTheRendering()
    {
        Assert.NotEqual(Render(24, Live(49)), Render(24, Live(50)));
        Assert.NotEqual(Render(24, Live(69)), Render(24, Live(70)));
    }

    [Fact]
    public void LightAndDarkPanelsRenderDifferently() =>
        Assert.NotEqual(Render(24, Live(47), light: false), Render(24, Live(47), light: true));

    /// <summary>
    /// CLAUDE.md rule 6. An estimate is a real number, but the icon cannot label it as one —
    /// so it must render exactly like "no data": a neutral empty ring, never a fill that
    /// would present a guess as a measurement.
    /// </summary>
    [Fact]
    public void AnEstimateRendersIdenticallyToNoData()
    {
        var estimate = new UsageSnapshot(DataSource.Estimate, 83, null, null, DateTimeOffset.UnixEpoch);

        Assert.Equal(Render(24, UsageSnapshot.None), Render(24, estimate));
    }

    [Fact]
    public void NoDataDoesNotRenderLikeAHealthyReading()
    {
        // Green would claim "plenty left", which is a statement about usage O-view has not
        // observed. The neutral ring says nothing, which is the point.
        Assert.NotEqual(Render(24, UsageSnapshot.None), Render(24, Live(0)));
    }

    [Fact]
    public void RenderingIsDeterministic() =>
        Assert.Equal(Render(24, Live(47)), Render(24, Live(47)));
}
