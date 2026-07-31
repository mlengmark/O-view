using OView.Core.Models;
using OView.Core.Providers.PlanHistory;

namespace OView.Core.Tests;

/// <summary>
/// The panel's wording, extracted so both heads say the same thing.
///
/// <para>These assert the <b>exact strings the Windows panel produced before the
/// extraction</b>, character for character. The panel's own offscreen renders cannot serve
/// as the check — they embed a live clock and countdowns, so two renders of an unchanged
/// build a minute apart already differ. Comparing them would have looked like a
/// verification while proving nothing.</para>
/// </summary>
public class PanelTextTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateTimeOffset Now = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    // ── countdown ───────────────────────────────────────────────────────────────────

    [Fact]
    public void UnderAMinuteIsWordsNotZero() =>
        // "0m" would read as a broken value rather than as imminent.
        Assert.Equal("under a minute", PanelText.Countdown(TimeSpan.FromSeconds(30)));

    [Fact]
    public void AnElapsedCountdownDoesNotGoNegative() =>
        Assert.Equal("under a minute", PanelText.Countdown(TimeSpan.FromMinutes(-5)));

    [Theory]
    [InlineData(1.5, "1m")]        // minutes only
    [InlineData(45, "45m")]
    [InlineData(60, "1h 0m")]      // the hour boundary keeps its minutes
    [InlineData(134, "2h 14m")]
    [InlineData(1500, "1d 1h")]    // a day out drops to days + hours
    [InlineData(4590, "3d 4h")]
    public void UnitsStepDownWithMagnitude(double minutes, string expected) =>
        Assert.Equal(expected, PanelText.Countdown(TimeSpan.FromMinutes(minutes)));

    // ── session reset ───────────────────────────────────────────────────────────────

    [Fact]
    public void SessionResetReadsAsCountdownPlusClockTime() =>
        Assert.Equal(
            "Resets in 2h 14m · 14:14",
            PanelText.SessionReset(Now.AddMinutes(134), Now, Utc));

    /// <summary>
    /// Before any drop is observed the reset time is genuinely unknown. Saying so beats
    /// guessing, and beats rendering nothing — which is indistinguishable from a bug
    /// (ADR-0011, rule 6).
    /// </summary>
    [Fact]
    public void AnUnobservedSessionResetSaysSoRatherThanGuessing() =>
        Assert.Equal(
            "Reset time unknown (no reset observed yet)",
            PanelText.SessionReset(null, Now, Utc));

    // ── weekly reset ────────────────────────────────────────────────────────────────

    [Fact]
    public void APreciseWeeklyResetCarriesTheWeekdayAndNoTilde() =>
        Assert.Equal(
            "Resets in 3d 4h · Mon 16:30",
            PanelText.WeeklyReset(Now.AddMinutes(4590), TimeSpan.FromMinutes(5), Now, Utc));

    /// <summary>
    /// A reset observed while Claude Desktop was not sampling is only bracketed to within
    /// hours. The <c>~</c> is the whole point: showing an exact minute O-view does not have
    /// would be a fabricated number.
    /// </summary>
    [Fact]
    public void AnApproximateWeeklyResetIsMarkedWithATilde() =>
        Assert.Equal(
            "Resets in 3d 4h · ~Mon 16:30",
            PanelText.WeeklyReset(Now.AddMinutes(4590), TimeSpan.FromHours(9), Now, Utc));

    [Fact]
    public void TheBracketBoundaryDecidesWhetherItIsApproximate()
    {
        Assert.False(PanelText.IsApproximate(WeeklyResetDetector.PreciseBracket));
        Assert.False(PanelText.IsApproximate(null));
        Assert.True(PanelText.IsApproximate(WeeklyResetDetector.PreciseBracket + TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void TheApproximateHintNamesHowWideTheBracketIs() =>
        Assert.Contains("known to within 9h 0m",
            PanelText.WeeklyResetApproximateHint(TimeSpan.FromHours(9)), StringComparison.Ordinal);

    // ── caveats ─────────────────────────────────────────────────────────────────────

    private static PanelStatistics Stats(int recordedDays, params string[] unpriced) =>
        new(0, null, 0, null, recordedDays, 31, [], 0, null) { UnpricedModels = unpriced };

    [Fact]
    public void AFullyCoveredWindowWithEverythingPricedHasNoCaveat() =>
        Assert.Equal("", PanelText.Caveat(Stats(31)));

    /// <summary>
    /// ADR-0006 makes this a requirement, not a nicety: a small 31-day figure without it
    /// reads as low usage rather than as short history.
    /// </summary>
    [Fact]
    public void PartialHistoryStatesItsCoverage() =>
        Assert.Equal("3 of 31 days recorded", PanelText.Caveat(Stats(3)));

    /// <summary>
    /// An unpriced model is named rather than silently dropped — and must not void the
    /// total. One newly released Claude once blanked both Est. tiles entirely.
    /// </summary>
    [Fact]
    public void AnUnpricedModelIsNamed() =>
        Assert.Equal("excludes claude-x (no published rate)", PanelText.Caveat(Stats(31, "claude-x")));

    [Fact]
    public void BothCaveatsAppearTogether() =>
        Assert.Equal(
            "3 of 31 days recorded · excludes claude-x, claude-y (no published rate)",
            PanelText.Caveat(Stats(3, "claude-x", "claude-y")));

    // ── tile labels ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// "Est." is never dropped: within plan limits the marginal cost is zero and these
    /// price tokens at public API rates, so the figure is a valuation, not a charge. The
    /// framing flips only when usage genuinely goes off-plan.
    /// </summary>
    [Fact]
    public void TheTodayLabelFlipsWhenSpendStopsBeingHypothetical()
    {
        Assert.Equal("Est. value today", PanelText.EstTodayLabel(offPlan: false));
        Assert.Equal("Est. spend today", PanelText.EstTodayLabel(offPlan: true));
        Assert.StartsWith("Est.", PanelText.EstTodayLabel(offPlan: true), StringComparison.Ordinal);
    }

    /// <summary>
    /// Divergence is detected for the current session window only, so the 31-day heading
    /// must NOT flip — that would extend a claim over 30 days it was never measured against.
    /// </summary>
    [Fact]
    public void The31DayLabelDoesNotFlip() =>
        Assert.Equal("Est. value · 31 days", PanelText.Est31DaysLabel);

    [Fact]
    public void TheOffPlanNoteOnlyAppearsOffPlan()
    {
        Assert.Equal("", PanelText.OffPlanNote(offPlan: false));
        Assert.Equal("incl. off-plan usage", PanelText.OffPlanNote(offPlan: true));
    }
}
