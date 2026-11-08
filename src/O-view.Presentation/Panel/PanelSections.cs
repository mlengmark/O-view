using System.Globalization;
using OView.Core.Models;
using OView.Core.Providers.CachedUsage;
using OView.Core.Providers.Jsonl;
using OView.Core.Providers.PlanHistory;
using OView.Presentation.Text;

namespace OView.Presentation.Panel;

/// <summary>
/// Builds the panel's header, its meters, its four statistics tiles and its 31-day graph,
/// once, for both heads.
///
/// <para><b>Why this exists above <see cref="PanelText"/>.</b> That class already held every
/// sentence, and the heads still disagreed, not about wording but about which sentence a
/// state calls for. Each one decided for itself when a figure counted as authoritative, when
/// a reset row was worth showing, which placeholder stood in for a missing percentage, and
/// what day it was for the purpose of a promo. Those are the same class of decision as the
/// wording (issues #55, #56): a statement about the user's usage, made twice.</para>
///
/// <para>What stays with each head is everything a toolkit has an opinion about, fonts,
/// spacing, how few pixels a fill may shrink to, whether a hover card can exist at all. A
/// view model here carries the band a figure falls in, never the colour it is painted.</para>
/// </summary>
public static class PanelSections
{
    /// <summary>
    /// Whether the snapshot's percentages are the account's own figures rather than a local
    /// estimate or nothing at all.
    ///
    /// <para>It gates more than the meters, the no-data banner and the token-scope note ask
    /// it too, so it is stated once here rather than spelled out beside each use. Both heads
    /// had their own copy of this expression, which is one edit away from two panels
    /// disagreeing about whether a reading may be shown as fact.</para>
    /// </summary>
    public static bool IsAuthoritative(UsageSnapshot snapshot) =>
        snapshot.Source is DataSource.Live or DataSource.Stale;

    /// <summary>
    /// The "Tokens used" section (<see cref="PanelTokenBarsView"/>): what was billed, beside
    /// tiles that headline only output (issue #253). Built once here; each head only lays it
    /// out.
    /// </summary>
    /// <param name="scopeReport">What the transcript scan found, for the coverage line.</param>
    public static PanelTokenBarsView TokenBars(PanelStatistics stats, TranscriptScopeReport scopeReport)
    {
        var today = stats.CompositionToday;
        var window31 = stats.Composition31Days;

        return new PanelTokenBarsView(
            Show: today.HasTokens || window31.HasTokens,
            Heading: TokenText.TokensUsedHeading,
            BarsLabel: TokenText.TokenViewBarsLabel,
            BreakdownLabel: TokenText.TokenViewBreakdownLabel,
            Today: TokenBar(today, TokenText.TokensUsedTodayLabel, TokenText.TokenWindowToday),
            Window31: TokenBar(window31, TokenText.TokensUsed31DaysLabel, TokenText.TokenWindow31Days),
            Breakdown: Breakdown(today, window31),
            CoverageLine: scopeReport.CoverageLine());
    }

    private static PanelTokenBarView TokenBar(TokenComposition composition, string heading, string window)
    {
        // Only the kinds with any tokens draw. The legend carries every exact figure the
        // track's width floors cannot (see TokenBarGeometry), so it is not optional decoration.
        var segments = composition.InDisplayOrder.Where(s => s.Tokens > 0).ToList();

        return new PanelTokenBarView(
            heading, window, UsageFormatter.Tokens(composition.Total), composition, segments,
            segments.Select(s => new PanelTokenLegendEntry(
                s.Kind, $"{TokenText.TokenKindLabel(s.Kind)} {UsageFormatter.Tokens(s.Tokens)}")).ToList());
    }

    /// <summary>
    /// The breakdown table: one row per kind, with a share column per window. Two share
    /// columns rather than one shared, because the windows have measurably different shapes:
    /// cache write ran 10.3% of a day against 0.93% of the month on the machine this was
    /// designed against, and one column between two token columns would be read against
    /// whichever it sat nearer, which is the ambiguity issue #169 was reported about.
    /// </summary>
    private static IReadOnlyList<PanelTokenBreakdownRow> Breakdown(TokenComposition today, TokenComposition window31)
    {
        var rows = new List<PanelTokenBreakdownRow>
        {
            new(PanelTokenRowKind.Header, null,
                TokenText.TokenBreakdownKindHeader, TokenText.TokenBreakdownTodayHeader,
                TokenText.TokenBreakdownShareHeader, TokenText.TokenBreakdown31DaysHeader,
                TokenText.TokenBreakdownShareHeader),
        };

        // Driven off today's order so both columns describe the same row, even where a kind
        // is present in one window and absent from the other.
        foreach (var slice in (today.HasTokens ? today : window31).InDisplayOrder)
        {
            var mine = today.InDisplayOrder.First(s => s.Kind == slice.Kind);
            var other = window31.InDisplayOrder.First(s => s.Kind == slice.Kind);

            rows.Add(new(PanelTokenRowKind.Kind, slice.Kind,
                TokenText.TokenKindLabel(slice.Kind),
                UsageFormatter.Tokens(mine.Tokens), TokenText.TokenShare(mine.Share),
                UsageFormatter.Tokens(other.Tokens), TokenText.TokenShare(other.Share)));
        }

        rows.Add(new(PanelTokenRowKind.Total, null,
            TokenText.TokenBreakdownTotalLabel,
            UsageFormatter.Tokens(today.Total), TokenText.TokenShare(today.HasTokens ? 1 : 0),
            UsageFormatter.Tokens(window31.Total), TokenText.TokenShare(window31.HasTokens ? 1 : 0)));

        return rows;
    }

    /// <summary>
    /// The note beneath the tiles when they read zero while the plan meters show real usage:
    /// the tiles are measuring a source this user does not feed, not measuring zero usage, so
    /// it says which source. Null when the note does not apply.
    ///
    /// <para>Derived from what the scan actually resolved, never a literal. It used to name a
    /// hard-coded <c>%USERPROFILE%\.claude\projects</c> and only Claude Code, so a Cowork
    /// user, whose transcripts are read, was told their source was not counted, and a
    /// packaged Desktop install was pointed at a path O-view never searched (issue #58).</para>
    ///
    /// <para>Keyed on the token total, not on recorded days. Those meant the same thing while
    /// recorded days counted days with usage; now that it counts days observed (issue #142),
    /// a store older than the window reports full coverage while the tiles still read zero,
    /// which is the exact case this note is for.</para>
    /// </summary>
    public static string? ScopeNote(PanelStatistics stats, UsageSnapshot snapshot, TranscriptScopeReport scopeReport) =>
        stats.Tokens31Days == 0 && IsAuthoritative(snapshot) &&
        snapshot.SessionPercentValue is { Status: not UsageValueStatus.Unavailable, Value: > 0 }
            ? scopeReport.Explain()
            : null;

    /// <summary>The header: app name, freshness, and the account block.</summary>
    public static PanelHeaderView Header(
        UsageSnapshot snapshot, ClaudeAccount? account, DateTimeOffset utcNow, TimeZoneInfo local) =>
        new(
            PanelHeaderView.AppName,
            PanelText.Freshness(snapshot, utcNow, local),
            account?.DisplayName ?? PanelText.AccountUnknown,
            // Null means "no row", and an empty string means the same thing. The two heads
            // tested it differently, one for null, one for length, and agreed only because
            // ClaudeAccount.ReadString already maps an empty property to null. This states the
            // rule rather than resting on that.
            account?.Email is { Length: > 0 } email ? email : null,
            // Tier from organizationType, seatTier is empty and would render blank (rule 8).
            account?.Tier ?? PanelText.TierUnknown);

    /// <summary>
    /// The rolling five-hour meter. Its reset line is unknown until a window start has been
    /// observed, and carries a <c>~</c> when the start was only bracketed (ADR-0011, issue
    /// #180), both of which <see cref="PanelText.SessionReset"/> decides.
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
    /// <item><b>Known</b>, the session line's shape plus the weekday, and labelled when the
    /// user entered it, so an unexpectedly precise time is explained rather than mysterious
    /// (issue #186). Never marked <c>~</c>: both remaining sources report the instant.</item>
    /// <item><b>Not known</b>, nothing has reported one and nothing was entered. The row says
    /// so and carries the one step that ends it, in the hint or inline.</item>
    /// <item><b>No plan data</b>, nothing to act on, and the banner above already explains
    /// why, so there is no line at all.</item>
    /// </list>
    /// </summary>
    /// <param name="settings">
    /// The user's settings, for the manually entered reset alone, null from a head that does
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
    /// One meter per model with a weekly limit of its own, in the order Claude Code lists them,
    /// and none for an account with no such limit or a machine with no cached block.
    ///
    /// <para><b>It takes no snapshot, deliberately.</b> The session and weekly meters ask
    /// <see cref="IsAuthoritative"/>, because their figures belong to the snapshot that won.
    /// Snapshots are chosen whole and Claude Desktop's carries no model limits, so a bar gated on
    /// the winner would blink out each time Desktop's reading was chosen, over an account whose
    /// limit had not changed. The cached block is read here directly, so the same block gives
    /// the same bars whichever source won.</para>
    ///
    /// <para><b>A row with no current figure still draws.</b>
    /// <see cref="CachedUtilizationProvider.CurrentWindowPercent(UtilizationBar?, DateTimeOffset, TimeSpan)"/>
    /// is the rule the two meters' cached figures pass through, so a passed reset or an aged zero
    /// reads unknown with no fill. Dropping the bar instead would say the limit went away, when
    /// all that is known is that the reading is old (rule 6).</para>
    /// </summary>
    /// <param name="cachedUsage">What Claude Code last cached, or null when nothing was readable.</param>
    public static IReadOnlyList<PanelMeterView> ModelLimitMeters(
        CachedUtilization? cachedUsage, DateTimeOffset utcNow, TimeZoneInfo local)
    {
        if (cachedUsage is null)
        {
            return [];
        }

        var age = utcNow - cachedUsage.FetchedAtUtc;

        return [.. cachedUsage.ModelLimits.Limits.Select(limit => ModelLimitMeter(limit, age, utcNow, local))];
    }

    /// <summary>
    /// One model's meter. The reset line is the weekly meter's, with no <c>~</c>, and there is
    /// none once the reset has passed: nothing here steps it forward, because a stepped instant
    /// would be an inference wearing a reported value's precision.
    /// </summary>
    private static PanelMeterView ModelLimitMeter(
        ModelLimit limit, TimeSpan age, DateTimeOffset utcNow, TimeZoneInfo local)
    {
        var line = limit.Bar.ResetsAtUtc is { } reset && reset > utcNow
            ? PanelText.WeeklyReset(reset, utcNow, local)
            : null;

        return Meter(
            PanelText.ModelWeeklyBarLabel(limit.Model),
            CachedUtilizationProvider.CurrentWindowPercent(limit.Bar, utcNow, age),
            PanelBanner.UnknownGauge) with
        {
            ResetLine = line,
            ResetLineWithAction = line,
        };
    }

    /// <summary>
    /// The four statistics tiles, each with the per-model split behind its total.
    ///
    /// <para>Both today tiles share <see cref="PanelStatistics.ModelsToday"/> and both 31-day
    /// tiles share <see cref="PanelStatistics.Models31Days"/>, one list per window, split two
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
        // Partial history states its coverage, a small number without this caveat reads as
        // low usage rather than short history (ADR-0006). An unpriced model is the same class
        // of caveat: the total is real but incomplete.
        var coverage = TokenText.Caveat(stats);

        // The "not money charged" framing is only true for plan usage. Off-plan work bills at
        // API rates, so the value tiles' label flips with it and they name the mixture.
        var (offPlanLabel, offPlanNote) = ValueTile(stats);

        return new PanelTilesView(
            // Today's token tile qualifies nothing. It carried a "(UTC)" hint for one release,
            // while the figure was a UTC day under a local-time header (issue #210); the figure
            // is the reader's own day now. The off-plan note belongs to the value tiles alone:
            // the mixture is what they are about, and a token count is the same count either
            // way. The Linux head used to add it here too; both heads now show this reading.
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
    /// The off-plan banner, the standing section and the value tile's relabel
    /// (<see cref="PanelOffPlanView"/>).
    ///
    /// <para>The banner's claim about billing is the part that had to stop being built per
    /// head: it follows a setting read from Claude Code's own cache, and the wording that
    /// setting licenses is a statement about the reader's money (issue #259). Both readings are
    /// taken from the one cached block rather than passed separately, so the sentence and the
    /// stamp on it can never describe different reads.</para>
    /// </summary>
    /// <param name="cachedUsage">
    /// What Claude Code last cached, or null from a machine where nothing was readable, which
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

        // Guarded on the divergence rather than on IsOffPlan alone. The two cannot disagree,
        // IsOffPlan reads the same field, but the banner needs the result itself, and a null
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
            banner, OffPlanSection(stats, cachedUsage, utcNow, local), valueTileLabel, valueTileNote);
    }

    /// <summary>
    /// The standing section, built from one figure or the other and never both.
    ///
    /// <para><b>A model limit decides which.</b> Without one, a credit-billed model's usage
    /// falls outside the plan, so pricing it says what fell outside. With one, the same usage
    /// draws from that model's own limit, so the estimate would call plan usage off-plan, and
    /// only what Claude itself reports says anything left the plan. Any row counts, a passed
    /// reset included: that says the reading is old, not that the limit went away.</para>
    ///
    /// <para>They are not added, because they do not measure the same thing: one is a valuation
    /// of a window of transcripts, the other an amount over a period nothing states.</para>
    /// </summary>
    private static PanelOffPlanSectionView? OffPlanSection(
        PanelStatistics stats,
        CachedUtilization? cachedUsage,
        DateTimeOffset utcNow,
        TimeZoneInfo local)
    {
        if (cachedUsage is { ModelLimits.Limits.Count: > 0 })
        {
            return ReportedSpendSection(cachedUsage, utcNow, local);
        }

        // Gated on the recorded tokens, not on the price: usage with no published rate has an
        // unknown figure, and hiding it would state a zero by omission (rule 6).
        return stats.HasCreditUsage
            ? new PanelOffPlanSectionView(
                // The day count is the window the figure beside it covers, exactly as the
                // chart's heading above is. A literal 31 here and a shorter window in the data
                // would read as a full month of spend.
                OffPlanText.OffPlanHeading(stats.WindowDays),
                PanelText.CreditBadgeRecorded,
                PanelText.CreditSpendLabel,
                UsageFormatter.Usd(stats.EstCredit31DaysValue.Value),
                TokenText.CoverageNote(stats),
                OffPlanText.OffPlanHint)
            : null;
    }

    /// <summary>
    /// The section on an account with a model limit: the amount decides, at any setting, and
    /// a zero hides it however heavily the model was used. With no amount to go on the setting
    /// decides instead: on means money may have moved, so the figure is unknown rather than
    /// a zero nobody reported (rule 6), and off or not stated gives no reason to raise it.
    /// </summary>
    private static PanelOffPlanSectionView? ReportedSpendSection(
        CachedUtilization cachedUsage, DateTimeOffset utcNow, TimeZoneInfo local)
    {
        if (cachedUsage.Spend.Spend is { } spend)
        {
            return spend.Amount == 0
                ? null
                : ReportedSpendView(
                    UsageFormatter.Reported(spend.Amount, spend.Currency),
                    OffPlanText.ReportedSpendHint(cachedUsage.FetchedAtUtc, utcNow, local));
        }

        return cachedUsage.ExtraUsage.State == ExtraUsageState.Enabled
            ? ReportedSpendView(
                UsageFormatter.Usd(null),
                OffPlanText.UnreadableSpendHint(cachedUsage.FetchedAtUtc, utcNow, local))
            : null;
    }

    /// <summary>
    /// No coverage note, because the figure comes with no window to be partial against, and no
    /// day count in the heading for the same reason (<see cref="OffPlanText.ReportedSpendHeading"/>).
    /// </summary>
    private static PanelOffPlanSectionView ReportedSpendView(string value, string hint) =>
        new(
            OffPlanText.ReportedSpendHeading,
            PanelText.ReportedSpendBadge,
            PanelText.ReportedSpendLabel,
            value,
            Coverage: "",
            hint);

    /// <summary>
    /// The value tile's label and caveat for the window's off-plan state, read both by
    /// <see cref="Tiles"/>, which draws the tile, and by <see cref="OffPlan"/>, which is where
    /// the state is otherwise described. One definition, because a tile labelled "Est. spend"
    /// beside a banner that is not showing describes a different window than the banner does.
    /// </summary>
    private static (string Label, string Note) ValueTile(PanelStatistics stats) =>
        (TokenText.EstTodayLabel(stats.IsOffPlan), OffPlanText.OffPlanNote(stats.IsOffPlan));

    /// <summary>
    /// One tile, both faces. Public because the Windows verification renders build tiles
    /// directly from hand-made slices, a sample assembled any other way would be a picture of
    /// something other than the shipped tile.
    /// </summary>
    /// <param name="slices">
    /// The window's full per-model list, not its segments. The unpriced models a value chart
    /// has to leave out are read from this: by the time
    /// <see cref="ModelBreakdown.Segments"/> has run they are gone, and a tile that reported
    /// none would imply its total was the whole picture (rule 6).
    /// </param>
    /// <param name="colourOrder">
    /// <see cref="PanelStatistics.ModelColourOrder"/>, the panel-wide slot order, so a model
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

        // Only a value chart can exclude a model, a token count exists for every model that
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
            // A non-null FoldedCount is what marks the remainder, never a word in Model or
            // DisplayName, both of which Core leaves empty on that segment.
            slice.FoldedCount is not null ? TokenText.OtherSegmentLabel : slice.DisplayName,
            Figure(slice, measure),
            slice.FoldedCount is int folded ? TokenText.FoldedModelsLabel(folded) : slice.Model,
            // Guarded rather than assumed non-zero: Segments drops zero-measure models, so a
            // zero total means no segments at all and this never runs, but a share is a
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
    /// The plan block: the meters in the order they are drawn, the off-plan row that closes
    /// them, and the banner above both blocks unless the reader hid it.
    ///
    /// <para>Built from the meters and the off-plan view the head already asks for, not from
    /// the snapshot again, so the block cannot describe a different reading than the rest of
    /// the panel. The row is the standing section re-shaped; it keeps that section's own
    /// condition for appearing.</para>
    /// </summary>
    /// <param name="cachedUsage">
    /// The cache <paramref name="offPlan"/> was built from, for the one thing the section
    /// does not say: whether its figure is an estimate over a window or Claude's own amount.
    /// </param>
    public static PanelPlanBlockView PlanBlock(
        PanelMeterView session,
        PanelMeterView weekly,
        IReadOnlyList<PanelMeterView> modelLimits,
        PanelOffPlanView offPlan,
        PanelStatistics stats,
        CachedUtilization? cachedUsage,
        PanelViewChoice view)
    {
        var row = offPlan.Section is { } section
            ? new PanelOffPlanRowView(
                OffPlanText.ReportedSpendHeading,
                section.Badge,
                cachedUsage is { ModelLimits.Limits.Count: > 0 }
                    ? OffPlanText.ReportedSpendRowCaption
                    : OffPlanText.OffPlanRowCaption(stats.WindowDays),
                section.SpendValue,
                section.Coverage,
                section.Hint)
            : null;

        return new PanelPlanBlockView(
            PanelText.PlanBlockTitle,
            [session, weekly, .. modelLimits],
            row,
            view.BannerHidden ? null : offPlan.Banner,
            PanelText.HideBannerLabel);
    }

    /// <summary>
    /// The local block's totals face for the chosen measure. This is where Output and Overall
    /// are decided, once:
    /// <list type="bullet">
    /// <item><b>Which total:</b> Output counts output tokens, Overall everything billed.</item>
    /// <item><b>Which estimate:</b> Output prices the output alone, Overall prices everything,
    /// which is what "Est. value" has always meant.</item>
    /// <item><b>Which series:</b> the chart's bars follow the total.</item>
    /// <item><b>Which wording:</b> value reads "spend" while the window is off-plan (F19).</item>
    /// </list>
    /// An unpriced figure reads unknown, never zero (rule 6), exactly as the tiles read it.
    /// </summary>
    public static PanelLocalTotalsView LocalTotals(
        PanelStatistics stats, UsageSnapshot snapshot, TimeZoneInfo zone, PanelViewChoice view)
    {
        var graph = Graph(stats, snapshot, zone, view.Measure);

        var weekMarks = graph.Boundaries
            .Select(b => new PanelLocalWeekMarkView(b, graph.Columns[b.Column].DateLabel))
            .ToList();

        return new PanelLocalTotalsView(
            PanelText.LocalBlockTitle,
            TokenText.LocalSubtitle(view.Measure, stats),
            new PanelLocalChartView(graph.Columns, weekMarks, TokenText.ChartTodayLabel),
            view.Measure == PanelMeasure.Output ? OutputTable(stats) : OverallTable(stats),
            TokenText.Caveat(stats));
    }

    private static PanelLocalTableView OutputTable(PanelStatistics stats) =>
        new(
            ["", TokenText.TokenBreakdownTodayHeader, TokenText.TokenBreakdown31DaysHeader],
            [
                new(PanelLocalRowKind.Figure, null, TokenText.OutputTokensRowLabel,
                    [UsageFormatter.Tokens(stats.TokensToday), UsageFormatter.Tokens(stats.Tokens31Days)]),
                new(PanelLocalRowKind.Value, null, TokenText.EstValueOutputLabel(stats.IsOffPlan),
                    [
                        UsageFormatter.Usd(stats.EstTodayOutputValue.Value),
                        UsageFormatter.Usd(stats.Est31DaysOutputValue.Value),
                    ]),
            ]);

    private static PanelLocalTableView OverallTable(PanelStatistics stats)
    {
        // The kind rows are the breakdown table's, so the two cannot count the same tokens
        // differently; only its heading row is replaced, by the headings this table owns.
        var rows = Breakdown(stats.CompositionToday, stats.Composition31Days)
            .Where(r => r.RowKind != PanelTokenRowKind.Header)
            .Select(r => new PanelLocalRowView(
                r.RowKind == PanelTokenRowKind.Total ? PanelLocalRowKind.Total : PanelLocalRowKind.Figure,
                r.Kind,
                r.Label,
                [r.TodayTokens, r.TodayShare, r.WindowTokens, r.WindowShare]))
            .ToList();

        rows.Add(new(PanelLocalRowKind.Value, null, TokenText.EstValueLabel(stats.IsOffPlan),
            [UsageFormatter.Usd(stats.EstTodayValue.Value), UsageFormatter.Usd(stats.Est31DaysValue.Value)],
            CellSpan: 2));

        return new PanelLocalTableView(
            [
                TokenText.TokenBreakdownKindHeader,
                TokenText.TokenBreakdownTodayHeader,
                TokenText.TokenBreakdownShareHeader,
                TokenText.TokenBreakdown31DaysHeader,
                TokenText.TokenBreakdownShareHeader,
            ],
            rows);
    }

    /// <summary>
    /// The local block's footer: the standing scope caveat, and the switch with the current
    /// measure selected. Each segment carries the view pressing it gives.
    /// </summary>
    public static PanelLocalFooterView LocalFooter(TranscriptScopeReport? scope, PanelViewChoice view) =>
        new(
            TokenText.LocalFooter(scope),
            [.. new[] { PanelMeasure.Output, PanelMeasure.Overall }.Select(measure =>
                new PanelMeasureOption(
                    TokenText.MeasureLabel(measure), measure == view.Measure, view with { Measure = measure }))]);

    /// <summary>
    /// The 31-day graph: every column measured and dated, every gridline placed, every hover
    /// card worded (<see cref="PanelGraphView"/>).
    ///
    /// <para>An empty series gives a view with no columns, which is a chart neither head
    /// draws. It is not an error state, a store that has recorded nothing is a new install,
    /// and the no-data banner above the chart is what says so.</para>
    /// </summary>
    /// <param name="zone">
    /// The reader's timezone, or a pinned one for verification renders. Everything the chart
    /// places is taken in it: the columns are local days and so a gridline drawn over them
    /// has to be positioned in the same frame (<see cref="LocalDays"/>, issue #211).
    /// </param>
    /// <param name="measure">
    /// What a bar counts: output tokens, or everything billed on the day. The week bands and
    /// gridlines are the same either way, so switching the measure moves the bars and nothing
    /// else on the chart.
    /// </param>
    public static PanelGraphView Graph(
        PanelStatistics stats, UsageSnapshot snapshot, TimeZoneInfo zone,
        PanelMeasure measure = PanelMeasure.Output)
    {
        long Volume(DayUsage d) => measure == PanelMeasure.Output ? d.OutputTokens : d.BilledTokens;

        var heading = PanelText.UsageWindowHeading(stats.WindowDays);
        var series = stats.DailySeries;
        if (series.Count == 0)
        {
            return new PanelGraphView(heading, [], []);
        }

        var weeks = PlanWeeks.ForSeries(series, snapshot, zone);

        // Floored at 1 so a window with nothing in it divides rather than throwing. Every
        // column is then zero-height, which is what a store with no usage should draw.
        var peak = Math.Max(1, series.Max(Volume));
        var weekPeak = series
            .Where(d => !d.PreInstall)
            .GroupBy(weeks.IndexOf)
            .ToDictionary(g => g.Key, g => Math.Max(1, g.Max(Volume)));

        var columns = new List<PanelGraphColumnView>(series.Count);
        foreach (var day in series)
        {
            var bar = !day.PreInstall && Volume(day) > 0;
            columns.Add(new PanelGraphColumnView(
                string.Create(CultureInfo.InvariantCulture, $"{day.Date:d MMM}"),
                !day.PreInstall,
                bar ? Volume(day) / (double)peak : 0,
                bar ? Volume(day) / (double)weekPeak[weeks.IndexOf(day)] : 0,
                bar ? TokenText.DayBarTokens(Volume(day)) : "",
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
    /// <para>Both answers are taken in the column's own frame, a <b>local</b> day, of
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
    /// than a claim about the plan. That premise no longer holds, ADR-0011 derives the weekly
    /// reset, so the bands follow the plan's own boundary wherever it is known, which is what
    /// the graph was always trying to convey. Mondays remain the fallback, and say so on
    /// hover, because until a reset has been observed the real boundary is still genuinely
    /// unknown (rule 6).</para>
    ///
    /// <para>Boundaries are derived by stepping the cadence back from the predicted next
    /// reset, so they cover the whole window including days before O-view was installed, the
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
            // as UTC, which would clip or over-collect boundaries by the offset.
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
        /// <para>A day that contains a boundary genuinely belongs to neither band, its tokens
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
    /// The session and weekly meters' start: a percentage is shown only when the snapshot is
    /// authoritative, an estimate has no meter to fill (ADR-0002).
    /// </summary>
    private static PanelMeterView Meter(
        string label, UsagePercent reported, UsageSnapshot snapshot, PanelBanner? banner) =>
        // Integer precision only, which is what the meter has always carried, the typed value
        // widens to a double so one percent type can serve figures that are not whole numbers,
        // and these two are.
        Meter(
            label,
            IsAuthoritative(snapshot) ? (int?)reported.Value : null,
            banner?.GaugePlaceholder ?? PanelBanner.UnknownGauge);

    /// <summary>
    /// What every meter shares: the figure, the band and the fill, or the placeholder that
    /// stands in for all three.
    /// </summary>
    private static PanelMeterView Meter(string label, int? percent, string placeholder) =>
        new(
            label,
            percent,
            percent is { } p ? PanelText.BarPercentUsed(p) : placeholder,
            percent is { } band ? UsageLevels.Classify(band) : null,
            percent is { } fill ? Math.Clamp(fill, 0, 100) : null,
            ResetLine: null,
            ResetLineWithAction: null,
            ResetHint: null,
            Chip: null);

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
