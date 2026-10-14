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
}
