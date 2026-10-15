using System.Globalization;
using OView.Core.Models;
using OView.Core.Providers.CachedUsage;
using OView.Core.Providers.PlanHistory;
using OView.Presentation.Text;

namespace OView.Presentation.Panel;

/// <summary>
/// Builds the panel's header, its two meters, its four statistics tiles and its 31-day graph,
/// once, for both heads.
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
            snapshot.SessionResetAtUtcValue.Value, utcNow, local, snapshot.SessionResetUncertainty);

        return Meter(PanelText.SessionBarLabel, snapshot.SessionPercentValue, snapshot, banner) with
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
        var meter = Meter(PanelText.WeeklyBarLabel, snapshot.WeeklyPercentValue, snapshot, banner) with
        {
            Chip = Chip(boost, utcNow, local),
        };

        if (snapshot.WeeklyResetAtUtcValue.Value is { } reset)
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
        var (offPlanLabel, offPlanNote) = ValueTile(stats);

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
                offPlanLabel,
                UsageFormatter.Usd(stats.EstTodayValue.Value),
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
                UsageFormatter.Usd(stats.Est31DaysValue.Value),
                coverage,
                stats.Models31Days,
                BreakdownMeasure.EstValue,
                stats.ModelColourOrder));
    }

    /// <summary>
    /// The off-plan banner, the standing 31-day section and the value tile's relabel
    /// (<see cref="PanelOffPlanView"/>).
    ///
    /// <para>The banner's claim about billing is the part that had to stop being built per
    /// head: it follows a setting read from Claude Code's own cache, and the wording that
    /// setting licenses is a statement about the reader's money (issue #259). Both readings are
    /// taken from the one cached block rather than passed separately, so the sentence and the
    /// stamp on it can never describe different reads.</para>
    /// </summary>
    /// <param name="cachedUsage">
    /// What Claude Code last cached, or null from a machine where nothing was readable — which
    /// reads as <see cref="ExtraUsageState.Unknown"/> and claims nothing either way.
    /// </param>
    public static PanelOffPlanView OffPlan(
        PanelStatistics stats,
        CachedUtilization? cachedUsage,
        DateTimeOffset utcNow,
        TimeZoneInfo local)
    {
        var (valueTileLabel, valueTileNote) = ValueTile(stats);
        var extraUsage = (cachedUsage?.ExtraUsage ?? ExtraUsageStatus.Unknown).State;

        // Guarded on the divergence rather than on IsOffPlan alone. The two cannot disagree —
        // IsOffPlan reads the same field — but the banner needs the result itself, and a null
        // check states that it is the result, not the verdict, the wording is built from.
        var banner = stats.Divergence is { IsOffPlan: true } divergence
            ? new PanelOffPlanBannerView(
                OffPlanText.OffPlanTitle(divergence.State, extraUsage),
                OffPlanText.OffPlanDetail(
                    divergence, extraUsage, cachedUsage?.FetchedAtUtc, utcNow, local),
                OffPlanText.UsageSettingsLinkLabel,
                OffPlanText.UsageSettingsUrl)
            : null;

        return new PanelOffPlanView(
            banner,
            // The day count is the window the figure beside it covers, exactly as the chart's
            // heading above is. A literal 31 here and a shorter window in the data would read
            // as a full month of spend.
            OffPlanText.OffPlanHeading(stats.WindowDays),
            stats.HasCreditUsage ? PanelText.CreditBadgeRecorded : PanelText.CreditBadgeNoneRecorded,
            PanelText.CreditSpendLabel,
            // Zero is stated, not blanked: nothing credit-billed was recorded in the window,
            // which is a measurement rather than an absence of one. The coverage caveat beside
            // it is what qualifies how much of the window was actually watched.
            UsageFormatter.Usd(stats.HasCreditUsage ? stats.EstCredit31DaysValue.Value : 0m),
            stats.HasCreditUsage ? TokenText.CoverageNote(stats) : "",
            OffPlanText.OffPlanHint(stats.HasCreditUsage),
            valueTileLabel,
            valueTileNote);
    }

    /// <summary>
    /// The value tile's label and caveat for the window's off-plan state — read both by
    /// <see cref="Tiles"/>, which draws the tile, and by <see cref="OffPlan"/>, which is where
    /// the state is otherwise described. One definition, because a tile labelled "Est. spend"
    /// beside a banner that is not showing describes a different window than the banner does.
    /// </summary>
    private static (string Label, string Note) ValueTile(PanelStatistics stats) =>
        (TokenText.EstTodayLabel(stats.IsOffPlan), OffPlanText.OffPlanNote(stats.IsOffPlan));

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
        BreakdownMeasure.EstValue => UsageFormatter.Usd(slice.EstUsdValue.Value),
        _ => "",
    };

    /// <summary>
    /// The 31-day graph: every column measured and dated, every gridline placed, every hover
    /// card worded (<see cref="PanelGraphView"/>).
    ///
    /// <para>An empty series gives a view with no columns, which is a chart neither head
    /// draws. It is not an error state — a store that has recorded nothing is a new install —
    /// and the no-data banner above the chart is what says so.</para>
    /// </summary>
    /// <param name="zone">
    /// The reader's timezone, or a pinned one for verification renders. Everything the chart
    /// places is taken in it: the columns are local days and so a gridline drawn over them
    /// has to be positioned in the same frame (<see cref="LocalDays"/>, issue #211).
    /// </param>
    public static PanelGraphView Graph(PanelStatistics stats, UsageSnapshot snapshot, TimeZoneInfo zone)
    {
        var heading = PanelText.UsageWindowHeading(stats.WindowDays);
        var series = stats.DailySeries;
        if (series.Count == 0)
        {
            return new PanelGraphView(heading, [], []);
        }

        var weeks = PlanWeeks.ForSeries(series, snapshot, zone);

        // Floored at 1 so a window with nothing in it divides rather than throwing. Every
        // column is then zero-height, which is what a store with no usage should draw.
        var peak = Math.Max(1, series.Max(d => d.OutputTokens));
        var weekPeak = series
            .Where(d => !d.PreInstall)
            .GroupBy(weeks.IndexOf)
            .ToDictionary(g => g.Key, g => Math.Max(1, g.Max(d => d.OutputTokens)));

        var columns = new List<PanelGraphColumnView>(series.Count);
        foreach (var day in series)
        {
            var bar = !day.PreInstall && day.OutputTokens > 0;
            columns.Add(new PanelGraphColumnView(
                string.Create(CultureInfo.InvariantCulture, $"{day.Date:d MMM}"),
                !day.PreInstall,
                bar ? day.OutputTokens / (double)peak : 0,
                bar ? day.OutputTokens / (double)weekPeak[weeks.IndexOf(day)] : 0,
                bar ? TokenText.DayBarTokens(day.OutputTokens) : "",
                bar ? string.Create(CultureInfo.InvariantCulture, $"{day.Date:dddd d MMMM}") : ""));
        }

        var columnOf = series
            .Select((day, index) => (day.Date, index))
            .ToDictionary(t => t.Date, t => t.index);

        var boundaries = new List<PanelGraphBoundaryView>();
        foreach (var boundary in weeks.Boundaries)
        {
            if (!columnOf.TryGetValue(boundary.Day, out var column))
            {
                continue;   // outside the plotted range
            }

            var fraction = boundary.FractionOfDay;
            if (column == 0 && fraction <= 0)
            {
                continue;   // the chart's own left edge, where a line reads as a border
            }

            boundaries.Add(new PanelGraphBoundaryView(
                column,
                fraction,
                weeks.IsPlanDerived
                    ? string.Create(CultureInfo.InvariantCulture,
                        $"{TimeZoneInfo.ConvertTime(boundary.AtUtc, zone):ddd d MMM · HH:mm}")
                    : "",
                weeks.IsPlanDerived ? PanelText.WeeklyResetMarker : PanelText.CalendarWeekMarker));
        }

        return new PanelGraphView(heading, columns, boundaries);
    }

    /// <summary>
    /// Where one week boundary lands on the chart: the day column it falls in, and how far
    /// through that column, since a plan reset happens at a time of day rather than at
    /// midnight.
    ///
    /// <para>Both answers are taken in the column's own frame — a <b>local</b> day, of
    /// whatever length the timezone says it was (issue #211). Dividing by a flat 24 hours
    /// would put the line up to an hour off its true position on the two DST days, and off in
    /// the same direction for every day after them.</para>
    /// </summary>
    private readonly record struct WeekBoundary(DateTimeOffset AtUtc, TimeZoneInfo Zone)
    {
        public DateOnly Day => LocalDays.DateOf(AtUtc, Zone);

        public double FractionOfDay => LocalDays.FractionThrough(AtUtc, Day, Zone);
    }

    /// <summary>
    /// The week bands the 31-day graph is divided into.
    ///
    /// <para>Originally these were calendar weeks (Mon–Sun) with a stated reason: the plan's
    /// true weekly boundary was not derivable, so Monday was an honest visual reference rather
    /// than a claim about the plan. That premise no longer holds — ADR-0011 derives the weekly
    /// reset — so the bands follow the plan's own boundary wherever it is known, which is what
    /// the graph was always trying to convey. Mondays remain the fallback, and say so on
    /// hover, because until a reset has been observed the real boundary is still genuinely
    /// unknown (rule 6).</para>
    ///
    /// <para>Boundaries are derived by stepping the cadence back from the predicted next
    /// reset, so they cover the whole window including days before O-view was installed — the
    /// log only holds resets it was running for.</para>
    /// </summary>
    private sealed class PlanWeeks
    {
        private readonly IReadOnlyList<DateTimeOffset> _boundaries;
        private readonly TimeZoneInfo _zone;

        private PlanWeeks(IReadOnlyList<DateTimeOffset> boundaries, bool isPlanDerived, TimeZoneInfo zone)
        {
            _boundaries = boundaries;
            _zone = zone;
            IsPlanDerived = isPlanDerived;
        }

        /// <summary>False when these are calendar weeks standing in for an unknown plan week.</summary>
        public bool IsPlanDerived { get; }

        public IEnumerable<WeekBoundary> Boundaries => _boundaries.Select(b => new WeekBoundary(b, _zone));

        public static PlanWeeks ForSeries(
            IReadOnlyList<DayUsage> series, UsageSnapshot snapshot, TimeZoneInfo zone)
        {
            // The span the columns actually cover, taken from the timezone. The columns are
            // local days, so their first and last instants are not local midnight interpreted
            // as UTC — which would clip or over-collect boundaries by the offset.
            var fromUtc = LocalDays.StartUtc(series[0].Date, zone);
            var toUtc = LocalDays.EndUtc(series[^1].Date, zone);

            if (snapshot.WeeklyResetAtUtcValue.Value is { } next &&
                snapshot.WeeklyResetPeriod is { } period)
            {
                return new PlanWeeks(
                    WeeklyWindow.BoundariesWithin(next, period, fromUtc, toUtc),
                    isPlanDerived: true,
                    zone);
            }

            // Fallback: the start of each local Monday in range.
            var mondays = series
                .Select(d => d.Date)
                .Where(d => d.DayOfWeek == DayOfWeek.Monday)
                .Select(d => LocalDays.StartUtc(d, zone))
                .ToList();
            return new PlanWeeks(mondays, isPlanDerived: false, zone);
        }

        /// <summary>
        /// Which band a day belongs to, for the purpose of normalising its colour.
        ///
        /// <para>A day that contains a boundary genuinely belongs to neither band — its tokens
        /// are split across two weeks and the rollups are daily, so the split is not
        /// recoverable. It is assigned to whichever band holds the majority of the day, which
        /// is what testing the MIDPOINT does. That is an approximation, and the only one
        /// available at this grain; it affects shading alone, never a stated figure.</para>
        /// </summary>
        public int IndexOf(DayUsage day)
        {
            var start = LocalDays.StartUtc(day.Date, _zone);
            var midday = start + (LocalDays.EndUtc(day.Date, _zone) - start) / 2;
            return _boundaries.Count(boundary => boundary <= midday);
        }
    }

    /// <summary>
    /// What both meters share: the figure, the band and the fill, or the placeholder that
    /// stands in for all three. A percentage is shown only when the snapshot is authoritative
    /// — an estimate has no meter to fill (ADR-0002).
    /// </summary>
    private static PanelMeterView Meter(
        string label, UsagePercent reported, UsageSnapshot snapshot, PanelBanner? banner)
    {
        // Integer precision only, which is what the meter has always carried — the typed value
        // widens to a double so one percent type can serve figures that are not whole numbers,
        // and these two are.
        var percent = IsAuthoritative(snapshot) ? (int?)reported.Value : null;

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
