using OView.Core.Providers.PlanHistory;

namespace OView.Core.Tests;

public class WeeklyResetDetectorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 14, 0, 0, 0, TimeSpan.Zero);
    private const string Org = "org";

    private static PlanHistorySample At(TimeSpan offset, int sd) => new(T0 + offset, Org, 0, sd);

    private static WeeklyResetObservation Precise(DateTimeOffset at) =>
        new(at - TimeSpan.FromMinutes(5), at, Org);

    private static WeeklyResetObservation Bracketed(DateTimeOffset at, TimeSpan width) =>
        new(at - width, at, Org);

    // ── detection ──────────────────────────────────────────────────────────────

    [Fact]
    public void InCadenceDrop_IsAPreciseObservation()
    {
        var samples = new[] { At(TimeSpan.Zero, 9), At(TimeSpan.FromMinutes(5), 0) };

        var observation = Assert.Single(WeeklyResetDetector.FindResets(samples));

        Assert.Equal(T0, observation.EarliestUtc);
        Assert.Equal(T0.AddMinutes(5), observation.LatestUtc);
        Assert.True(observation.IsPrecise);
    }

    [Fact]
    public void DropAcrossADesktopGap_IsObserved_ButBracketedByTheGap()
    {
        // The real 2026-07-28 case: sd 70% -> 0% across a 10-hour Desktop-closed gap. The
        // previous detector discarded this as a suspected restart snap, which is why no
        // weekly reset was ever derived — both of the dev machine's resets look like this.
        var samples = new[] { At(TimeSpan.Zero, 70), At(TimeSpan.FromHours(10), 0) };

        var observation = Assert.Single(WeeklyResetDetector.FindResets(samples));

        Assert.Equal(TimeSpan.FromHours(10), observation.Uncertainty);
        Assert.False(observation.IsPrecise);
    }

    [Fact]
    public void SinglePointWobble_IsNotAReset()
    {
        var samples = new[] { At(TimeSpan.Zero, 9), At(TimeSpan.FromMinutes(5), 8) };

        Assert.Empty(WeeklyResetDetector.FindResets(samples));
    }

    [Fact]
    public void RisingUsage_IsNeverAReset()
    {
        var samples = new[] { At(TimeSpan.Zero, 2), At(TimeSpan.FromMinutes(5), 20), At(TimeSpan.FromMinutes(10), 70) };

        Assert.Empty(WeeklyResetDetector.FindResets(samples));
    }

    [Fact]
    public void ObservationsCarryTheirOrg()
    {
        var samples = new[]
        {
            new PlanHistorySample(T0, "org-a", 0, 9),
            new PlanHistorySample(T0.AddMinutes(5), "org-a", 0, 0),
        };

        Assert.Equal("org-a", WeeklyResetDetector.FindResets(samples)[0].OrgUuid);
    }

    // ── prediction ─────────────────────────────────────────────────────────────

    [Fact]
    public void NoObservations_YieldsNoPrediction()
    {
        Assert.Null(WeeklyResetDetector.PredictNextReset([], T0));
    }

    [Fact]
    public void OneObservationIsEnough_ThePeriodIsMeasuredNotDerivedPerUser()
    {
        // The behaviour issue #6 actually asked for: the panel fills in on the FIRST reset
        // seen, not the second. Requiring two meant a fortnight's wait after install.
        var forecast = WeeklyResetDetector.PredictNextReset([Precise(T0)], T0.AddHours(1));

        Assert.NotNull(forecast);
        Assert.Equal(T0.AddDays(7), forecast.AtUtc);
    }

    [Fact]
    public void PredictionRollsForwardAcrossMissedWindows()
    {
        var forecast = WeeklyResetDetector.PredictNextReset([Precise(T0)], T0.AddDays(20));

        Assert.NotNull(forecast);
        Assert.Equal(T0.AddDays(21), forecast.AtUtc);
    }

    [Fact]
    public void PredictionIsStrictlyAfterNow_EvenOnTheBoundary()
    {
        var forecast = WeeklyResetDetector.PredictNextReset([Precise(T0)], T0.AddDays(7));

        Assert.NotNull(forecast);
        Assert.Equal(T0.AddDays(14), forecast.AtUtc);
    }

    [Fact]
    public void TheMostPreciseObservationAnchors_NotTheMostRecent()
    {
        // A tight bracket from three weeks ago beats yesterday's ten-hour one: the period
        // is exact, so projecting the precise anchor forward costs nothing and keeps the
        // predicted minute rather than inheriting the gap's slop.
        var precise = Precise(T0);
        var vague = Bracketed(T0.AddDays(21).AddHours(9), TimeSpan.FromHours(10));

        var forecast = WeeklyResetDetector.PredictNextReset([precise, vague], T0.AddDays(22));

        Assert.NotNull(forecast);
        Assert.Equal(T0.AddDays(28), forecast.AtUtc);
        Assert.True(forecast.IsPrecise);
    }

    [Fact]
    public void ForecastReportsTheAnchorsUncertainty()
    {
        var forecast = WeeklyResetDetector.PredictNextReset(
            [Bracketed(T0, TimeSpan.FromHours(10))], T0.AddHours(1));

        Assert.NotNull(forecast);
        Assert.Equal(TimeSpan.FromHours(10), forecast.Uncertainty);
        Assert.False(forecast.IsPrecise);
    }

    // ── the period constant is checked against precise pairs, not trusted blindly ──

    [Fact]
    public void TwoPreciseObservationsAgreeingWithSevenDays_KeepTheConstant()
    {
        var first = Precise(T0);
        var second = Precise(T0.AddDays(7).AddMinutes(4));   // sampling jitter, not drift

        var forecast = WeeklyResetDetector.PredictNextReset([first, second], T0.AddDays(8));

        Assert.NotNull(forecast);
        // Equally precise, so the newer one anchors — least accumulated drift — and the
        // step is the constant 7 days, not the 7 d 4 m the pair happens to measure.
        Assert.Equal(second.LatestUtc.AddDays(7), forecast.AtUtc);
    }

    [Fact]
    public void AMissedResetDoesNotLookLikeADifferentPeriod()
    {
        // Two precise observations a fortnight apart are consistent with a 7-day window
        // and one reset unobserved — not evidence of a 14-day window.
        var first = Precise(T0);
        var second = Precise(T0.AddDays(14));

        var forecast = WeeklyResetDetector.PredictNextReset([first, second], T0.AddDays(15));

        Assert.NotNull(forecast);
        Assert.Equal(T0.AddDays(21), forecast.AtUtc);
    }

    [Fact]
    public void TwoPreciseObservationsContradictingSevenDays_WinOverTheConstant()
    {
        // If Anthropic ever changes the window, measurement on the user's own machine has
        // to beat a constant measured on ours.
        var first = Precise(T0);
        var second = Precise(T0.AddHours(72));

        var forecast = WeeklyResetDetector.PredictNextReset([first, second], T0.AddHours(73));

        Assert.NotNull(forecast);
        Assert.Equal(second.LatestUtc.AddHours(72), forecast.AtUtc);
    }

    [Fact]
    public void ImpreciseObservationsNeverMeasureThePeriod()
    {
        // Two ten-hour brackets cannot tell 7 d from 7 d 10 h apart, so they must not be
        // allowed to overwrite the constant with their own slop.
        var first = Bracketed(T0, TimeSpan.FromHours(10));
        var second = Bracketed(T0.AddDays(7).AddHours(6), TimeSpan.FromHours(10));

        var forecast = WeeklyResetDetector.PredictNextReset([first, second], T0.AddDays(8));

        Assert.NotNull(forecast);
        // Anchored on the tighter-or-equal, most recent bracket, stepped by exactly 7 days.
        Assert.Equal(second.LatestUtc.AddDays(7), forecast.AtUtc);
    }

    // ── past boundaries, for the 31-day graph's week gridlines ─────────────────

    [Fact]
    public void BoundariesAreDerivedBackwards_AcrossTheWholeWindow()
    {
        // The log only holds resets O-view was running for, but the graph covers 31 days —
        // so past boundaries are stepped back from the prediction, not looked up.
        var next = T0.AddDays(7);

        var boundaries = WeeklyResetDetector.BoundariesWithin(
            next, TimeSpan.FromDays(7), T0.AddDays(-30), T0);

        Assert.Equal(
            [T0.AddDays(-28), T0.AddDays(-21), T0.AddDays(-14), T0.AddDays(-7), T0],
            boundaries);
    }

    [Fact]
    public void BoundariesKeepTheResetsTimeOfDay()
    {
        // The gridline's whole point is that a plan week does not start at midnight.
        var next = new DateTimeOffset(2026, 8, 4, 6, 28, 57, TimeSpan.Zero);

        var boundaries = WeeklyResetDetector.BoundariesWithin(
            next, TimeSpan.FromDays(7), next.AddDays(-21), next.AddDays(-1));

        Assert.All(boundaries, b => Assert.Equal(new TimeSpan(6, 28, 57), b.UtcDateTime.TimeOfDay));
        Assert.Equal(next.AddDays(-7), boundaries[^1]);
    }

    [Fact]
    public void BoundariesRespectAMeasuredPeriod_NotTheConstant()
    {
        var next = T0.AddHours(72);

        var boundaries = WeeklyResetDetector.BoundariesWithin(
            next, TimeSpan.FromHours(72), T0.AddHours(-144), T0);

        Assert.Equal([T0.AddHours(-144), T0.AddHours(-72), T0], boundaries);
    }

    [Fact]
    public void APredictionFarBeyondTheWindow_StillYieldsTheWindowsBoundaries()
    {
        // The panel can be opened after a long idle stretch, when the next reset has rolled
        // forward well past the plotted range.
        var next = T0.AddDays(70);

        var boundaries = WeeklyResetDetector.BoundariesWithin(
            next, TimeSpan.FromDays(7), T0.AddDays(-10), T0);

        Assert.Equal([T0.AddDays(-7), T0], boundaries);
    }

    [Fact]
    public void DegenerateInputsYieldNoBoundaries()
    {
        Assert.Empty(WeeklyResetDetector.BoundariesWithin(T0, TimeSpan.Zero, T0.AddDays(-7), T0));
        Assert.Empty(WeeklyResetDetector.BoundariesWithin(T0, TimeSpan.FromDays(7), T0, T0.AddDays(-7)));
    }

    [Fact]
    public void ForecastCarriesThePeriodItSteppedBy()
    {
        // So the graph's gridlines and the countdown can never step by different cadences.
        var sevenDay = WeeklyResetDetector.PredictNextReset([Precise(T0)], T0.AddHours(1));
        Assert.Equal(TimeSpan.FromDays(7), sevenDay!.Period);

        var measured = WeeklyResetDetector.PredictNextReset(
            [Precise(T0), Precise(T0.AddHours(72))], T0.AddHours(73));
        Assert.Equal(TimeSpan.FromHours(72), measured!.Period);
    }

    // ── the measured evidence behind the 7-day constant ────────────────────────

    [Fact]
    public void RealDevMachineSeries_DerivesASevenDayWindow()
    {
        // Reconstructed from %APPDATA%\Claude\plan-usage-history.json as observed
        // 2026-07-28: sd climbs 2 -> 70 over seven days with exactly two drops, both across
        // overnight gaps, 7 d 0 h 14 m apart. Sampling continues through 2026-07-24, where a
        // 72-hour window would have produced a third drop and does not — which is what rules
        // that alternative out. See docs/findings/plan-usage-history.md.
        var samples = new List<PlanHistorySample>();
        void Sample(string utc, int sd) =>
            samples.Add(new PlanHistorySample(DateTimeOffset.Parse(utc, null,
                System.Globalization.DateTimeStyles.AssumeUniversal |
                System.Globalization.DateTimeStyles.AdjustToUniversal), Org, 0, sd));

        Sample("2026-07-20T20:56:00Z", 9);
        Sample("2026-07-21T06:14:55Z", 0);      // reset 1, across a 9 h 19 m gap
        Sample("2026-07-24T06:00:02Z", 35);     // in cadence, and climbing — no 72 h reset
        Sample("2026-07-24T06:05:02Z", 35);
        Sample("2026-07-27T20:22:20Z", 70);
        Sample("2026-07-28T06:28:57Z", 0);      // reset 2, across a 10 h 07 m gap

        var observations = WeeklyResetDetector.FindResets(samples);

        Assert.Equal(2, observations.Count);
        Assert.All(observations, o => Assert.False(o.IsPrecise));

        // 7 d 0 h 14 m apart, and the 14 m is smaller than either bracket — consistent with
        // a 7-day window and inconsistent with nothing else the file contains.
        var apart = observations[1].LatestUtc - observations[0].LatestUtc;
        Assert.True((apart - TimeSpan.FromDays(7)).Duration() < TimeSpan.FromMinutes(20), $"{apart}");
        Assert.True(apart < observations[0].Uncertainty + observations[1].Uncertainty + TimeSpan.FromDays(7));

        var forecast = WeeklyResetDetector.PredictNextReset(observations,
            DateTimeOffset.Parse("2026-07-28T07:00:00Z", null,
                System.Globalization.DateTimeStyles.AssumeUniversal |
                System.Globalization.DateTimeStyles.AdjustToUniversal));

        // Both brackets are wide, so the tighter one anchors — 9 h 19 m beats 10 h 07 m —
        // and it is stepped forward two windows to clear "now".
        Assert.NotNull(forecast);
        Assert.Equal(observations[0].LatestUtc.AddDays(14), forecast.AtUtc);
        Assert.Equal(observations[0].Uncertainty, forecast.Uncertainty);
    }
}
