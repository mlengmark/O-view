using System.Globalization;
using OView.Core.Models;
using OView.Core.Providers.CachedUsage;
using OView.Core.Providers.PlanHistory;
using OView.Presentation.Text;

namespace OView.Presentation.Tests;

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
    /// <see cref="PanelText.Freshness"/> reads <c>CapturedAtUtcValue</c> rather than the plain
    /// nullable member, but the tier it reports (<c>As of</c> vs <c>Local estimate</c>) still
    /// comes from the snapshot's <see cref="DataSource"/>, not from this status — a status can
    /// read Estimated under a Live snapshot (a provider can mark one figure modelled without
    /// changing the snapshot's own tier) and the stamp must not disappear because of it.
    /// </summary>
    [Fact]
    public void AnEstimatedCaptureTimeUnderALiveSnapshotStillGetsStamped()
    {
        var snapshot = Snapshot(DataSource.Live, Now.AddMinutes(-40)) with
        {
            CapturedAtUtcStatus = UsageValueStatus.Estimated,
        };

        Assert.Equal("As of 11:20", PanelText.Freshness(snapshot, Now, Utc));
    }

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
        Assert.Equal("", TokenText.Caveat(Stats(31)));

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
            Assert.DoesNotContain(TokenText.TokenScopeCaveat, TokenText.Caveat(stats), StringComparison.Ordinal);
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
        Assert.Equal("chat and cloud sessions not counted", TokenText.TokenScopeCaveat);
    }

    /// <summary>
    /// ADR-0006 makes this a requirement, not a nicety: a small 31-day figure without it
    /// reads as low usage rather than as short history.
    /// </summary>
    [Fact]
    public void PartialHistoryStatesItsCoverage() =>
        Assert.Equal("3 of 31 days recorded", TokenText.Caveat(Stats(3)));

    /// <summary>
    /// An unpriced model is named rather than silently dropped — and must not void the
    /// total. One newly released Claude once blanked both Est. tiles entirely.
    /// </summary>
    [Fact]
    public void AnUnpricedModelIsNamed() =>
        Assert.Equal("excludes claude-x (no published rate)", TokenText.Caveat(Stats(31, "claude-x")));

    [Fact]
    public void BothCaveatsAppearTogether() =>
        Assert.Equal(
            "3 of 31 days recorded · excludes claude-x, claude-y (no published rate)",
            TokenText.Caveat(Stats(3, "claude-x", "claude-y")));

    /// <summary>
    /// Cache writes whose TTL was never recorded are priced at the 5-minute rate, and the panel
    /// says so where it applies (GitHub issue #255). The alternative the issue rejected outright
    /// was pricing them at 1.25× silently, which is the original defect with a migration in
    /// front of it.
    /// </summary>
    [Fact]
    public void UnattributedCacheWritesNameTheAssumptionTheyArePricedUnder()
    {
        var caveat = TokenText.Caveat(Stats(31) with { TtlUnrecordedCacheWrites = 4_200_000 });

        Assert.Contains("4.2M cache writes", caveat, StringComparison.Ordinal);
        Assert.Contains("5-minute rate", caveat, StringComparison.Ordinal);
    }

    /// <summary>
    /// It is a condition that clears, not a standing note: once the window holds only rows this
    /// build ingested, every write carries its own TTL and there is nothing to caveat.
    /// </summary>
    [Fact]
    public void NoUnattributedWritesMeansNoAssumptionToState() =>
        Assert.Equal("", TokenText.Caveat(Stats(31) with { TtlUnrecordedCacheWrites = 0 }));

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

        Assert.Equal("rates: bundled, as of 24 Jun 2026", TokenText.Caveat(stats));
    }

    /// <summary>
    /// And stays off while the table is current. A caveat that is always on says nothing, and
    /// the figures are not less true for being priced at a rate that has not changed.
    /// </summary>
    [Fact]
    public void ACurrentRateTableIsNotWorthSaying() =>
        Assert.Equal("", TokenText.Caveat(Stats(31) with { RatesAreStale = false }));

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
        var text = OffPlanText.DivergenceDetail(126_900, 0);

        Assert.Contains("126.9K output tokens", text, StringComparison.Ordinal);
        Assert.Contains("moved 0 points", text, StringComparison.Ordinal);
        Assert.Contains("cannot see your billing", text, StringComparison.Ordinal);
        Assert.DoesNotContain("most likely", text, StringComparison.Ordinal);
        Assert.DoesNotContain("credits", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OnePointIsNotPluralised() =>
        Assert.Contains("moved 1 point.", OffPlanText.DivergenceDetail(60_000, 1), StringComparison.Ordinal);

    /// <summary>An exhausted window is stated as such, without asserting how it bills.</summary>
    [Fact]
    public void TheLimitReachedWordingDoesNotAssertHowItBills()
    {
        Assert.Contains("exhausted", OffPlanText.PlanLimitReachedDetail, StringComparison.Ordinal);
        Assert.Contains("O-view could not read", OffPlanText.PlanLimitReachedDetail, StringComparison.Ordinal);
        Assert.DoesNotContain("bills as extra usage at API rates",
            OffPlanText.PlanLimitReachedDetail, StringComparison.Ordinal);
    }

    // ── off-plan heading and detail, against the extra-usage setting (issue #259) ────

    private static readonly DateTimeOffset Fetched = new(2026, 7, 31, 9, 7, 0, TimeSpan.Zero);

    private static DivergenceResult LimitReached =>
        new(DivergenceState.PlanLimitReached, 120_000, 0);

    private static DivergenceResult Diverging =>
        new(DivergenceState.Diverging, 126_900, 0);

    /// <summary>
    /// The heading the issue was raised about. "Plan limit reached — usage is billing beyond
    /// your plan" was shown to every reader whose 5-hour window ran out, including the ones who
    /// had extra usage switched off and therefore could not be billed anything — a claim about
    /// their money that was both alarming and false (issue #259).
    /// </summary>
    [Fact]
    public void TheExhaustedWindowHeadingClaimsAChargeOnlyWhereTheAccountAllowsOne()
    {
        Assert.Equal("Plan limit reached — further work bills as extra usage",
            OffPlanText.OffPlanTitle(DivergenceState.PlanLimitReached, ExtraUsageState.Enabled));

        Assert.Equal("Plan limit reached — extra usage is switched off",
            OffPlanText.OffPlanTitle(DivergenceState.PlanLimitReached, ExtraUsageState.Disabled));

        // Unknown states the observation and nothing else. It is the state a machine with no
        // Claude Code is in, so it must read as a complete sentence rather than a hedge.
        Assert.Equal("Plan limit reached",
            OffPlanText.OffPlanTitle(DivergenceState.PlanLimitReached, ExtraUsageState.Unknown));
    }

    /// <summary>
    /// No state of the account turns the disabled heading into a billing claim. Written as an
    /// exhaustive sweep rather than three literals because the wording will be edited again,
    /// and what must survive the edit is the claim, not the sentence.
    /// </summary>
    [Theory]
    [InlineData(ExtraUsageState.Unknown)]
    [InlineData(ExtraUsageState.Disabled)]
    public void OnlyAnEnabledAccountIsToldItsWorkIsBilling(ExtraUsageState state)
    {
        var title = OffPlanText.OffPlanTitle(DivergenceState.PlanLimitReached, state);
        var detail = OffPlanText.OffPlanDetail(LimitReached, state, Fetched, Now, Utc);

        Assert.DoesNotContain("billing beyond your plan", title, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bills beyond", detail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The diverging heading never asserted a charge and does not gain one. The meter not
    /// moving may not be about billing at all — a Cowork session in a cloud container leaves no
    /// local record either (rule 9) — so the account setting changes the detail, not the claim.
    /// </summary>
    [Theory]
    [InlineData(ExtraUsageState.Unknown)]
    [InlineData(ExtraUsageState.Disabled)]
    [InlineData(ExtraUsageState.Enabled)]
    public void TheDivergingHeadingIsTheSameWhateverTheAccountSaysAboutExtraUsage(ExtraUsageState state) =>
        Assert.Equal("This session's usage is not drawing from your plan",
            OffPlanText.OffPlanTitle(DivergenceState.Diverging, state));

    /// <summary>
    /// The setting is relayed with its provenance, never asserted in O-view's own voice: it is
    /// another application's cache, refreshed only when <c>/usage</c> runs, so it can be days
    /// old while looking current. Same treatment, same reason, as the promo notice.
    /// </summary>
    [Fact]
    public void TheSettingIsRelayedWithWhoSaidItAndWhen()
    {
        var off = OffPlanText.OffPlanDetail(LimitReached, ExtraUsageState.Disabled, Fetched, Now, Utc);

        Assert.Contains("exhausted", off, StringComparison.Ordinal);
        Assert.Contains("switched off", off, StringComparison.Ordinal);
        Assert.Contains("should not bill beyond it", off, StringComparison.Ordinal);
        Assert.Contains("reported by Claude Code, read 09:07", off, StringComparison.Ordinal);

        var on = OffPlanText.OffPlanDetail(LimitReached, ExtraUsageState.Enabled, Fetched, Now, Utc);

        Assert.Contains("switched on", on, StringComparison.Ordinal);
        Assert.Contains("can bill beyond it", on, StringComparison.Ordinal);
    }

    /// <summary>
    /// A bare <c>09:07</c> on a four-day-old block reads as this morning, which hides the one
    /// thing the provenance clause exists to disclose. The date appears as soon as the reading
    /// is not from the reader's own today.
    /// </summary>
    [Fact]
    public void AReadingFromAnotherDayCarriesItsDate()
    {
        var stale = OffPlanText.OffPlanDetail(
            LimitReached, ExtraUsageState.Disabled, Now.AddDays(-4), Now, Utc);

        Assert.Contains("read 27 Jul 12:00", stale, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nothing read means nothing claimed, in both directions — and specifically not a stamp on
    /// a reading that never happened. A null fetch time is treated as no answer even if a state
    /// was somehow supplied alongside it.
    /// </summary>
    [Fact]
    public void WithNothingReadTheDetailFallsBackToTheObservationAlone()
    {
        Assert.Equal(OffPlanText.PlanLimitReachedDetail,
            OffPlanText.OffPlanDetail(LimitReached, ExtraUsageState.Unknown, Fetched, Now, Utc));

        Assert.Equal(OffPlanText.PlanLimitReachedDetail,
            OffPlanText.OffPlanDetail(LimitReached, ExtraUsageState.Disabled, null, Now, Utc));

        Assert.Equal(OffPlanText.DivergenceDetail(126_900, 0),
            OffPlanText.OffPlanDetail(Diverging, ExtraUsageState.Unknown, Fetched, Now, Utc));
    }

    /// <summary>
    /// The diverging detail keeps its two numbers when the setting is known — the setting is
    /// added to the observation, not substituted for it.
    /// </summary>
    [Fact]
    public void TheDivergingDetailKeepsItsNumbersAndGainsTheSetting()
    {
        var text = OffPlanText.OffPlanDetail(Diverging, ExtraUsageState.Disabled, Fetched, Now, Utc);

        Assert.Contains("126.9K output tokens", text, StringComparison.Ordinal);
        Assert.Contains("moved 0 points", text, StringComparison.Ordinal);
        Assert.Contains("switched off", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The link the issue asked for, and the reason it is a constant: every wording above ends
    /// by sending the reader to Claude for the figure O-view will not state, so the address has
    /// to be the one Claude Code itself uses for that screen rather than a plausible guess
    /// (rule 6 applied to a URL).
    /// </summary>
    [Fact]
    public void TheBannerLinkPointsAtClaudesOwnUsageSettings()
    {
        Assert.Equal("https://claude.ai/settings/usage", OffPlanText.UsageSettingsUrl);
        Assert.NotEmpty(OffPlanText.UsageSettingsLinkLabel);
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
        Assert.Equal("Est. value today", TokenText.EstTodayLabel(offPlan: false));
        Assert.Equal("Est. spend today", TokenText.EstTodayLabel(offPlan: true));
        Assert.StartsWith("Est.", TokenText.EstTodayLabel(offPlan: true), StringComparison.Ordinal);
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
        Assert.DoesNotContain("UTC", TokenText.TokensTodayLabel, StringComparison.Ordinal);
        Assert.DoesNotContain("UTC", TokenText.EstTodayLabel(offPlan: false), StringComparison.Ordinal);
        Assert.DoesNotContain("UTC", TokenText.EstTodayLabel(offPlan: true), StringComparison.Ordinal);
    }

    /// <summary>
    /// Divergence is detected for the current session window only, so the 31-day heading
    /// must NOT flip — that would extend a claim over 30 days it was never measured against.
    /// </summary>
    [Fact]
    public void The31DayLabelDoesNotFlip() =>
        Assert.Equal("Est. value · 31 days", TokenText.Est31DaysLabel);

    [Fact]
    public void TheOffPlanNoteOnlyAppearsOffPlan()
    {
        Assert.Equal("", OffPlanText.OffPlanNote(offPlan: false));
        Assert.Equal("incl. off-plan usage", OffPlanText.OffPlanNote(offPlan: true));
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
        var withUsage = OffPlanText.OffPlanHint(hasCreditUsage: true);
        Assert.Contains("Estimated at published API rates", withUsage, StringComparison.Ordinal);
        Assert.Contains("billing page", withUsage, StringComparison.Ordinal);
        Assert.Contains("cannot read your credit balance", withUsage, StringComparison.Ordinal);

        // The zero state has its own caveat, and it is the more important of the two: a
        // confident "$0.00" is only honest alongside "usage while O-view wasn't running
        // isn't captured".
        var without = OffPlanText.OffPlanHint(hasCreditUsage: false);
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
                OffPlanText.CreditModelsCaption,
                OffPlanText.OffPlanHint(hasUsage),
                StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The caption joins names with non-breaking spaces so a model never splits across a
    /// line; compare against ordinary spaces so these assertions read as the sentence a
    /// user sees rather than as its encoding.
    /// </summary>
    private const char Nbsp = '\u00A0';

    private static string CreditCaption => OffPlanText.CreditModelsCaption.Replace(Nbsp, ' ');

    /// <summary>
    /// The caption lives in Presentation, built from <see cref="CreditBilledModels.Models"/>,
    /// but what it must still prove is unchanged: the caption names exactly the set
    /// <see cref="CreditBilledModels.IsCreditBilled"/> matches. The regression this issue
    /// was filed for: the caption said "Fable" while the
    /// classifier matched Fable AND Mythos, so credit spend was summed for a model the note
    /// told the user was not included (GitHub issue #56).
    /// </summary>
    [Fact]
    public void CreditDisplayList_NamesEveryModelItCounts()
    {
        var credit = ModelCatalog.InClass(BillingClass.Credit);

        Assert.NotEmpty(credit);
        foreach (var entry in credit)
        {
            Assert.True(CreditBilledModels.IsCreditBilled(entry.Prefix),
                $"{entry.Prefix} is classified Credit but IsCreditBilled says otherwise");
            Assert.Contains(entry.DisplayName, CreditCaption, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CreditDisplayList_NamesNothingItDoesNotCount()
    {
        // The mirror of the above: the caption must not claim coverage the classifier
        // does not provide either.
        foreach (var entry in ModelCatalog.InClass(BillingClass.Plan))
        {
            Assert.False(CreditBilledModels.IsCreditBilled(entry.Prefix));
            Assert.DoesNotContain(entry.DisplayName, CreditCaption, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CreditDisplayList_KeepsEachModelOnOneLine()
    {
        // Non-breaking spaces inside the names, ordinary spaces only after the commas
        // that separate them.
        Assert.DoesNotContain(" 5", OffPlanText.CreditModelsCaption, StringComparison.Ordinal);
        Assert.Contains(Nbsp, OffPlanText.CreditModelsCaption);
    }

    /// <summary>
    /// The breakdown's folded remainder is pinned nowhere else: the golden images cover
    /// <see cref="TokenText.OtherSegmentLabel"/>, but no golden names a model count, so a
    /// typo in <see cref="TokenText.FoldedModelsLabel"/> would ship silently.
    /// </summary>
    [Fact]
    public void TheFoldedModelsLabelNamesTheCount() =>
        Assert.Equal("3 more models", TokenText.FoldedModelsLabel(3));

    [Fact]
    public void TheOtherSegmentLabelIsOther() =>
        Assert.Equal("Other", TokenText.OtherSegmentLabel);

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

    // ── the weekly reset nobody has reported (ADR-0014) ──────────────────────

    /// <summary>
    /// The row states the fact. It must not promise a fill-in: the derivation that once justified
    /// "Waiting for first reset…" was deleted by ADR-0014, so a wait is the one thing this state
    /// is not.
    /// </summary>
    [Fact]
    public void TheUnknownWeeklyResetRowDoesNotDescribeAWait()
    {
        Assert.Equal("Weekly reset time not known", PanelText.WeeklyResetUnknown);
        Assert.DoesNotContain("Waiting", PanelText.WeeklyResetUnknown, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The hint names both routes and neither is chosen for the reader. Which one applies
    /// depends on whether this machine has a usable Claude Code — and on the machine that
    /// prompted this, O-view's own refresher was running and producing nothing, so a branch
    /// would assert a cause nobody has established (rule 6 turned on ourselves).
    /// </summary>
    [Fact]
    public void TheUnknownWeeklyResetHintCarriesBothRoutes()
    {
        Assert.Contains("/usage", PanelText.WeeklyResetUnknownHint, StringComparison.Ordinal);
        Assert.Contains("Weekly reset", PanelText.WeeklyResetUnknownHint, StringComparison.Ordinal);
        Assert.Contains("once is enough", PanelText.WeeklyResetUnknownHint,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The Linux row has one line and no hover card, so the action rides on the row itself.
    /// Both heads read these from here, which is what <see cref="PanelText"/> is for.
    /// </summary>
    [Fact]
    public void TheShortActionNamesTheCommand()
    {
        Assert.Contains("/usage", PanelText.WeeklyResetUnknownAction, StringComparison.Ordinal);
    }

    // ── the labels both panels used to spell out themselves ─────────────────────────

    /// <summary>
    /// The two meter names, character for character as both panels already showed them. The
    /// Windows panel had them typed into its markup and the Linux one into its builder, so
    /// the pair could have drifted on either side without a test noticing.
    /// </summary>
    [Fact]
    public void TheMeterNamesAreTheOnesBothPanelsAlreadyShowed()
    {
        Assert.Equal("Current session", PanelText.SessionBarLabel);
        Assert.Equal("Weekly", PanelText.WeeklyBarLabel);
    }

    /// <summary>
    /// Both stand-ins stay lower case. They sit where a name and a tier go, and a capitalised
    /// "Account unknown" reads as a label for something the account is rather than as the
    /// absence of one.
    /// </summary>
    [Fact]
    public void TheUnknownAccountStandinsReadAsAbsences()
    {
        Assert.Equal("account unknown", PanelText.AccountUnknown);
        Assert.Equal("tier unknown", PanelText.TierUnknown);
    }

    /// <summary>
    /// The chart heading counts the window it describes. Both panels read it from the same
    /// place now, and the Windows one no longer carries a 31 typed into its markup — which
    /// would have gone on claiming 31 days over a chart of some other length.
    /// </summary>
    [Fact]
    public void TheChartHeadingCountsTheWindowItDescribes()
    {
        Assert.Equal("Usage · last 31 days", PanelText.UsageWindowHeading(31));
        Assert.Equal("Usage · last 7 days", PanelText.UsageWindowHeading(7));
    }

    /// <summary>
    /// The off-plan spend heading had the same defect the chart heading did, one section
    /// lower: "last 31 days" was typed into the markup beside a total built from
    /// <see cref="PanelStatistics.WindowDays"/>. It counts now, so the heading and the
    /// figure under it cannot describe different spans.
    /// </summary>
    [Fact]
    public void TheOffPlanHeadingCountsTheWindowItDescribes()
    {
        Assert.Equal("Off-plan usage · last 31 days", OffPlanText.OffPlanHeading(31));
        Assert.Equal("Off-plan usage · last 7 days", OffPlanText.OffPlanHeading(7));
    }

    // ── the copy the Windows panel used to write itself ─────────────────────────────

    /// <summary>
    /// The meter figure, character for character as both panels already showed it, and now the
    /// one member both of them read. <b>Asserted whole rather than by looking for "used"</b>: a
    /// panel that went back to building <c>"{p}% used"</c> beside this one would pass a keyword
    /// check while being exactly the duplicate this member exists to remove.
    /// </summary>
    [Fact]
    public void TheMeterFigureNamesTheDirectionItRuns()
    {
        Assert.Equal("47% used", PanelText.BarPercentUsed(47));
        Assert.Equal("0% used", PanelText.BarPercentUsed(0));
        Assert.Equal("100% used", PanelText.BarPercentUsed(100));
    }

    /// <summary>
    /// The digits are the app's, not the desktop's.
    ///
    /// <para>The literal this member replaced in the Linux panel interpolated the percentage
    /// under the ambient culture, which renders Arabic-Indic digits under <c>ar-EG</c> — a panel
    /// whose figure beside the gauge does not match the one in its own tray tooltip. The member
    /// pins <see cref="CultureInfo.InvariantCulture"/>, so this is the behaviour that moving the
    /// Linux figure here bought, and the one a future tidy-up of the <c>string.Create</c> call
    /// would quietly give back.</para>
    /// </summary>
    [Fact]
    public void TheMeterFigureUsesTheAppsDigitsNotTheMachineCultures()
    {
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("ar-EG");

            Assert.Equal("47% used", PanelText.BarPercentUsed(47));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    /// <summary>
    /// The two captions a week-boundary gridline carries. The fallback is asserted as the whole
    /// sentence because every clause in it is load-bearing: that the line is Monday, that no
    /// reset has been seen, and that it is therefore not the plan's boundary. A shortened
    /// version would still read as a disclaimer while disclaiming less.
    /// </summary>
    [Fact]
    public void TheWeekMarkersSayWhetherTheyAreThePlansBoundary()
    {
        Assert.Equal("Weekly limit reset", PanelText.WeeklyResetMarker);
        Assert.Equal(
            "Start of the calendar week (Monday). O-view hasn't observed a weekly reset "
            + "yet, so this is a calendar reference, not your plan's boundary.",
            PanelText.CalendarWeekMarker);
    }

    /// <summary>
    /// The off-plan badge and the label beside its figure, as the Windows panel showed them.
    /// Both branches of the badge are pinned: the Windows panel wrote the spend label twice,
    /// once per branch, which is the shape a drift takes.
    /// </summary>
    [Fact]
    public void TheOffPlanBadgeReportsBothStatesAndKeepsTheEstPrefix()
    {
        Assert.Equal("credit-billed", PanelText.CreditBadgeRecorded);
        Assert.Equal("none recorded", PanelText.CreditBadgeNoneRecorded);
        Assert.Equal("Est. credit spend", PanelText.CreditSpendLabel);
    }
}
