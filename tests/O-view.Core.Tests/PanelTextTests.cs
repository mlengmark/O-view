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

    // ── freshness ───────────────────────────────────────────────────────────────────

    private static UsageSnapshot Snapshot(DataSource source, DateTimeOffset? capturedAtUtc) =>
        new(source, 47, 20, null, capturedAtUtc);

    /// <summary>
    /// The header used to read <c>Updated 11:34 · live</c>, where the time was the repaint
    /// clock and "live" was a claim about the pipeline rather than about the figures beside
    /// it (issue #192). Both are gone: the line now carries the capture time and nothing else.
    /// </summary>
    [Fact]
    public void TheHeaderStampsTheReadingNotTheRepaint()
    {
        var line = PanelText.Freshness(Snapshot(DataSource.Live, Now.AddMinutes(-40)), Now, Utc);

        Assert.Equal("As of 11:20", line);
        Assert.DoesNotContain("live", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Updated", line, StringComparison.Ordinal);
    }

    [Fact]
    public void AReadingTakenThisMinuteIsTheOnlyOneCalledNow() =>
        Assert.Equal("As of now", PanelText.Freshness(Snapshot(DataSource.Live, Now), Now, Utc));

    /// <summary>
    /// Thirty seconds old, but on the other side of the minute — a past log, and stamped as
    /// one. This is the case the issue names: once the minute has passed the reading stops
    /// being "now", so the stamp can never read as the current clock minute either.
    /// </summary>
    [Fact]
    public void OnceTheMinuteHasPassedTheReadingIsAPastLog() =>
        Assert.Equal(
            "As of 11:59",
            PanelText.Freshness(Snapshot(DataSource.Live, Now.AddSeconds(-30)), Now, Utc));

    /// <summary>
    /// Live and Stale collapse to the same wording on purpose: the capture time states the
    /// age outright, which is strictly more than the tier split said.
    /// </summary>
    [Fact]
    public void AStaleReadingIsStampedTheSameWay() =>
        Assert.Equal(
            "As of 11:20",
            PanelText.Freshness(Snapshot(DataSource.Stale, Now.AddMinutes(-40)), Now, Utc));

    /// <summary>
    /// What must not collapse (rule 6, ADR-0002): a JSONL-derived figure says it is a local
    /// estimate, with its age beside that label rather than instead of it.
    /// </summary>
    [Fact]
    public void AnEstimateKeepsItsLabelAndGainsTheStamp()
    {
        Assert.Equal(
            "Local estimate · as of 11:20",
            PanelText.Freshness(Snapshot(DataSource.Estimate, Now.AddMinutes(-40)), Now, Utc));
        Assert.Equal(
            "Local estimate",
            PanelText.Freshness(Snapshot(DataSource.Estimate, null), Now, Utc));
    }

    [Fact]
    public void NoDataSaysSo() =>
        Assert.Equal("No data", PanelText.Freshness(UsageSnapshot.None, Now, Utc));

    /// <summary>
    /// An authoritative figure with no capture time says the time is unknown. Falling back to
    /// the repaint clock would put the removed bug back, one branch lower down.
    /// </summary>
    [Fact]
    public void AnAuthoritativeReadingWithoutACaptureTimeSaysSo() =>
        Assert.Equal(
            "Reading time unknown",
            PanelText.Freshness(Snapshot(DataSource.Live, null), Now, Utc));

    /// <summary>
    /// A capture stamped ahead of the clock is a clock adjustment, not a prediction. Report
    /// it as now rather than stamping the panel with a time that has not happened.
    /// </summary>
    [Fact]
    public void ACaptureTimeInTheFutureIsNotStamped() =>
        Assert.Equal(
            "As of now",
            PanelText.Freshness(Snapshot(DataSource.Live, Now.AddSeconds(20)), Now, Utc));

    /// <summary>The stamp converts at the display edge, like every other time the panel shows.</summary>
    [Fact]
    public void TheStampIsInTheDisplayZone()
    {
        var plusTwo = TimeZoneInfo.CreateCustomTimeZone("t+2", TimeSpan.FromHours(2), "t+2", "t+2");

        Assert.Equal(
            "As of 13:20",
            PanelText.Freshness(Snapshot(DataSource.Live, Now.AddMinutes(-40)), Now, plusTwo));
    }

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
        Assert.False(PanelText.IsApproximate(WeeklyWindow.PreciseBracket));
        Assert.False(PanelText.IsApproximate(null));
        Assert.True(PanelText.IsApproximate(WeeklyWindow.PreciseBracket + TimeSpan.FromMinutes(1)));
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

    // ── divergence wording ──────────────────────────────────────────────────────────

    /// <summary>
    /// The banner reports two numbers and names no cause. It used to end "that work is billing
    /// elsewhere — most likely extra-usage credits", which is a claim about the reader's billing
    /// that O-view cannot see — and on the machine that reported it (2026-08-24) was false: the
    /// account had extra usage switched off, which Claude Code's own cache records in
    /// <c>extra_usage.user_disabled</c>. A figure O-view cannot check is not a figure it states.
    /// </summary>
    [Fact]
    public void TheDivergenceBannerStatesTheObservationAndNamesNoCause()
    {
        var text = PanelText.DivergenceDetail(126_900, 0);

        Assert.Contains("126.9K output tokens", text, StringComparison.Ordinal);
        Assert.Contains("moved 0 points", text, StringComparison.Ordinal);
        Assert.Contains("cannot see your billing", text, StringComparison.Ordinal);
        Assert.DoesNotContain("most likely", text, StringComparison.Ordinal);
        Assert.DoesNotContain("credits", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OnePointIsNotPluralised() =>
        Assert.Contains("moved 1 point.", PanelText.DivergenceDetail(60_000, 1), StringComparison.Ordinal);

    /// <summary>An exhausted window is stated as such, without asserting how it bills.</summary>
    [Fact]
    public void TheLimitReachedWordingDoesNotAssertHowItBills()
    {
        Assert.Contains("exhausted", PanelText.PlanLimitReachedDetail, StringComparison.Ordinal);
        Assert.Contains("O-view cannot read", PanelText.PlanLimitReachedDetail, StringComparison.Ordinal);
        Assert.DoesNotContain("bills as extra usage at API rates",
            PanelText.PlanLimitReachedDetail, StringComparison.Ordinal);
    }

    // ── tile labels ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// "Est." is never dropped: within plan limits the marginal cost is zero and these
    /// price tokens at public API rates, so the figure is a valuation, not a charge. The
    /// framing flips only when usage genuinely goes off-plan.
    /// </summary>
    [Fact]
    public void TheTodayLabelFlipsWhenSpendStopsBeingHypothetical()
    {
        Assert.Equal("Est. value today (UTC)", PanelText.EstTodayLabel(offPlan: false));
        Assert.Equal("Est. spend today (UTC)", PanelText.EstTodayLabel(offPlan: true));
        Assert.StartsWith("Est.", PanelText.EstTodayLabel(offPlan: true), StringComparison.Ordinal);
    }

    // ── which day "today" is (issue #210) ───────────────────────────────────────────

    /// <summary>
    /// Both "today" tiles are computed over a UTC day, and both must say so. The number was
    /// always right; the unqualified label was not, and a correct number under a wrong name
    /// is the same rule-6 failure as a wrong one.
    /// </summary>
    [Fact]
    public void BothTodayLabelsNameTheirTimezoneBasis()
    {
        Assert.Contains("(UTC)", PanelText.TokensTodayLabel, StringComparison.Ordinal);
        Assert.Contains("(UTC)", PanelText.EstTodayLabel(offPlan: false), StringComparison.Ordinal);
        Assert.Contains("(UTC)", PanelText.EstTodayLabel(offPlan: true), StringComparison.Ordinal);
    }

    /// <summary>
    /// "(UTC)" on its own still leaves the reader working out which hours are counted, so the
    /// hint states the boundary in their own clock. Asserted against a fixed zone and a fixed
    /// clock, never the machine's — a test that reads whatever zone CI happens to sit in is
    /// the hazard issue #212 is about.
    /// </summary>
    [Fact]
    public void TheTodayHintNamesTheBoundaryInLocalTime()
    {
        // 23:26 UTC on the 25th is 01:26 local on the 26th at UTC+2 — the reported case.
        // The UTC day open on that reading began 02:00 local, that same morning.
        var hint = PanelText.TodayUtcHint(
            new DateTimeOffset(2026, 8, 25, 23, 26, 0, TimeSpan.Zero), PlusTwo);

        Assert.Contains("UTC day", hint, StringComparison.Ordinal);
        Assert.Contains("02:00", hint, StringComparison.Ordinal);
    }

    /// <summary>
    /// West of UTC the boundary lands on the previous local day, which is the half of the
    /// bug that lasts longer: at UTC-8 a user spends eight hours of every evening watching
    /// "today" accrue tomorrow's early hours.
    /// </summary>
    [Fact]
    public void TheTodayHintFollowsTheZoneWestOfUtc()
    {
        var hint = PanelText.TodayUtcHint(
            new DateTimeOffset(2026, 8, 25, 23, 26, 0, TimeSpan.Zero), MinusEight);

        Assert.Contains("16:00", hint, StringComparison.Ordinal);
    }

    private static readonly TimeZoneInfo PlusTwo =
        TimeZoneInfo.CreateCustomTimeZone("test-plus-2", TimeSpan.FromHours(2), "UTC+2", "UTC+2");

    private static readonly TimeZoneInfo MinusEight =
        TimeZoneInfo.CreateCustomTimeZone("test-minus-8", TimeSpan.FromHours(-8), "UTC-8", "UTC-8");

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

    /// <summary>
    /// The off-plan explanation moved from standing text to a hover card (issue #181), and
    /// the risk in that move is losing the caveat rather than relocating it. Both wordings
    /// must still say the figure is not what was charged — an "Est." number presented without
    /// that reads as money taken (rule 6).
    /// </summary>
    [Fact]
    public void TheOffPlanHintKeepsItsCaveatInBothStates()
    {
        var withUsage = PanelText.OffPlanHint(hasCreditUsage: true);
        Assert.Contains("Estimated at published API rates", withUsage, StringComparison.Ordinal);
        Assert.Contains("billing page", withUsage, StringComparison.Ordinal);
        Assert.Contains("cannot read your credit balance", withUsage, StringComparison.Ordinal);

        // The zero state has its own caveat, and it is the more important of the two: a
        // confident "$0.00" is only honest alongside "usage while O-view wasn't running
        // isn't captured".
        var without = PanelText.OffPlanHint(hasCreditUsage: false);
        Assert.Contains("No credit-billed usage", without, StringComparison.Ordinal);
        Assert.Contains("wasn't running isn't captured", without, StringComparison.Ordinal);
    }

    /// <summary>Both wordings name the models, so "credit-billed" is never an unexplained label.</summary>
    [Fact]
    public void TheOffPlanHintNamesTheCreditBilledModels()
    {
        foreach (var hasUsage in new[] { true, false })
        {
            Assert.Contains(
                CreditBilledModels.DisplayList,
                PanelText.OffPlanHint(hasUsage),
                StringComparison.Ordinal);
        }
    }
}
