using OView.Core.Models;
using OView.Core.Providers.CachedUsage;
using OView.Presentation.Panel;
using OView.Presentation.Text;

namespace OView.Presentation.Tests;

/// <summary>
/// The off-plan banner and the standing section, built once for both heads.
///
/// <para>These assert the decisions the Windows panel used to make alone and the Linux panel
/// did not make at all: whether a banner is drawn, which of the three billing headings it is
/// allowed to carry, whether the detail line is stamped with when the setting was read, and
/// whether the section exists at all. The sentences themselves are <see cref="OffPlanText"/>'s
/// and are asserted against it rather than re-typed, so a reworded sentence does not fail here,
/// only a face choosing the wrong one does.</para>
/// </summary>
public class PanelOffPlanTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateTimeOffset Now = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    /// <summary>31 days, all of them recorded, so the coverage caveat is silent unless a test
    /// asks for a partial window.</summary>
    private static readonly IReadOnlyList<DayUsage> Series =
        [.. Enumerable.Range(0, 31).Select(i => new DayUsage(
            DateOnly.FromDateTime(Now.UtcDateTime).AddDays(-30 + i), 100_000, PreInstall: false))];

    private static PanelStatistics Stats(
        long creditTokens = 395_000, decimal? creditUsd = 92.75m, int recordedDays = 31) =>
        new(
            TokensToday: 128_600,
            Tokens31Days: 6_771_000,
            RecordedDays: recordedDays, WindowDays: 31,
            DailySeries: Series,
            CreditTokens31Days: creditTokens)
        {
            EstTodayValue = Priced(9.36m),
            Est31DaysValue = Priced(492.52m),
            EstCredit31DaysValue = Priced(creditUsd),
        };

    /// <summary>An Est. figure as the real builder pairs it: estimated, or unavailable.</summary>
    private static EstimatedUsd Priced(decimal? usd) =>
        new(usd, usd is null ? UsageValueStatus.Unavailable : UsageValueStatus.Estimated);

    /// <summary>The exhausted-window state the three sample renders are built on.</summary>
    private static PanelStatistics Exhausted() => Stats() with
    {
        Divergence = new DivergenceResult(DivergenceState.PlanLimitReached, 69_091, 0),
        EstOffPlanValue = Priced(92.75m),
    };

    private static CachedUtilization Cached(ExtraUsageState state, bool userDisabled = false) =>
        new(Now.AddMinutes(-20), null, null, null)
        {
            ExtraUsage = new ExtraUsageStatus(state, userDisabled),
        };

    /// <summary>
    /// A block with a model limit. <paramref name="percent"/> is that model's usage, which the
    /// figure must take no notice of.
    /// </summary>
    private static CachedUtilization Limited(
        SpendRead? spend = null,
        ExtraUsageState extraUsage = ExtraUsageState.Unknown,
        int percent = 58,
        DateTimeOffset? resetsAtUtc = null) =>
        Cached(extraUsage) with
        {
            ModelLimits = new ModelLimitRead(
                [new ModelLimit("Fable", new UtilizationBar(percent, resetsAtUtc ?? Now.AddDays(3)))],
                RowsSeen: 3,
                RowsSkipped: 2),
            Spend = spend ?? SpendRead.Absent,
        };

    private static SpendRead Reported(decimal amount, string currency = "USD", bool enabled = true) =>
        new(SpendReadState.Read, new ReportedSpend(amount, currency, enabled));

    // ── the live banner ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A window drawing from the plan as expected has no banner at all. Null rather than a
    /// view with empty strings: the heads collapse the row on this, and a banner that existed
    /// but said nothing would be drawing an empty warning box.
    /// </summary>
    [Fact]
    public void AnOnPlanWindowHasNoBanner()
    {
        var offPlan = PanelSections.OffPlan(Stats(), Cached(ExtraUsageState.Enabled), Now, Utc);

        Assert.Null(offPlan.Banner);
    }

    /// <summary>
    /// A state the detector rules on but does not call off-plan is still not a banner. The
    /// verdict is the gate, not the presence of a divergence result.
    /// </summary>
    [Theory]
    [InlineData(DivergenceState.Consistent)]
    [InlineData(DivergenceState.InsufficientActivity)]
    [InlineData(DivergenceState.MeterNotReporting)]
    [InlineData(DivergenceState.RiseNotMeasurable)]
    public void OnlyAnOffPlanVerdictDrawsTheBanner(DivergenceState state)
    {
        var stats = Stats() with { Divergence = new DivergenceResult(state, 69_091, 0) };

        var offPlan = PanelSections.OffPlan(stats, Cached(ExtraUsageState.Enabled), Now, Utc);

        Assert.Null(offPlan.Banner);
    }

    /// <summary>
    /// The three billing wordings, each one the heading the account setting licenses, this is
    /// the claim issue #259 was about, and it is now made in one place for both faces.
    /// </summary>
    [Theory]
    [InlineData(ExtraUsageState.Enabled)]
    [InlineData(ExtraUsageState.Disabled)]
    [InlineData(ExtraUsageState.Unknown)]
    public void TheHeadingFollowsTheAccountsExtraUsageSetting(ExtraUsageState state)
    {
        var stats = Exhausted();

        var offPlan = PanelSections.OffPlan(stats, Cached(state), Now, Utc);

        Assert.NotNull(offPlan.Banner);
        Assert.Equal(
            OffPlanText.OffPlanTitle(DivergenceState.PlanLimitReached, state),
            offPlan.Banner.Title);
    }

    /// <summary>
    /// The three headings are three different sentences. Asserted directly, because the test
    /// above would pass just as well if <see cref="OffPlanText.OffPlanTitle"/> collapsed them
    /// into one hedge, which is the wording it exists to have replaced.
    /// </summary>
    [Fact]
    public void TheEnabledHeadingIsTheOnlyOneThatAssertsACharge()
    {
        var titles = new[] { ExtraUsageState.Enabled, ExtraUsageState.Disabled, ExtraUsageState.Unknown }
            .Select(state => PanelSections.OffPlan(Exhausted(), Cached(state), Now, Utc).Banner!.Title)
            .ToList();

        Assert.Equal(3, titles.Distinct().Count());
        Assert.Contains("extra usage", titles[0]);
        Assert.Contains("switched off", titles[1]);
    }

    /// <summary>
    /// A known setting is relayed with when Claude Code read it. The stamp is the whole of what
    /// makes reporting another application's cache honest rather than a guess (rule 6).
    /// </summary>
    [Theory]
    [InlineData(ExtraUsageState.Enabled)]
    [InlineData(ExtraUsageState.Disabled)]
    public void AKnownSettingIsStampedWithWhenItWasRead(ExtraUsageState state)
    {
        var stats = Exhausted();
        var cached = Cached(state);

        var offPlan = PanelSections.OffPlan(stats, cached, Now, Utc);

        Assert.NotNull(offPlan.Banner);
        // The stamp is the cached block's OWN read time, 11:40, not the moment the panel was
        // drawn, taking the two readings from one object is what makes that unmissable.
        Assert.Contains("reported by Claude Code, read 11:40", offPlan.Banner.Detail);
        Assert.Equal(
            OffPlanText.OffPlanDetail(stats.Divergence!, state, cached.FetchedAtUtc, Now, Utc),
            offPlan.Banner.Detail);
    }

    /// <summary>
    /// With nothing cached there is nothing to stamp and nothing to claim, so the detail line
    /// is the observation alone. A panel that stamped a reading it never took would be
    /// dressing an absence up as a fact.
    /// </summary>
    [Fact]
    public void NothingCachedClaimsNothingAndStampsNothing()
    {
        var offPlan = PanelSections.OffPlan(Exhausted(), null, Now, Utc);

        Assert.NotNull(offPlan.Banner);
        Assert.Equal(OffPlanText.PlanLimitReachedDetail, offPlan.Banner.Detail);
        Assert.DoesNotContain("reported by Claude Code", offPlan.Banner.Detail);
    }

    /// <summary>
    /// The address travels with the label. Each head then shows whichever its surface supports,
    /// the Windows banner makes the label clickable, the Linux one prints the address, and
    /// neither has to know the URL.
    /// </summary>
    [Fact]
    public void TheBannerCarriesBothHalvesOfTheLink()
    {
        var offPlan = PanelSections.OffPlan(Exhausted(), Cached(ExtraUsageState.Enabled), Now, Utc);

        Assert.NotNull(offPlan.Banner);
        Assert.Equal(OffPlanText.UsageSettingsLinkLabel, offPlan.Banner.LinkLabel);
        Assert.Equal(OffPlanText.UsageSettingsUrl, offPlan.Banner.LinkUrl);
    }

    /// <summary>
    /// A diverging window, a meter that is not moving rather than one that is spent, gets the
    /// heading that never asserted a charge, and a detail line naming the two numbers the
    /// reader can check.
    /// </summary>
    [Fact]
    public void ADivergingMeterStatesTheTwoNumbersRatherThanACause()
    {
        var stats = Stats() with
        {
            Divergence = new DivergenceResult(DivergenceState.Diverging, 69_091, 1),
        };

        var offPlan = PanelSections.OffPlan(stats, null, Now, Utc);

        Assert.NotNull(offPlan.Banner);
        Assert.Equal(
            OffPlanText.OffPlanTitle(DivergenceState.Diverging, ExtraUsageState.Unknown),
            offPlan.Banner.Title);
        Assert.Equal(OffPlanText.DivergenceDetail(69_091, 1), offPlan.Banner.Detail);
    }

    // ── the standing section ────────────────────────────────────────────────────────

    /// <summary>
    /// The heading counts the window the figure beside it covers, exactly as the chart's does.
    /// A literal would keep reading 31 the day the window changed.
    /// </summary>
    [Fact]
    public void TheHeadingCountsTheWindowTheFigureCovers()
    {
        var section = PanelSections.OffPlan(Stats() with { WindowDays = 14 }, null, Now, Utc).Section;

        Assert.NotNull(section);
        Assert.Equal(OffPlanText.OffPlanHeading(14), section.Heading);
    }

    /// <summary>Recorded credit usage: the badge says so and the figure is the estimate.</summary>
    [Fact]
    public void RecordedCreditUsageIsBadgedAndPriced()
    {
        var section = PanelSections.OffPlan(Stats(), null, Now, Utc).Section;

        Assert.NotNull(section);
        Assert.Equal(PanelText.CreditBadgeRecorded, section.Badge);
        Assert.Equal(PanelText.CreditSpendLabel, section.SpendLabel);
        Assert.Equal(UsageFormatter.Usd(92.75m), section.SpendValue);
    }

    /// <summary>
    /// Nothing recorded means no section at all, on every account. Null is what both heads
    /// collapse on, so a head cannot draw an empty heading or keep a stated zero of its own.
    /// </summary>
    [Fact]
    public void NoCreditUsageHidesTheSectionRatherThanStatingAZero()
    {
        var offPlan = PanelSections.OffPlan(Stats(creditTokens: 0, creditUsd: null), null, Now, Utc);

        Assert.Null(offPlan.Section);
    }

    /// <summary>
    /// The gate is the usage, not its value. Tokens on a model with no price leave the figure
    /// unknown rather than zero, and hiding the section would report them as nothing (rule 6).
    /// </summary>
    [Fact]
    public void CreditUsageWithNoPriceKeepsTheSectionWithAnUnknownFigure()
    {
        var section = PanelSections.OffPlan(Stats(creditUsd: null), null, Now, Utc).Section;

        Assert.NotNull(section);
        Assert.Equal(UsageFormatter.Usd(null), section.SpendValue);
    }

    /// <summary>
    /// The two registers stay independent: a window that has gone off-plan before any credit
    /// usage was recorded raises the banner and still has no section.
    /// </summary>
    [Fact]
    public void TheBannerShowsWithoutTheSection()
    {
        var stats = Exhausted() with { CreditTokens31Days = 0 };

        var offPlan = PanelSections.OffPlan(stats, null, Now, Utc);

        Assert.NotNull(offPlan.Banner);
        Assert.Null(offPlan.Section);
    }

    /// <summary>
    /// A partial window qualifies the figure on screen. This caveat is the difference between a
    /// real total and one covering nine days, so unlike
    /// <see cref="PanelOffPlanSectionView.Hint"/> it is not a sentence either head may put
    /// behind a hover.
    /// </summary>
    [Fact]
    public void APartialWindowQualifiesTheFigureInPlace()
    {
        var stats = Stats(recordedDays: 9);

        var section = PanelSections.OffPlan(stats, null, Now, Utc).Section;

        Assert.NotNull(section);
        Assert.Equal(TokenText.CoverageNote(stats), section.Coverage);
        Assert.NotEqual("", section.Coverage);
    }

    /// <summary>
    /// The hint keeps the rule-6 caveat that the figure is not the charge, the reason it is a
    /// carried sentence rather than something a head writes beside the number.
    /// </summary>
    [Fact]
    public void TheHintNamesWhatTheFigureIsNot()
    {
        var section = PanelSections.OffPlan(Stats(), null, Now, Utc).Section;

        Assert.NotNull(section);
        Assert.Equal(OffPlanText.OffPlanHint, section.Hint);
        Assert.Contains(OffPlanText.CreditModelsCaption, section.Hint);
    }

    // ── the section on an account with a model limit ────────────────────────────────

    /// <summary>
    /// Both figures non-zero, the one case that could add them: the row alone decides which is
    /// shown, and the other leaves no trace in the section.
    /// </summary>
    [Fact]
    public void TheTwoFiguresAreNeverAdded()
    {
        var withARow = PanelSections.OffPlan(Stats(), Limited(Reported(12.34m)), Now, Utc).Section;
        var withoutARow = PanelSections.OffPlan(
            Stats(), Cached(ExtraUsageState.Enabled) with { Spend = Reported(12.34m) }, Now, Utc).Section;

        Assert.NotNull(withARow);
        Assert.Equal("$12.34", withARow.SpendValue);
        Assert.DoesNotContain("92.75", Sentences(withARow));
        Assert.DoesNotContain("105.09", Sentences(withARow));

        Assert.NotNull(withoutARow);
        Assert.Equal("$92.75", withoutARow.SpendValue);
        Assert.DoesNotContain("12.34", Sentences(withoutARow));
        Assert.DoesNotContain("105.09", Sentences(withoutARow));
    }

    /// <summary>With no row, reported spend changes nothing: today's estimate, field for field.</summary>
    [Fact]
    public void WithNoRowTheEstimateIsUntouchedWhateverSpendReads()
    {
        var withoutSpend = PanelSections.OffPlan(Stats(recordedDays: 9), null, Now, Utc).Section;
        var withSpend = PanelSections.OffPlan(
            Stats(recordedDays: 9), Cached(ExtraUsageState.Enabled) with { Spend = Reported(12.34m) }, Now, Utc).Section;

        Assert.Equal(withoutSpend, withSpend);
    }

    /// <summary>
    /// Heavy use of the limited model is usage inside its limit. Credit usage is on record and
    /// the model is nearly spent, and with nothing reported there is still no section.
    /// </summary>
    [Fact]
    public void HighUsageOfTheModelWithZeroSpendReportedHidesTheSection()
    {
        var cached = Limited(Reported(0m), ExtraUsageState.Enabled, percent: 98);

        var offPlan = PanelSections.OffPlan(Stats(), cached, Now, Utc);

        Assert.Null(offPlan.Section);
    }

    /// <summary>A passed reset says the reading is old, not that the limit went away.</summary>
    [Fact]
    public void ARowWhoseResetHasPassedStillSwitchesTheFigure()
    {
        var cached = Limited(Reported(12.34m), resetsAtUtc: Now.AddDays(-1));

        var section = PanelSections.OffPlan(Stats(), cached, Now, Utc).Section;

        Assert.NotNull(section);
        Assert.Equal("$12.34", section.SpendValue);
    }

    /// <summary>The amount decides: <c>spend.enabled</c> is not asked, either way.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheAmountDecidesWhateverSpendEnabledSays(bool enabled)
    {
        var shown = PanelSections.OffPlan(Stats(), Limited(Reported(12.34m, enabled: enabled)), Now, Utc);
        var hidden = PanelSections.OffPlan(Stats(), Limited(Reported(0m, enabled: enabled)), Now, Utc);

        Assert.NotNull(shown.Section);
        Assert.Null(hidden.Section);
    }

    /// <summary>
    /// The figure is Claude's, so nothing in the section may read as O-view's: no "Est.", no
    /// span it does not state, no coverage of a window it does not have, and no claim that the
    /// usage was credit-billed. An empty badge would draw an empty pill, so the words change
    /// rather than disappear.
    /// </summary>
    [Fact]
    public void AReportedFigureIsLabelledReportedWithNoEstimateOrPeriod()
    {
        var section = PanelSections.OffPlan(Stats(recordedDays: 9), Limited(Reported(12.34m)), Now, Utc).Section;

        Assert.NotNull(section);
        Assert.Contains("reported", section.SpendLabel, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("$12.34", section.SpendValue);
        Assert.DoesNotContain("Est.", Sentences(section));
        Assert.DoesNotContain("days", Sentences(section));
        Assert.DoesNotContain("credit-billed", Sentences(section));
        Assert.Equal("", section.Coverage);
        Assert.NotEqual("", section.Badge);
    }

    /// <summary>A currency the block names is the one shown, and it is never dollars.</summary>
    [Fact]
    public void AnotherCurrencyShowsItsOwnCode()
    {
        var section = PanelSections.OffPlan(Stats(), Limited(Reported(12.34m, "EUR")), Now, Utc).Section;

        Assert.NotNull(section);
        Assert.Equal("12.34 EUR", section.SpendValue);
    }

    /// <summary>
    /// This is another application's cached answer, so the hint carries when it was read: the
    /// block's own read time, 11:40, not the moment the panel was drawn.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheHintIsStampedWithWhenTheBlockWasRead(bool amountRead)
    {
        var cached = amountRead
            ? Limited(Reported(12.34m))
            : Limited(extraUsage: ExtraUsageState.Enabled);

        var section = PanelSections.OffPlan(Stats(), cached, Now, Utc).Section;

        Assert.NotNull(section);
        Assert.Contains("reported by Claude Code, read 11:40", section.Hint);
    }

    /// <summary>
    /// Extra usage on and no amount: money may have moved and nobody said how much, so the
    /// figure is unknown. Both ways of having no amount read the same, because neither has one.
    /// </summary>
    [Theory]
    [InlineData(SpendReadState.Absent)]
    [InlineData(SpendReadState.Unreadable)]
    public void ExtraUsageOnWithNoAmountIsUnknownNeverZero(SpendReadState state)
    {
        var cached = Limited(new SpendRead(state, null), ExtraUsageState.Enabled);

        var section = PanelSections.OffPlan(Stats(), cached, Now, Utc).Section;

        Assert.NotNull(section);
        Assert.Equal(UsageFormatter.Usd(null), section.SpendValue);
        Assert.DoesNotContain("$0", Sentences(section));
        Assert.DoesNotContain("Est.", Sentences(section));
    }

    /// <summary>Off or not stated: nothing says money moved, so nothing is raised.</summary>
    [Theory]
    [InlineData(ExtraUsageState.Disabled, SpendReadState.Absent)]
    [InlineData(ExtraUsageState.Disabled, SpendReadState.Unreadable)]
    [InlineData(ExtraUsageState.Unknown, SpendReadState.Absent)]
    [InlineData(ExtraUsageState.Unknown, SpendReadState.Unreadable)]
    public void ExtraUsageNotOnWithNoAmountHidesTheSection(ExtraUsageState extraUsage, SpendReadState state)
    {
        var cached = Limited(new SpendRead(state, null), extraUsage);

        var offPlan = PanelSections.OffPlan(Stats(), cached, Now, Utc);

        Assert.Null(offPlan.Section);
    }

    /// <summary>
    /// A model limit changes the section and nothing else. The banner and the tile still read
    /// the divergence and the estimate, in the same window with and without a row.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheBannerAndTheValueTileIgnoreTheModelLimit(bool exhausted)
    {
        var stats = exhausted ? Exhausted() : Stats();
        var withoutARow = PanelSections.OffPlan(stats, Cached(ExtraUsageState.Enabled), Now, Utc);
        var withARow = PanelSections.OffPlan(
            stats, Limited(Reported(12.34m), ExtraUsageState.Enabled), Now, Utc);

        Assert.Equal(withoutARow.Banner, withARow.Banner);
        Assert.Equal(withoutARow.ValueTileLabel, withARow.ValueTileLabel);
        Assert.Equal(withoutARow.ValueTileNote, withARow.ValueTileNote);
    }

    /// <summary>Every sentence a section shows, so a word can be ruled out of all of them at once.</summary>
    private static string Sentences(PanelOffPlanSectionView section) =>
        string.Join(
            "\n",
            section.Heading,
            section.Badge,
            section.SpendLabel,
            section.SpendValue,
            section.Coverage,
            section.Hint);

    // ── the value tile's relabel ────────────────────────────────────────────────────

    /// <summary>
    /// The value tile's "not money charged" framing is only true of plan usage, so the label
    /// flips with the window's state and the tile names the mixture.
    /// </summary>
    [Fact]
    public void AnOffPlanWindowRelabelsTheValueTile()
    {
        var offPlan = PanelSections.OffPlan(Exhausted(), Cached(ExtraUsageState.Enabled), Now, Utc);

        Assert.Equal(TokenText.EstTodayLabel(true), offPlan.ValueTileLabel);
        Assert.Equal(OffPlanText.OffPlanNote(true), offPlan.ValueTileNote);
    }

    /// <summary>
    /// And the tiles the heads actually draw take the same reading. One definition, because a
    /// tile labelled "Est. spend" beside a banner that is not showing would describe a
    /// different window than the banner does.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheTileAndTheOffPlanViewAgreeOnTheRelabel(bool exhausted)
    {
        var stats = exhausted ? Exhausted() : Stats();

        var offPlan = PanelSections.OffPlan(stats, null, Now, Utc);
        var tiles = PanelSections.Tiles(stats);

        Assert.Equal(offPlan.ValueTileLabel, tiles.EstToday.Label);
        Assert.Equal(offPlan.ValueTileNote, tiles.EstToday.Note);
    }
}
