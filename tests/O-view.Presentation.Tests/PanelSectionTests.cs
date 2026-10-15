using OView.Core.Models;
using OView.Core.Providers.CachedUsage;
using OView.Presentation.Panel;
using OView.Presentation.Text;

namespace OView.Presentation.Tests;

/// <summary>
/// The header and the two meters, built once for both heads.
///
/// <para>These assert the decisions each head used to make for itself: when a figure counts
/// as the account's own, which placeholder stands in for a missing one, when a reset row is
/// worth drawing at all, and what a user-entered reset is labelled. The strings themselves
/// are <see cref="PanelText"/>'s and are asserted against it rather than re-typed, so a
/// reworded sentence does not fail here — only a row choosing the wrong sentence does.</para>
/// </summary>
public class PanelSectionTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateTimeOffset Now = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WeeklyReset = new(2026, 8, 4, 6, 28, 57, TimeSpan.Zero);

    private static readonly ClaudeAccount Account =
        new("Sample User", "sample@example.com", "claude_pro", "sample-org");

    private static UsageSnapshot Live(int session = 25, int weekly = 3) =>
        new(DataSource.Live, session, weekly, Now.AddHours(3), Now, WeeklyReset, TimeSpan.FromDays(7));

    // ── the header ──────────────────────────────────────────────────────────────────

    [Fact]
    public void TheHeaderCarriesTheAccountAsItReads()
    {
        var header = PanelSections.Header(Live(), Account, Now, Utc);

        Assert.Equal(PanelHeaderView.AppName, header.Title);
        Assert.Equal(PanelText.Freshness(Live(), Now, Utc), header.Freshness);
        Assert.Equal("Sample User", header.AccountName);
        Assert.Equal("sample@example.com", header.Email);
        Assert.Equal("claude_pro", header.Tier);
    }

    /// <summary>
    /// With no account file read, the name and tier are stand-in sentences rather than blanks
    /// — a reader must not take either for what their account is called.
    /// </summary>
    [Fact]
    public void AnUnreadAccountStandsInRatherThanBlanking()
    {
        var header = PanelSections.Header(Live(), null, Now, Utc);

        Assert.Equal(PanelText.AccountUnknown, header.AccountName);
        Assert.Equal(PanelText.TierUnknown, header.Tier);
        Assert.Null(header.Email);
    }

    /// <summary>
    /// An email row with nothing in it is not a row. Null and empty mean the same absence,
    /// which the two heads previously tested for differently.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AnAbsentEmailIsNoRowEitherWayItIsAbsent(string? email)
    {
        var header = PanelSections.Header(Live(), Account with { Email = email }, Now, Utc);

        Assert.Null(header.Email);
    }

    // ── which readings may be shown as fact ─────────────────────────────────────────

    [Theory]
    [InlineData(DataSource.Live, true)]
    [InlineData(DataSource.Stale, true)]
    [InlineData(DataSource.Estimate, false)]
    [InlineData(DataSource.None, false)]
    public void OnlyTheAccountsOwnFiguresAreAuthoritative(DataSource source, bool expected) =>
        Assert.Equal(expected, PanelSections.IsAuthoritative(new UsageSnapshot(source, 25, 3, null, Now)));

    /// <summary>
    /// A JSONL-derived snapshot can carry percentages, and they are not the account's meters
    /// (ADR-0002). The row states why instead, and nothing is filled — a zero-width fill
    /// beside a number would read as "no usage" when the truth is "not known" (rule 6).
    /// </summary>
    [Fact]
    public void AnEstimateFillsNoMeter()
    {
        var estimate = new UsageSnapshot(DataSource.Estimate, 91, 80, null, Now);

        var session = PanelSections.SessionMeter(estimate, null, Now, Utc);

        Assert.Equal(PanelBanner.UnknownGauge, session.Value);
        Assert.Null(session.Percent);
        Assert.Null(session.Band);
        Assert.Null(session.FillPercent);
    }

    /// <summary>
    /// When the banner has worked out why the meters are empty, its placeholder says so rather
    /// than the generic one — the whole point of carrying a placeholder per banner.
    /// </summary>
    [Fact]
    public void TheBannersOwnPlaceholderWinsWhenItHasOne()
    {
        var banner = new PanelBanner("title", "detail", PanelBanner.NeedsDesktopGauge);

        var meter = PanelSections.SessionMeter(UsageSnapshot.None, banner, Now, Utc);

        Assert.Equal(PanelBanner.NeedsDesktopGauge, meter.Value);
    }

    // ── the meters ──────────────────────────────────────────────────────────────────

    [Fact]
    public void EachMeterStatesItsOwnFigureAndBand()
    {
        var snapshot = Live(session: 91, weekly: 25);

        var session = PanelSections.SessionMeter(snapshot, null, Now, Utc);
        var weekly = PanelSections.WeeklyMeter(snapshot, null, null, null, Now, Utc);

        Assert.Equal(PanelText.SessionBarLabel, session.Label);
        Assert.Equal(PanelText.BarPercentUsed(91), session.Value);
        Assert.Equal(UsageLevel.Critical, session.Band);
        Assert.Equal(91, session.FillPercent);

        Assert.Equal(PanelText.WeeklyBarLabel, weekly.Label);
        Assert.Equal(PanelText.BarPercentUsed(25), weekly.Value);
        Assert.Equal(UsageLevel.Normal, weekly.Band);
        Assert.Equal(25, weekly.FillPercent);
    }

    /// <summary>
    /// A figure outside 0–100 is still stated as reported — the panel does not quietly correct
    /// what a source said — but it is drawn inside the track. Clamping for the fill is a data
    /// question and belongs here; how few pixels a 1% fill may shrink to is each head's.
    /// </summary>
    [Theory]
    [InlineData(150, 100)]
    [InlineData(-5, 0)]
    public void AFigureOutsideTheTrackIsStatedWholeAndDrawnClamped(int reported, int drawn)
    {
        var meter = PanelSections.SessionMeter(Live(session: reported), null, Now, Utc);

        Assert.Equal(reported, meter.Percent);
        Assert.Equal(PanelText.BarPercentUsed(reported), meter.Value);
        Assert.Equal(drawn, meter.FillPercent);
    }

    [Fact]
    public void TheSessionLineIsTheSameWithOrWithoutHover()
    {
        var snapshot = Live();

        var session = PanelSections.SessionMeter(snapshot, null, Now, Utc);

        Assert.Equal(
            PanelText.SessionReset(
                snapshot.SessionResetAtUtc, Now, Utc, snapshot.SessionResetUncertainty),
            session.ResetLine);
        Assert.Equal(session.ResetLine, session.ResetLineWithAction);
        Assert.Null(session.ResetHint);
    }

    // ── the weekly reset line's three states ────────────────────────────────────────

    [Fact]
    public void AReportedWeeklyResetIsStatedAndUnlabelled()
    {
        var weekly = PanelSections.WeeklyMeter(Live(), null, null, null, Now, Utc);

        Assert.Equal(PanelText.WeeklyReset(WeeklyReset, Now, Utc), weekly.ResetLine);
        Assert.Equal(weekly.ResetLine, weekly.ResetLineWithAction);
        Assert.Null(weekly.ResetHint);
    }

    /// <summary>
    /// A reset the user entered says so, and the hover explains why an entered time outranks
    /// a derived one (issue #186).
    /// </summary>
    [Fact]
    public void AnEnteredWeeklyResetSaysWhoSetIt()
    {
        var settings = new TraySettings(WeeklyResetDay: "Tuesday", WeeklyResetTime: "06:28");

        var weekly = PanelSections.WeeklyMeter(Live(), null, settings, null, Now, Utc);

        Assert.Equal(
            $"{PanelText.WeeklyReset(WeeklyReset, Now, Utc)} · {PanelText.WeeklyResetUserSupplied}",
            weekly.ResetLine);
        Assert.Equal(PanelText.WeeklyResetUserSuppliedHint, weekly.ResetHint);
    }

    /// <summary>
    /// Nothing has reported one and nothing was entered. The row names the state — rendering
    /// nothing is indistinguishable from a bug — and carries the one step that ends it, in the
    /// hint where there is hover and on the line itself where there is not.
    /// </summary>
    [Fact]
    public void AnUnknownWeeklyResetCarriesTheActionOnEitherSurface()
    {
        var snapshot = new UsageSnapshot(DataSource.Live, 25, 3, Now.AddHours(3), Now);

        var weekly = PanelSections.WeeklyMeter(snapshot, null, null, null, Now, Utc);

        Assert.Equal(PanelText.WeeklyResetUnknown, weekly.ResetLine);
        Assert.Equal(PanelText.WeeklyResetUnknownHint, weekly.ResetHint);
        Assert.Contains(PanelText.WeeklyResetUnknownAction, weekly.ResetLineWithAction);
        Assert.StartsWith(PanelText.WeeklyResetUnknown, weekly.ResetLineWithAction);
    }

    /// <summary>
    /// With no plan data there is nothing to act on, and the banner above already explains
    /// why — so there is no row at all rather than a second copy of the explanation.
    /// </summary>
    [Fact]
    public void WithNoPlanDataTheWeeklyRowIsAbsentEntirely()
    {
        var weekly = PanelSections.WeeklyMeter(UsageSnapshot.None, null, null, null, Now, Utc);

        Assert.Null(weekly.ResetLine);
        Assert.Null(weekly.ResetLineWithAction);
        Assert.Null(weekly.ResetHint);
    }

    // ── the boost chip ──────────────────────────────────────────────────────────────

    [Fact]
    public void NoPromoIsNoChip()
    {
        Assert.Null(PanelSections.WeeklyMeter(Live(), null, null, null, Now, Utc).Chip);
        Assert.Null(PanelSections.SessionMeter(Live(), null, Now, Utc).Chip);
    }

    /// <summary>
    /// A live promo on the weekly meter gets the chip and, for a surface with room, Claude's
    /// own sentence with who said it and when it was read.
    /// </summary>
    [Fact]
    public void ALivePromoCarriesBothItsSummaryAndItsCard()
    {
        var notice = new BoostNotice(
            BoostNotice.WeeklyBar, "+50% weekly limits promo through Aug 18",
            50, DateOnly.FromDateTime(Now.UtcDateTime).AddDays(18));
        var notices = new BoostNotices(Now, [notice]);

        var chip = PanelSections.WeeklyMeter(Live(), null, null, notices, Now, Utc).Chip;

        Assert.NotNull(chip);
        Assert.Equal(BoostText.BoostChip(notice, Now, Utc), chip.Text);
        Assert.Equal(BoostText.BoostCard(notice, Now, Utc), chip.Card);
    }

    /// <summary>
    /// The chip is the <b>reader's</b> day's business: a promo ending today is still live for
    /// someone whose local date has not caught up, so the zone the panel renders in decides,
    /// never the machine's.
    /// </summary>
    [Fact]
    public void ThePromosLastDayFollowsTheRendersZone()
    {
        var nowUtc = new DateTimeOffset(2026, 7, 31, 23, 30, 0, TimeSpan.Zero);
        var ends = new DateOnly(2026, 7, 31);
        var notices = new BoostNotices(nowUtc, [
            new BoostNotice(BoostNotice.WeeklyBar, "+50% weekly limits promo", 50, ends),
        ]);
        var aheadOfUtc = TimeZoneInfo.CreateCustomTimeZone("t+2", TimeSpan.FromHours(2), "t+2", "t+2");

        Assert.NotNull(PanelSections.WeeklyMeter(Live(), null, null, notices, nowUtc, Utc).Chip);
        Assert.Null(PanelSections.WeeklyMeter(Live(), null, null, notices, nowUtc, aheadOfUtc).Chip);
    }

    // ── the statistics tiles ────────────────────────────────────────────────────────

    private static ModelSlice Slice(string model, long tokens, decimal? usd, string? name = null) =>
        new(model, name ?? model, tokens, usd);

    /// <summary>
    /// Four models, so the fourth is folded into the remainder — the shape that exercises both
    /// the chromatic slots and the neutral.
    /// </summary>
    private static readonly ModelSlice[] Window =
    [
        Slice("claude-opus-4-8", 350_000_000, 250.00m, "Opus 4.8"),
        Slice("claude-opus-5", 180_000_000, 120.00m, "Opus 5"),
        Slice("claude-fable-5", 40_000_000, 50.00m, "Fable 5"),
        Slice("claude-haiku-4-5", 5_900_000, 5.21m, "Haiku 4.5"),
    ];

    private static PanelStatistics Stats(
        IReadOnlyList<ModelSlice>? today = null,
        IReadOnlyList<ModelSlice>? window = null,
        DivergenceResult? divergence = null)
    {
        var models31 = window ?? Window;

        return new PanelStatistics(
            TokensToday: today?.Sum(s => s.Tokens) ?? 0,
            EstTodayUsd: today?.Sum(s => s.EstUsd ?? 0m) ?? 0m,
            Tokens31Days: models31.Sum(s => s.Tokens),
            Est31DaysUsd: models31.Sum(s => s.EstUsd ?? 0m),
            RecordedDays: 31,
            WindowDays: 31,
            DailySeries: [],
            CreditTokens31Days: 0,
            EstCredit31DaysUsd: null,
            Divergence: divergence)
        {
            ModelsToday = today ?? [],
            Models31Days = models31,
            ModelColourOrder = ModelBreakdown.ColourOrder(models31),
        };
    }

    /// <summary>
    /// <b>The reason the slot order exists.</b> Colour used to follow a segment's rank inside
    /// its own tile, so Opus 5 came out blue on the tile where it happened to be largest and
    /// orange on the one where it was second, and the four tiles could not be read against one
    /// another. A model takes one slot on every tile it appears on.
    /// </summary>
    [Fact]
    public void AModelKeepsOneColourSlotAcrossEveryTile()
    {
        // Today's value ranking differs from the 31-day token ranking: Fable 5 is worth more
        // than Opus 5 here, so a tile that coloured by rank would disagree with the others.
        var today = new[]
        {
            Slice("claude-opus-5", 20_000_000, 10.00m, "Opus 5"),
            Slice("claude-fable-5", 5_000_000, 90.00m, "Fable 5"),
        };
        var tiles = PanelSections.Tiles(Stats(today: today));

        var slots = new[] { tiles.TokensToday, tiles.EstToday, tiles.Tokens31Days, tiles.Est31Days }
            .SelectMany(t => t.Segments)
            .Where(s => s.Identity == "claude-opus-5")
            .Select(s => s.Slot)
            .Distinct()
            .ToList();

        Assert.Equal([ModelBreakdown.SlotFor("claude-opus-5", Window.Select(s => s.Model).ToList())], slots);
    }

    /// <summary>
    /// A token tile's segments are measured in tokens and a value tile's in money. The same
    /// rows, two measures — and the figure a reader sees on hover has to be the one the segment
    /// was sized from.
    /// </summary>
    [Fact]
    public void EachTileMeasuresItsSegmentsInItsOwnUnit()
    {
        var tiles = PanelSections.Tiles(Stats());

        var tokens = tiles.Tokens31Days.Segments.First(s => s.Identity == "claude-opus-4-8");
        var value = tiles.Est31Days.Segments.First(s => s.Identity == "claude-opus-4-8");

        Assert.Equal(UsageFormatter.Tokens(350_000_000), tokens.Figure);
        Assert.Equal(UsageFormatter.Usd(250.00m), value.Figure);
    }

    /// <summary>
    /// Shares are the fraction of the tile's own total, so a head can proportion the bar
    /// without the laid-out pixels. They are the measure's shares, not the token shares, on a
    /// value tile.
    /// </summary>
    [Fact]
    public void SharesAreTheMeasuresOwnFractionsAndFillTheBar()
    {
        var tiles = PanelSections.Tiles(Stats());

        Assert.Equal(1.0, tiles.Est31Days.Segments.Sum(s => s.Share), 6);

        // 250 of 425.21 priced dollars, where the same model is 350M of 575.9M tokens — two
        // different widths for one model, which is the whole point of splitting by measure.
        var value = tiles.Est31Days.Segments.First(s => s.Identity == "claude-opus-4-8");
        Assert.Equal(250.00 / 425.21, value.Share, 6);
    }

    /// <summary>
    /// The fourth model is folded, and the remainder is named by Presentation from the count
    /// Core set — never from a word Core wrote into the slice, which it leaves empty. It takes
    /// the neutral slot, because it is not one of the panel's coloured models.
    /// </summary>
    [Fact]
    public void TheFoldedRemainderIsNamedHereAndTakesTheNeutral()
    {
        // Five models. Past three, the third chromatic slot is given up so the neutral has
        // something it is separable from, so two keep a colour and the other three fold.
        ModelSlice[] window = [.. Window, Slice("claude-brandnew-9", 3_000_000, 2.00m, "Brand New 9")];
        var tiles = PanelSections.Tiles(Stats(window: window));

        var remainder = tiles.Tokens31Days.Segments[^1];

        Assert.Equal(TokenText.OtherSegmentLabel, remainder.Name);
        Assert.Equal(TokenText.FoldedModelsLabel(3), remainder.Identity);
        Assert.Equal(-1, remainder.Slot);
    }

    /// <summary>
    /// An unpriced model cannot go on a value chart — its worth is unknown, not zero — so the
    /// caption says the total is short and the hint names it (rule 6).
    /// </summary>
    [Fact]
    public void AValueTileSaysWhatItHadToLeaveOut()
    {
        var window = new[]
        {
            Slice("claude-opus-4-8", 150_000_000, 30.00m, "Opus 4.8"),
            Slice("claude-brandnew-9", 4_000_000, null, "Brand New 9"),
        };
        var tiles = PanelSections.Tiles(Stats(window: window));

        Assert.Equal(TokenText.ModelBreakdownCaption(1), tiles.Est31Days.Caption);
        Assert.Equal(TokenText.UnpricedModelsHint(["Brand New 9"]), tiles.Est31Days.CaptionHint);

        // The same model on the token tile is not missing from anything — it recorded tokens.
        Assert.Equal(TokenText.ModelBreakdownCaption(0), tiles.Tokens31Days.Caption);
        Assert.Null(tiles.Tokens31Days.CaptionHint);
    }

    /// <summary>
    /// The identifying line is <b>always</b> there. It was once dropped when it matched the
    /// friendly name, which is exactly the case for an unrecognised model — so those hover
    /// cards showed a figure with nothing naming it.
    /// </summary>
    [Fact]
    public void AnUnrecognisedModelStillCarriesItsId()
    {
        var window = new[] { Slice("claude-brandnew-9", 4_000_000, 2.00m) };
        var tiles = PanelSections.Tiles(Stats(window: window));

        var segment = Assert.Single(tiles.Tokens31Days.Segments);
        Assert.Equal("claude-brandnew-9", segment.Name);
        Assert.Equal("claude-brandnew-9", segment.Identity);
    }

    /// <summary>
    /// A tile with nothing to break down offers no flip: an affordance that leads nowhere is
    /// worse than none. Asked of the segments rather than the raw list, because a value tile
    /// whose only model is unpriced has rows and still no chart.
    /// </summary>
    [Fact]
    public void ATileWithNothingToChartCannotFlip()
    {
        // No usage today at all.
        Assert.False(PanelSections.Tiles(Stats()).TokensToday.CanFlip);

        // Rows, but nothing a value chart can place.
        var unpriceable = PanelSections.Tiles(
            Stats(window: [Slice("claude-brandnew-9", 4_000_000, null)]));
        Assert.False(unpriceable.Est31Days.CanFlip);
        Assert.True(unpriceable.Tokens31Days.CanFlip);
    }

    /// <summary>
    /// Which tile carries which caveat. The coverage caveat belongs to the window it describes,
    /// so only the 31-day pair can show it.
    /// </summary>
    [Fact]
    public void TheCoverageCaveatRidesOnTheWindowItDescribes()
    {
        var stats = Stats() with { RecordedDays = 8 };
        var tiles = PanelSections.Tiles(stats);

        Assert.Equal(TokenText.Caveat(stats), tiles.Tokens31Days.Note);
        Assert.Equal(TokenText.Caveat(stats), tiles.Est31Days.Note);
        Assert.Equal("", tiles.TokensToday.Note);
    }

    /// <summary>
    /// Off-plan work bills at API rates, so the value tiles name the mixture and their label
    /// flips with it. <b>The token tiles do not</b> — a count is the same count either way.
    ///
    /// <para>This is the one place the two heads disagree, and the Windows reading is what is
    /// built here. The Linux head still overrides it, because taking this would drop a line
    /// from six shipped renders; which head is right is an open question, not something this
    /// asserts an answer to.</para>
    /// </summary>
    [Fact]
    public void OnlyTheValueTilesNameOffPlanWork()
    {
        var offPlan = new DivergenceResult(DivergenceState.Diverging, 5_000_000, 0);
        var tiles = PanelSections.Tiles(Stats(today: Window, divergence: offPlan));

        Assert.Equal(OffPlanText.OffPlanNote(true), tiles.EstToday.Note);
        Assert.Equal(TokenText.EstTodayLabel(true), tiles.EstToday.Label);

        Assert.Equal("", tiles.TokensToday.Note);
        Assert.Equal(TokenText.TokensTodayLabel, tiles.TokensToday.Label);
    }

    /// <summary>
    /// Both faces of one tile state the same total. They are one object for this reason: a
    /// breakdown whose total disagreed with the summary it opened from would be two figures for
    /// one quantity, and the heads each used to assemble the faces separately.
    /// </summary>
    [Fact]
    public void BothFacesOfATileCarryOneTotalAndOneCaveat()
    {
        var stats = Stats() with { RecordedDays = 8 };
        var tile = PanelSections.Tiles(stats).Tokens31Days;

        Assert.Equal(UsageFormatter.Tokens(stats.Tokens31Days), tile.Value);
        Assert.Equal(TokenText.Caveat(stats), tile.Note);
    }
}
