using OView.App.Rendering;

namespace OView.App.Tests;

/// <summary>
/// <see cref="PanelMotion"/> is the one home of the panel's and the menu's motion figures.
/// These tests pin the rules between the figures rather than the figures themselves, so
/// retuning the pace or a single duration does not break them, but a change that makes a
/// transition leave slower than it arrived, or start a row after the fold has ended, does.
/// </summary>
public class PanelMotionTests
{
    // ── leaving is quicker than arriving ────────────────────────────────────────────

    public static TheoryData<string, double, double> InAndOut => new()
    {
        { "surface", PanelMotion.SurfaceOpenBaseMs, PanelMotion.SurfaceCloseBaseMs },
        { "breakdown fold", PanelMotion.FoldOpenBaseMs, PanelMotion.FoldCloseBaseMs },
        { "breakdown rows", PanelMotion.RowFadeBaseMs, PanelMotion.RowsCloseFadeBaseMs },
        { "tick", PanelMotion.TickOnBaseMs, PanelMotion.TickOffBaseMs },
        { "threshold list", PanelMotion.ThresholdOpenBaseMs, PanelMotion.ThresholdCloseBaseMs },
        { "threshold options", PanelMotion.OptionFadeBaseMs, PanelMotion.OptionsCloseFadeBaseMs },
        { "tile face", PanelMotion.TileFaceInBaseMs, PanelMotion.TileFaceOutBaseMs },
    };

    [Theory]
    [MemberData(nameof(InAndOut))]
    public void LeavingIsQuickerThanArriving(string what, double arriving, double leaving)
    {
        Assert.True(leaving < arriving, what);
    }

    [Fact]
    public void AHoverFillArrivesAtOnceAndLingersOnTheWayOut()
    {
        // The one pair that runs the other way round on purpose: a fill that lingers after the
        // pointer has gone reads as a trail, a fill that arrives at once reads as a response.
        Assert.True(PanelMotion.HoverFillInBaseMs < PanelMotion.HoverFillOutBaseMs);
    }

    [Fact]
    public void ALeavingSurfaceTakesAboutTwoThirdsOfAnArrivingOne()
    {
        var ratio = PanelMotion.SurfaceCloseBaseMs / PanelMotion.SurfaceOpenBaseMs;

        Assert.InRange(ratio, 0.6, 0.7);
    }

    // ── the pace ────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryFigureScalesWithThePace()
    {
        Assert.Equal(PanelMotion.SurfaceOpenBaseMs * PanelMotion.Pace, PanelMotion.Ms(PanelMotion.SurfaceOpenBaseMs));
        Assert.Equal(0, PanelMotion.Ms(0));

        // Staggered starts are built from base figures, so they follow the pace too.
        Assert.Equal(
            PanelMotion.Ms(PanelMotion.RowFirstStartBaseMs + PanelMotion.RowStaggerBaseMs * 3),
            PanelMotion.RowStartMs(3));
        Assert.Equal(
            PanelMotion.Ms(PanelMotion.OptionFirstStartBaseMs + PanelMotion.OptionStaggerBaseMs * 2),
            PanelMotion.OptionStartMs(2));
        Assert.Equal(
            PanelMotion.Ms(PanelMotion.TileLegendFirstStartBaseMs + PanelMotion.TileLegendStaggerBaseMs * 4),
            PanelMotion.LegendStartMs(4));
    }

    // ── stagger ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void EachRowStartsLaterThanTheOneAboveIt()
    {
        for (var i = 1; i < PanelMotion.BreakdownRowCount; i++)
        {
            Assert.True(PanelMotion.RowStartMs(i) > PanelMotion.RowStartMs(i - 1), $"row {i}");
        }
    }

    [Fact]
    public void TheFirstRowStartsAfterTheFoldHasBegunAndTheLastBeforeItEnds()
    {
        var fold = PanelMotion.Ms(PanelMotion.FoldOpenBaseMs);

        Assert.True(PanelMotion.RowStartMs(0) > 0);
        Assert.True(PanelMotion.RowStartMs(PanelMotion.BreakdownRowCount - 1) < fold);
    }

    [Fact]
    public void ClosingRowsAreGoneBeforeTheFoldHasFinishedClosing()
    {
        Assert.True(PanelMotion.RowsCloseFadeBaseMs < PanelMotion.FoldCloseBaseMs);
    }

    [Fact]
    public void EachOptionAndLegendEntryStartsLaterThanThePreviousOne()
    {
        for (var i = 1; i < 3; i++)
        {
            Assert.True(PanelMotion.OptionStartMs(i) > PanelMotion.OptionStartMs(i - 1), $"option {i}");
        }

        for (var i = 1; i < 4; i++)
        {
            Assert.True(PanelMotion.LegendStartMs(i) > PanelMotion.LegendStartMs(i - 1), $"legend {i}");
        }
    }

    [Fact]
    public void TheIncomingTileFaceStartsBeforeTheOutgoingOneHasGone()
    {
        // Otherwise the tile is blank for a moment between the two faces.
        Assert.True(PanelMotion.TileFaceInDelayBaseMs < PanelMotion.TileFaceOutBaseMs);
    }

    // ── the two-block panel ─────────────────────────────────────────────────────────

    [Fact]
    public void TheNewStaggeredStartsFollowThePace()
    {
        Assert.Equal(
            PanelMotion.Ms(PanelMotion.ChartBarFirstStartBaseMs + PanelMotion.ChartBarStaggerBaseMs * 7),
            PanelMotion.ChartBarStartMs(7));
        Assert.Equal(
            PanelMotion.Ms(PanelMotion.ChartBarBackFirstStartBaseMs + PanelMotion.ChartBarStaggerBaseMs * 7),
            PanelMotion.ChartBarBackStartMs(7));
        Assert.Equal(
            PanelMotion.Ms(PanelMotion.ModelRowFirstStartBaseMs + PanelMotion.ModelRowStaggerBaseMs * 3),
            PanelMotion.ModelRowStartMs(3));
    }

    [Fact]
    public void ABannerFadesBeforeItsHeightHasFinishedClosing()
    {
        // The words are gone before the space is, so nothing is squeezed while still legible.
        Assert.True(PanelMotion.BannerFadeBaseMs < PanelMotion.BannerFoldBaseMs);
    }

    [Fact]
    public void TheArrivalFillsStartAfterTheSlideHasBegunAndBeforeItEnds()
    {
        var slide = PanelMotion.Ms(PanelMotion.SurfaceOpenBaseMs);

        Assert.True(PanelMotion.Ms(PanelMotion.PlanBarGrowDelayBaseMs) > 0);
        Assert.True(PanelMotion.Ms(PanelMotion.PlanBarGrowDelayBaseMs) < slide);
        Assert.True(PanelMotion.ChartBarStartMs(0) > 0);
        Assert.True(PanelMotion.ChartBarStartMs(30) < slide);
    }

    [Fact]
    public void ThirtyOneChartBarsOverlapRatherThanRiseOneAfterAnother()
    {
        // The last bar starts while the first is still rising, so the chart reads as one wave
        // and not as thirty-one separate events.
        var lastStart = PanelMotion.ChartBarStartMs(30) - PanelMotion.ChartBarStartMs(0);

        Assert.True(lastStart < PanelMotion.Ms(PanelMotion.ChartBarRiseBaseMs));
    }

    [Fact]
    public void EachChartBarAndModelRowStartsLaterThanThePreviousOne()
    {
        for (var i = 1; i < 31; i++)
        {
            Assert.True(PanelMotion.ChartBarStartMs(i) > PanelMotion.ChartBarStartMs(i - 1), $"bar {i}");
            Assert.True(PanelMotion.ChartBarBackStartMs(i) > PanelMotion.ChartBarBackStartMs(i - 1), $"bar back {i}");
        }

        for (var i = 1; i < 10; i++)
        {
            Assert.True(PanelMotion.ModelRowStartMs(i) > PanelMotion.ModelRowStartMs(i - 1), $"model row {i}");
        }
    }

    [Fact]
    public void TheChartRisesBackOnlyAfterTheModelFaceHasBegunLeaving()
    {
        Assert.True(PanelMotion.ChartBarBackFirstStartBaseMs >= PanelMotion.TileFaceOutBaseMs);
    }

    [Fact]
    public void TheDoughnutsSweepBeforeTheirModelRowsFadeIn()
    {
        // The rows name what the ring is showing; they arrive as the sweep reaches them,
        // and the first of them does not wait for the sweep to end.
        Assert.True(PanelMotion.DoughnutSweepDelayBaseMs < PanelMotion.ModelRowFirstStartBaseMs);
        Assert.True(
            PanelMotion.ModelRowFirstStartBaseMs
            < PanelMotion.DoughnutSweepDelayBaseMs + PanelMotion.DoughnutSweepBaseMs);
    }

    [Fact]
    public void OpeningOnTheModelFaceStartsTheSweepAndRowsWithTheSlideFills()
    {
        var slide = PanelMotion.Ms(PanelMotion.SurfaceOpenBaseMs);

        Assert.Equal(
            PanelMotion.Ms(PanelMotion.PlanBarGrowDelayBaseMs),
            PanelMotion.Ms(PanelMotion.DoughnutSweepOpenDelayBaseMs));
        Assert.Equal(
            PanelMotion.Ms(PanelMotion.ModelRowOpenFirstStartBaseMs + PanelMotion.ModelRowStaggerBaseMs * 2),
            PanelMotion.ModelRowOpenStartMs(2));
        Assert.True(PanelMotion.ModelRowOpenStartMs(0) < slide);

        // Opening beats flipping: nothing has to wait for a face to leave.
        Assert.True(PanelMotion.DoughnutSweepOpenDelayBaseMs < PanelMotion.DoughnutSweepDelayBaseMs);
        Assert.True(PanelMotion.ModelRowOpenStartMs(0) < PanelMotion.ModelRowStartMs(0));

        for (var i = 1; i < 10; i++)
        {
            Assert.True(PanelMotion.ModelRowOpenStartMs(i) > PanelMotion.ModelRowOpenStartMs(i - 1), $"row {i}");
        }
    }

    [Fact]
    public void TheModelFaceArrivesOnlyAfterTheTotalsFaceHasBegunLeaving()
    {
        // The flip's incoming face starts at TileFaceInDelayBaseMs, and what is on it starts
        // no earlier, otherwise a ring sweeps over a face that is still fading out.
        Assert.True(PanelMotion.DoughnutSweepDelayBaseMs >= PanelMotion.TileFaceInDelayBaseMs);
        Assert.True(PanelMotion.ModelRowFirstStartBaseMs >= PanelMotion.TileFaceInDelayBaseMs);
    }

    [Fact]
    public void TheHeightOfAFlipTakesLongerThanTheFaceLeaving()
    {
        // The outgoing face is gone before the block has finished changing height.
        Assert.True(PanelMotion.TileFaceOutBaseMs < PanelMotion.BlockFlipHeightBaseMs);
    }

    [Fact]
    public void AChartSeriesSwitchIsQuickerThanABarRisingOnArrival()
    {
        // Bars already on screen only change height; bars arriving come from nothing.
        Assert.True(PanelMotion.ChartSeriesSwitchBaseMs < PanelMotion.ChartBarRiseBaseMs);
    }

    [Fact]
    public void TheOutputOverallTableReusesTheFoldAndTheFlipReusesTheTileFaces()
    {
        // The rules the document gives that table and that flip: a shrink is quicker than a
        // grow, the old rows leave before the new arrive, a face leaves quicker than it
        // arrives. They are carried by the existing figures, so this pins that they stay so.
        Assert.True(PanelMotion.FoldCloseBaseMs < PanelMotion.FoldOpenBaseMs);
        Assert.True(PanelMotion.RowsCloseFadeBaseMs < PanelMotion.RowFadeBaseMs);
        Assert.True(PanelMotion.TileFaceOutBaseMs < PanelMotion.TileFaceInBaseMs);
        Assert.True(PanelMotion.Decelerate.ValueAt(0.25) > PanelMotion.Accelerate.ValueAt(0.25));
    }

    // ── the curves ──────────────────────────────────────────────────────────────────

    public static TheoryData<string> CurveNames => new() { "decelerate", "accelerate" };

    private static PanelMotion.Curve Named(string name) =>
        name == "decelerate" ? PanelMotion.Decelerate : PanelMotion.Accelerate;

    [Theory]
    [MemberData(nameof(CurveNames))]
    public void ACurveStartsAtNothingAndEndsAtEverything(string name)
    {
        var curve = Named(name);

        Assert.Equal(0, curve.ValueAt(0), 9);
        Assert.Equal(1, curve.ValueAt(1), 9);
        Assert.Equal(0, curve.ValueAt(-0.5), 9);
        Assert.Equal(1, curve.ValueAt(1.5), 9);
    }

    [Theory]
    [MemberData(nameof(CurveNames))]
    public void ACurveNeverGoesBackwards(string name)
    {
        var curve = Named(name);
        var previous = 0.0;

        for (var i = 1; i <= 200; i++)
        {
            var value = curve.ValueAt(i / 200.0);
            Assert.True(value >= previous, $"{name} fell at step {i}");
            previous = value;
        }
    }

    [Fact]
    public void TheDecelerateCurveIsFastEarlyAndTheAccelerateCurveIsSlowEarly()
    {
        Assert.True(PanelMotion.Decelerate.ValueAt(0.25) > 0.25);
        Assert.True(PanelMotion.Accelerate.ValueAt(0.25) < 0.25);
    }

    [Fact]
    public void TheDecelerateCurveStillMatchesTheRecordingItWasFittedTo()
    {
        // Progress of Windows' own flyout at 8, 24, 57 and 115 ms of its 230 ms, within the
        // slack the fit itself had.
        AssertNear(0.15, PanelMotion.Decelerate.ValueAt(8 / 230.0));
        AssertNear(0.35, PanelMotion.Decelerate.ValueAt(24 / 230.0));
        AssertNear(0.60, PanelMotion.Decelerate.ValueAt(57 / 230.0));
        AssertNear(0.84, PanelMotion.Decelerate.ValueAt(115 / 230.0));
    }

    private static void AssertNear(double expected, double actual) =>
        Assert.InRange(actual, expected - 0.03, expected + 0.03);

    // ── animations off ──────────────────────────────────────────────────────────────

    [Fact]
    public void NothingRunsWhenWindowsAnimationsAreOff()
    {
        Assert.False(PanelMotion.Runs(false, 460));
        Assert.True(PanelMotion.Runs(true, 460));
    }

    [Fact]
    public void AZeroLengthTransitionDoesNotRun()
    {
        Assert.False(PanelMotion.Runs(true, 0));
    }
}
