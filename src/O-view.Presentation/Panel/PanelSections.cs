using OView.Core.Models;
using OView.Core.Providers.CachedUsage;
using OView.Presentation.Text;

namespace OView.Presentation.Panel;

/// <summary>
/// Builds the panel's header, its two meters and its four statistics tiles, once, for both
/// heads.
///
/// <para><b>Why this exists above <see cref="PanelText"/>.</b> That class already held every
/// sentence, and the heads still disagreed — not about wording but about which sentence a
/// state calls for. Each one decided for itself when a figure counted as authoritative, when
/// a reset row was worth showing, which placeholder stood in for a missing percentage, and
/// what day it was for the purpose of a promo. Those are the same class of decision as the
/// wording (issues #55, #56): a statement about the user's usage, made twice.</para>
///
/// <para>What stays with each head is everything a toolkit has an opinion about — fonts,
/// spacing, how few pixels a fill may shrink to, whether a hover card can exist at all. A
/// view model here carries the band a figure falls in, never the colour it is painted.</para>
/// </summary>
public static class PanelSections
{
    /// <summary>
    /// Whether the snapshot's percentages are the account's own figures rather than a local
    /// estimate or nothing at all.
    ///
    /// <para>It gates more than the meters — the no-data banner and the token-scope note ask
    /// it too — so it is stated once here rather than spelled out beside each use. Both heads
    /// had their own copy of this expression, which is one edit away from two panels
    /// disagreeing about whether a reading may be shown as fact.</para>
    /// </summary>
    public static bool IsAuthoritative(UsageSnapshot snapshot) =>
        snapshot.Source is DataSource.Live or DataSource.Stale;

    /// <summary>The header: app name, freshness, and the account block.</summary>
    public static PanelHeaderView Header(
        UsageSnapshot snapshot, ClaudeAccount? account, DateTimeOffset utcNow, TimeZoneInfo local) =>
        new(
            PanelHeaderView.AppName,
            PanelText.Freshness(snapshot, utcNow, local),
            account?.DisplayName ?? PanelText.AccountUnknown,
            // Null means "no row", and an empty string means the same thing. The two heads
            // tested it differently — one for null, one for length — and agreed only because
            // ClaudeAccount.ReadString already maps an empty property to null. This states the
            // rule rather than resting on that.
            account?.Email is { Length: > 0 } email ? email : null,
            // Tier from organizationType — seatTier is empty and would render blank (rule 8).
            account?.Tier ?? PanelText.TierUnknown);

    /// <summary>
    /// The rolling five-hour meter. Its reset line is unknown until a window start has been
    /// observed, and carries a <c>~</c> when the start was only bracketed (ADR-0011, issue
    /// #180) — both of which <see cref="PanelText.SessionReset"/> decides.
    /// </summary>
    public static PanelMeterView SessionMeter(
        UsageSnapshot snapshot, PanelBanner? banner, DateTimeOffset utcNow, TimeZoneInfo local)
    {
        var line = PanelText.SessionReset(
            snapshot.SessionResetAtUtc, utcNow, local, snapshot.SessionResetUncertainty);

        return Meter(PanelText.SessionBarLabel, snapshot.SessionPercent, snapshot, banner) with
        {
            ResetLine = line,
            ResetLineWithAction = line,
        };
    }

    /// <summary>
    /// The weekly meter, with the reset line and the boost chip that only this one carries.
    ///
    /// <para>The reset line has three states, and the middle one is the point of the feature
    /// (ADR-0014):</para>
    /// <list type="bullet">
    /// <item><b>Known</b> — the session line's shape plus the weekday, and labelled when the
    /// user entered it, so an unexpectedly precise time is explained rather than mysterious
    /// (issue #186). Never marked <c>~</c>: both remaining sources report the instant.</item>
    /// <item><b>Not known</b> — nothing has reported one and nothing was entered. The row says
    /// so and carries the one step that ends it, in the hint or inline.</item>
    /// <item><b>No plan data</b> — nothing to act on, and the banner above already explains
    /// why, so there is no line at all.</item>
    /// </list>
    /// </summary>
    /// <param name="settings">
    /// The user's settings, for the manually entered reset alone — null from a head that does
    /// not receive them, which reads the same as not having entered one.
    /// </param>
    public static PanelMeterView WeeklyMeter(
        UsageSnapshot snapshot,
        PanelBanner? banner,
        TraySettings? settings,
        BoostNotices? boost,
        DateTimeOffset utcNow,
        TimeZoneInfo local)
    {
        var meter = Meter(PanelText.WeeklyBarLabel, snapshot.WeeklyPercent, snapshot, banner) with
        {
            Chip = Chip(boost, utcNow, local),
        };

        if (snapshot.WeeklyResetAtUtc is { } reset)
        {
            // A user-supplied reset is labelled. This used to also require a zero bracket, to
            // exclude a derived reset on a machine that happened to have an entered one; that
            // distinction died with the derivation (ADR-0014), so the term was always true.
            var line = PanelText.WeeklyReset(reset, utcNow, local)
                + (settings?.WeeklyReset is not null ? $" · {PanelText.WeeklyResetUserSupplied}" : "");

            return meter with
            {
                ResetLine = line,
                ResetLineWithAction = line,
                ResetHint = settings?.WeeklyReset is not null
                    ? PanelText.WeeklyResetUserSuppliedHint
                    : null,
            };
        }

        if (!IsAuthoritative(snapshot))
        {
            return meter;
        }

        return meter with
        {
            ResetLine = PanelText.WeeklyResetUnknown,
            // The hint names two routes and will not fit on a row. A head that cannot hover
            // gets the short one spliced in instead, because the state is only worth naming if
            // the reader is told what ends it.
            ResetLineWithAction =
                $"{PanelText.WeeklyResetUnknown} · {PanelText.WeeklyResetUnknownAction}",
            ResetHint = PanelText.WeeklyResetUnknownHint,
        };
    }

    /// <summary>
    /// The four statistics tiles, each with the per-model split behind its total.
    ///
    /// <para>Both today tiles share <see cref="PanelStatistics.ModelsToday"/> and both 31-day
    /// tiles share <see cref="PanelStatistics.Models31Days"/> — one list per window, split two
    /// ways, because a token breakdown and a value breakdown of the same usage are the same
    /// rows measured differently.</para>
    ///
    /// <para>The caveat rules are the part that was stated twice. Partial history and an
    /// unpriced model are conditions that pass, so they ride on the tiles they qualify; the
    /// tiles excluding chat and cloud sessions is the standing shape of the data and belongs
    /// under the whole block, which is why no tile carries it (issue #235).</para>
    /// </summary>
    public static PanelTilesView Tiles(PanelStatistics stats)
    {
        // Partial history states its coverage — a small number without this caveat reads as
        // low usage rather than short history (ADR-0006). An unpriced model is the same class
        // of caveat: the total is real but incomplete.
        var coverage = TokenText.Caveat(stats);

        // The "not money charged" framing is only true for plan usage. Off-plan work bills at
        // API rates, so the value tiles' label flips with it and they name the mixture.
        var offPlan = stats.IsOffPlan;
        var offPlanNote = OffPlanText.OffPlanNote(offPlan);

        return new PanelTilesView(
            // Today's token tile qualifies nothing. It carried a "(UTC)" hint for one release,
            // while the figure was a UTC day under a local-time header (issue #210); the figure
            // is the reader's own day now. The Linux head also put the off-plan note here,
            // which the Windows one never did — the mixture is what the value tiles are about,
            // and a token count is the same count either way.
            Tile(
                TokenText.TokensTodayLabel,
                UsageFormatter.Tokens(stats.TokensToday),
                "",
                stats.ModelsToday,
                BreakdownMeasure.Tokens,
                stats.ModelColourOrder),
            Tile(
                TokenText.EstTodayLabel(offPlan),
                UsageFormatter.Usd(stats.EstTodayUsd),
                offPlanNote,
                stats.ModelsToday,
                BreakdownMeasure.EstValue,
                stats.ModelColourOrder),
            Tile(
                TokenText.Tokens31DaysLabel,
                UsageFormatter.Tokens(stats.Tokens31Days),
                coverage,
                stats.Models31Days,
                BreakdownMeasure.Tokens,
                stats.ModelColourOrder),
            Tile(
                TokenText.Est31DaysLabel,
                UsageFormatter.Usd(stats.Est31DaysUsd),
                coverage,
                stats.Models31Days,
                BreakdownMeasure.EstValue,
                stats.ModelColourOrder));
    }

    /// <summary>
    /// One tile, both faces. Public because the Windows verification renders build tiles
    /// directly from hand-made slices — a sample assembled any other way would be a picture of
    /// something other than the shipped tile.
    /// </summary>
    /// <param name="slices">
    /// The window's full per-model list, not its segments. The unpriced models a value chart
    /// has to leave out are read from this: by the time
    /// <see cref="ModelBreakdown.Segments"/> has run they are gone, and a tile that reported
    /// none would imply its total was the whole picture (rule 6).
    /// </param>
    /// <param name="colourOrder">
    /// <see cref="PanelStatistics.ModelColourOrder"/> — the panel-wide slot order, so a model
    /// keeps one colour across all four tiles.
    /// </param>
    public static PanelTileView Tile(
        string label,
        string value,
        string note,
        IReadOnlyList<ModelSlice> slices,
        BreakdownMeasure measure,
        IReadOnlyList<string> colourOrder)
    {
        var segments = ModelBreakdown.Segments(slices, measure, colourOrder);
        var total = segments.Sum(s => ModelBreakdown.Measure(s, measure));

        // Only a value chart can exclude a model — a token count exists for every model that
        // recorded one, so asking on a token tile would always answer "none" and the caption
        // would be deciding something it had no say in.
        var unpriced = measure == BreakdownMeasure.EstValue
            ? ModelBreakdown.Unpriced(slices)
            : [];

        return new PanelTileView(
            label,
            value,
            note,
            [.. segments.Select(s => Segment(s, measure, total, colourOrder))],
            TokenText.ModelBreakdownCaption(unpriced.Count),
            unpriced.Count > 0 ? TokenText.UnpricedModelsHint(unpriced) : null);
    }

    private static PanelTileSegmentView Segment(
        ModelSlice slice, BreakdownMeasure measure, double total, IReadOnlyList<string> colourOrder) =>
        new(
            // A non-null FoldedCount is what marks the remainder — never a word in Model or
            // DisplayName, both of which Core leaves empty on that segment.
            slice.FoldedCount is not null ? TokenText.OtherSegmentLabel : slice.DisplayName,
            Figure(slice, measure),
            slice.FoldedCount is int folded ? TokenText.FoldedModelsLabel(folded) : slice.Model,
            // Guarded rather than assumed non-zero: Segments drops zero-measure models, so a
            // zero total means no segments at all and this never runs — but a share is a
            // division and the guard costs less than finding out that changed.
            total > 0 ? ModelBreakdown.Measure(slice, measure) / total : 0,
            ModelBreakdown.SlotFor(slice.Model, colourOrder));

    /// <summary>One segment's measure, worded as the tile's measure is worded.</summary>
    private static string Figure(ModelSlice slice, BreakdownMeasure measure) => measure switch
    {
        BreakdownMeasure.Tokens => UsageFormatter.Tokens(slice.Tokens),
        BreakdownMeasure.EstValue => UsageFormatter.Usd(slice.EstUsd),
        _ => "",
    };

    /// <summary>
    /// What both meters share: the figure, the band and the fill, or the placeholder that
    /// stands in for all three. A percentage is shown only when the snapshot is authoritative
    /// — an estimate has no meter to fill (ADR-0002).
    /// </summary>
    private static PanelMeterView Meter(
        string label, int? reported, UsageSnapshot snapshot, PanelBanner? banner)
    {
        var percent = IsAuthoritative(snapshot) ? reported : null;

        return new PanelMeterView(
            label,
            percent,
            percent is { } p ? PanelText.BarPercentUsed(p) : banner?.GaugePlaceholder ?? PanelBanner.UnknownGauge,
            percent is { } band ? UsageLevels.Classify(band) : null,
            percent is { } fill ? Math.Clamp(fill, 0, 100) : null,
            ResetLine: null,
            ResetLineWithAction: null,
            ResetHint: null,
            Chip: null);
    }

    /// <summary>
    /// The chip, when Claude Code has cached a promo for the weekly meter and it has not
    /// ended. The reader's own day decides that, which is why the zone is a parameter rather
    /// than the machine's.
    /// </summary>
    private static PanelBoostChipView? Chip(
        BoostNotices? notices, DateTimeOffset utcNow, TimeZoneInfo local)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcNow, local).DateTime);

        return notices?.For(BoostNotice.WeeklyBar, today, utcNow) is { } notice
            ? new PanelBoostChipView(
                BoostText.BoostChip(notice, utcNow, local),
                BoostText.BoostCard(notice, notices.FetchedAtUtc, local))
            : null;
    }
}
