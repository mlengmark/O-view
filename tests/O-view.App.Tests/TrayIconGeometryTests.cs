using OView.App.Rendering;
using OView.Core.Models;

namespace OView.App.Tests;

/// <summary>
/// The tray mark's measured decisions. These are not arbitrary numbers to be tidied — the
/// ring/pupil ratios and the stroke width came out of a legibility spike
/// (docs/findings/tray-icon-rendering.md) and two GitHub issues, and they are now shared by
/// two drawing backends that must not drift apart (ADR-0013).
/// </summary>
public class TrayIconGeometryTests
{
    private static UsageSnapshot Live(int percent) =>
        new(DataSource.Live, percent, null, null, DateTimeOffset.UnixEpoch);

    [Theory]
    [InlineData(16, 16 / 7f)]
    [InlineData(24, 24 / 7f)]
    [InlineData(32, 32 / 7f)]
    [InlineData(48, 48 / 7f)]
    public void StrokeIsOneSeventhOfTheIcon(int size, float expected) =>
        Assert.Equal(expected, TrayIconGeometry.Stroke(size), 4);

    [Fact]
    public void StrokeIsFlooredSoItStaysVisibleOnTinyIcons()
    {
        // size/7 would be under 2 px here, which disappears.
        Assert.Equal(2f, TrayIconGeometry.Stroke(8), 4);
        Assert.Equal(2f, TrayIconGeometry.Stroke(12), 4);
    }

    /// <summary>
    /// The ring must sit fully inside the canvas. A stroke centred on the edge would be
    /// clipped in half, which is what the half-pixel in the inset is for.
    /// </summary>
    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    [InlineData(48)]
    [InlineData(256)]
    public void RingFitsWithinTheCanvasAtEverySize(int size)
    {
        var inset = TrayIconGeometry.Inset(size);
        var outerEdge = inset + TrayIconGeometry.RingDiameter(size) + (TrayIconGeometry.Stroke(size) / 2f);

        Assert.True(inset - (TrayIconGeometry.Stroke(size) / 2f) >= 0f, "ring overflows the left/top edge");
        Assert.True(outerEdge <= size, $"ring overflows the right/bottom edge at {size}px");
    }

    /// <summary>
    /// The pupil lives in the ring's hole and must not touch the stroke — that collision is
    /// exactly what made "ring plus digits" unreadable at 16 px, and the pupil only works
    /// because it clears the ring at every size.
    /// </summary>
    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    [InlineData(48)]
    [InlineData(256)]
    public void PupilClearsTheRingStrokeAtEverySize(int size)
    {
        var holeRadius = TrayIconGeometry.RingRadius(size) - (TrayIconGeometry.Stroke(size) / 2f);

        Assert.True(
            TrayIconGeometry.PupilRadius(size) < holeRadius,
            $"pupil collides with the ring at {size}px");
    }

    [Fact]
    public void PupilKeepsTheMasterMarkRatio()
    {
        // 0.405x the ring radius — from the master mark (pupil 30 / ring 74 at 256 px), so
        // the tray icon and the exe icon are the same shape at every scale.
        const int size = 256;
        Assert.Equal(0.405f, TrayIconGeometry.PupilRadius(size) / TrayIconGeometry.RingRadius(size), 4);
    }

    [Fact]
    public void ArcStartsAtTwelveOClock() =>
        Assert.Equal(-90f, TrayIconGeometry.StartAngleDegrees);

    [Theory]
    [InlineData(0, 0f)]
    [InlineData(25, 90f)]
    [InlineData(50, 180f)]
    [InlineData(100, 360f)]
    public void SweepIsProportionalToThePercentage(int percent, float degrees) =>
        Assert.Equal(degrees, TrayIconGeometry.SweepDegrees(percent), 4);

    // ── what may be drawn at all ────────────────────────────────────────────────────

    [Fact]
    public void LiveAndStaleDataAreDrawn()
    {
        Assert.Equal(47, TrayIconGeometry.FillPercent(Live(47)));
        Assert.Equal(47, TrayIconGeometry.FillPercent(
            new UsageSnapshot(DataSource.Stale, 47, null, null, DateTimeOffset.UnixEpoch)));
    }

    /// <summary>
    /// CLAUDE.md rule 6. A JSONL-derived estimate is a real number, but the icon has no room
    /// to label it "estimate" — so drawing a fill would present a guess as a measurement.
    /// The neutral empty ring is the honest rendering.
    /// </summary>
    [Fact]
    public void AnEstimateIsNotDrawnAsAFill()
    {
        var estimate = new UsageSnapshot(DataSource.Estimate, 83, null, null, DateTimeOffset.UnixEpoch);

        Assert.Null(TrayIconGeometry.FillPercent(estimate));
    }

    [Fact]
    public void NoDataIsNotDrawnAsAFill() =>
        Assert.Null(TrayIconGeometry.FillPercent(UsageSnapshot.None));

    [Fact]
    public void PercentagesAreClampedRatherThanOverflowingTheRing()
    {
        Assert.Equal(100, TrayIconGeometry.FillPercent(Live(140)));
        Assert.Equal(0, TrayIconGeometry.FillPercent(Live(-5)));
    }

    // ── colour ──────────────────────────────────────────────────────────────────────

    /// <summary>Bands must match <see cref="UsageLevels"/> exactly — one definition, two surfaces.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(49)]
    [InlineData(50)]
    [InlineData(69)]
    [InlineData(70)]
    [InlineData(100)]
    public void ColourFollowsTheSharedBands(int percent)
    {
        var expected = TrayIconGeometry.LevelColor(UsageLevels.Classify(percent), lightTaskbar: false);

        Assert.Equal(expected, TrayIconGeometry.Foreground(Live(percent), lightTaskbar: false));
    }

    [Fact]
    public void BandBoundariesActuallyChangeColour()
    {
        var dark = false;
        Assert.NotEqual(TrayIconGeometry.Foreground(Live(49), dark), TrayIconGeometry.Foreground(Live(50), dark));
        Assert.NotEqual(TrayIconGeometry.Foreground(Live(69), dark), TrayIconGeometry.Foreground(Live(70), dark));
    }

    [Fact]
    public void UnknownDataTakesTheNeutralColourNotAGreenOne()
    {
        var neutral = TrayIconGeometry.Foreground(UsageSnapshot.None, lightTaskbar: false);

        // Green would read as "plenty left", which is a claim about usage O-view has not
        // observed. Neutral says nothing, which is the point.
        Assert.NotEqual(TrayIconGeometry.Foreground(Live(0), lightTaskbar: false), neutral);
    }

    [Fact]
    public void LightTaskbarUsesDarkerShades()
    {
        foreach (var percent in new[] { 10, 60, 90 })
        {
            var onDark = TrayIconGeometry.Foreground(Live(percent), lightTaskbar: false);
            var onLight = TrayIconGeometry.Foreground(Live(percent), lightTaskbar: true);

            Assert.NotEqual(onDark, onLight);
            Assert.True(
                onLight.R + onLight.G + onLight.B < onDark.R + onDark.G + onDark.B,
                $"the light-taskbar shade should be darker at {percent}%");
        }
    }

    [Fact]
    public void TrackIsFaintSoAnEmptyGaugeStillReadsAsAGauge()
    {
        Assert.Equal(90, TrayIconGeometry.Track(lightTaskbar: false).A);
        Assert.Equal(90, TrayIconGeometry.Track(lightTaskbar: true).A);
    }
}
