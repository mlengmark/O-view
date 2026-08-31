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

    /// <summary>
    /// The weekly reset carries the weekday and <b>never a tilde</b> (issue #248).
    ///
    /// <para>It took an uncertainty and could render <c>~</c>, from when the reset was inferred
    /// from a drop in Claude Desktop's sampled series. ADR-0014 replaced that with a reported
    /// instant projected forward by whole weeks, so there is no bracket left to qualify and the
    /// parameter is gone.</para>
    /// </summary>
    [Fact]
    public void AWeeklyResetCarriesTheWeekdayAndNeverATilde() =>
        Assert.Equal(
            "Resets in 3d 4h · Mon 16:30",
            PanelText.WeeklyReset(Now.AddMinutes(4590), Now, Utc));

    /// <summary>
    /// <see cref="PanelText.IsApproximate"/> survives the weekly removal because the <b>session</b>
    /// window still needs it: it rolls from first use rather than sitting on a grid, so its reset
    /// is still derived and still bracketed. Applying one rule to both windows is the mistake
    /// ADR-0014 exists to prevent.
    /// </summary>
    [Fact]
    public void TheBracketBoundaryStillDecidesForTheSessionWindow()
    {
        Assert.False(PanelText.IsApproximate(WeeklyWindow.PreciseBracket));
        Assert.False(PanelText.IsApproximate(null));
        Assert.True(PanelText.IsApproximate(WeeklyWindow.PreciseBracket + TimeSpan.FromMinutes(1)));

        Assert.Contains("~", PanelText.SessionReset(
            Now.AddMinutes(134), Now, Utc, TimeSpan.FromHours(9)), StringComparison.Ordinal);
    }

    // ── caveats ─────────────────────────────────────────────────────────────────────

    private static PanelStatistics Stats(int recordedDays, params string[] unpriced) =>
        new(0, null, 0, null, recordedDays, 31, [], 0, null) { UnpricedModels = unpriced };

    [Fact]
    public void AFullyCoveredWindowWithEverythingPricedHasNoCaveat() =>
        Assert.Equal("", PanelText.Caveat(Stats(31)));

    /// <summary>
    /// The scope note is <b>not</b> part of the per-tile caveat (issue #235).
    ///
    /// <para>That caveat is carried by the two 31-day tiles and is right for statements about the
    /// 31-day window. The scope note applies to every token and cost figure on the panel, so
    /// riding the same channel would print it twice on adjacent tiles and never on the "today"
    /// pair. It renders once, beneath the whole block.</para>
    /// </summary>
    [Fact]
    public void TheScopeNoteDoesNotRideThePerTileCaveat()
    {
        foreach (var stats in new[] { Stats(31), Stats(3), Stats(31, "claude-x"), Stats(3, "claude-x") })
        {
            Assert.DoesNotContain(PanelText.TokenScopeCaveat, PanelText.Caveat(stats), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Names both surfaces rather than saying "local only". A reader who sees "local" has to
    /// already know which of their Claude surfaces are not local; naming them removes that step,
    /// and these are the two the plan bars include and the tiles cannot.
    ///
    /// <para>Measured 2026-08-28: a cloud-container Cowork session writes no registration and no
    /// transcript on this machine at all, so this is a permanent gap rather than a lag.</para>
    /// </summary>
    [Fact]
    public void TheScopeNoteNamesTheSurfacesItExcludes()
    {
        Assert.Equal("chat and cloud sessions not counted", PanelText.TokenScopeCaveat);
    }

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

    /// <summary>
    /// Cache writes whose TTL was never recorded are priced at the 5-minute rate, and the panel
    /// says so where it applies (GitHub issue #255). The alternative the issue rejected outright
    /// was pricing them at 1.25× silently, which is the original defect with a migration in
    /// front of it.
    /// </summary>
    [Fact]
    public void UnattributedCacheWritesNameTheAssumptionTheyArePricedUnder()
    {
        var caveat = PanelText.Caveat(Stats(31) with { TtlUnrecordedCacheWrites = 4_200_000 });

        Assert.Contains("4.2M cache writes", caveat, StringComparison.Ordinal);
        Assert.Contains("5-minute rate", caveat, StringComparison.Ordinal);
    }

    /// <summary>
    /// It is a condition that clears, not a standing note: once the window holds only rows this
    /// build ingested, every write carries its own TTL and there is nothing to caveat.
    /// </summary>
    [Fact]
    public void NoUnattributedWritesMeansNoAssumptionToState() =>
        Assert.Equal("", PanelText.Caveat(Stats(31) with { TtlUnrecordedCacheWrites = 0 }));

    /// <summary>
    /// The rate table's age reaches the tiles past a threshold, naming its source as well as its
    /// date — a figure derived from rates a reader cannot trace is a figure they cannot check.
    /// </summary>
    [Fact]
    public void AStaleRateTableIsNamedBesideTheFiguresItPriced()
    {
        var stats = Stats(31) with
        {
            Rates = ModelCatalog.Bundled with { AsOf = new DateOnly(2026, 6, 24) },
            RatesAreStale = true,
        };

        Assert.Equal("rates: bundled, as of 24 Jun 2026", PanelText.Caveat(stats));
    }

    /// <summary>
    /// And stays off while the table is current. A caveat that is always on says nothing, and
    /// the figures are not less true for being priced at a rate that has not changed.
    /// </summary>
    [Fact]
    public void ACurrentRateTableIsNotWorthSaying() =>
        Assert.Equal("", PanelText.Caveat(Stats(31) with { RatesAreStale = false }));

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
        Assert.Equal("Est. value today", PanelText.EstTodayLabel(offPlan: false));
        Assert.Equal("Est. spend today", PanelText.EstTodayLabel(offPlan: true));
        Assert.StartsWith("Est.", PanelText.EstTodayLabel(offPlan: true), StringComparison.Ordinal);
    }

    /// <summary>
    /// The "(UTC)" qualifier carried by both today tiles for one release comes back off with
    /// the thing it described (issues #210, #211). It is asserted rather than merely deleted:
    /// the figure is the reader's own day now, so re-adding the suffix would mislabel it in
    /// the opposite direction — which is the same rule-6 failure, not its cure.
    /// </summary>
    [Fact]
    public void NeitherTodayLabelStillClaimsAUtcDay()
    {
        Assert.DoesNotContain("UTC", PanelText.TokensTodayLabel, StringComparison.Ordinal);
        Assert.DoesNotContain("UTC", PanelText.EstTodayLabel(offPlan: false), StringComparison.Ordinal);
        Assert.DoesNotContain("UTC", PanelText.EstTodayLabel(offPlan: true), StringComparison.Ordinal);
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

    /// <summary>
    /// The resume row is an action, not a status line. Both heads read this one string, so the
    /// Windows menu and the Linux DBusMenu cannot offer the same control under different names
    /// (issue #234).
    /// </summary>
    [Fact]
    public void TheResumeRowIsWordedAsAnAction()
    {
        Assert.Equal("Resume usage refresh", PanelText.UsageRefreshBlockedRow);
    }

    /// <summary>
    /// The hint has to carry the evidence and admit the check is cautious. A user who trips this
    /// needs to know it may be a false positive, or the only safe reading of a blocked feature is
    /// "O-view is broken".
    /// </summary>
    [Fact]
    public void TheResumeHintNamesTheReasonAndAdmitsItMayBeWrong()
    {
        var hint = PanelText.UsageRefreshBlockedHint("an invocation appears to have been billed (refresh.jsonl)");

        Assert.Contains("refresh.jsonl", hint, StringComparison.Ordinal);
        Assert.Contains("deliberately cautious", hint, StringComparison.Ordinal);
        Assert.Contains("Resume if", hint, StringComparison.Ordinal);
    }
}
