using OView.Core.Pricing;
using OView.Core.Storage;

namespace OView.Core.Models;

// TokenComposition keeps a single "cache write" figure: the panel's bar draws one segment for
// it, and the 5m/1h split (issue #255) is a pricing distinction rather than a display one. The
// value beside that segment is priced per bucket, so the split is honoured where it matters.

/// <summary>
/// The four token kinds, in the order the panel draws them.
///
/// <para><b>The order is the design, not an enum's declaration accident.</b> Output leads
/// because it is the figure the tiles now headline (GitHub issue #253) and the one a reader
/// watches move during a session; cache read trails because it dominates every total and
/// would otherwise push everything else off the origin. See
/// <see cref="TokenComposition.InDisplayOrder"/>.</para>
/// </summary>
public enum TokenKind
{
    Output,
    Input,
    CacheWrite,
    CacheRead,
}

/// <summary>
/// One token kind's contribution to a window: how many, what share of the window, and what
/// it would have cost at published API rates.
/// </summary>
/// <param name="EstUsd">
/// Null when nothing in the window could be priced — the same "honest unknown" rule
/// <see cref="PanelStatistics"/> applies to the Est. tiles, so a card and the tile above it
/// cannot disagree about whether a figure is known.
/// </param>
public sealed record TokenKindSlice(TokenKind Kind, long Tokens, double Share, decimal? EstUsd);

/// <summary>
/// The four token kinds behind a window's figures, kept separate so the panel can say what
/// was billed and what each kind was worth.
///
/// <para><b>Why this exists.</b> A user reported the token tiles as inflated: the panel
/// read <c>235.6M</c> where Claude's own UI showed thousands, and they concluded O-view was
/// over-counting (GitHub issue #169). It was not — the ingestion path de-duplicates on
/// request id and upserts by replacement, and the sum was arithmetically right. The two
/// figures simply answer different questions.</para>
///
/// <para>Measured on real transcripts: <b>cache reads were 89.2% of a day's total</b> —
/// 398,121 of 446,148 tokens across 7 requests. Every turn re-sends the whole conversation
/// and prompt caching bills that re-send as <c>cache_read_input_tokens</c>, so the
/// cumulative figure outruns any single conversation's size by orders of magnitude.</para>
///
/// <para><b>Issue #169 fixed the label; issue #253 changed the metric.</b> The tiles used to
/// headline this <see cref="Total"/> and qualify it <c>incl. cache</c>. They now headline
/// <see cref="Output"/> alone, and this type is what sits beneath them explaining what else
/// was billed. Both halves of #169's reasoning survive the change and neither should be
/// undone: the sum here is still the whole of what was billed, and
/// <see cref="Pricing.CostEstimator"/> still prices every kind — dropping cache reads from
/// the <i>cost</i> path would understate real consumption by roughly the share measured
/// above.</para>
/// </summary>
public sealed record TokenComposition(long Input, long CacheCreation, long CacheRead, long Output)
{
    public static readonly TokenComposition Empty = new(0, 0, 0, 0);

    /// <summary>Estimated value of the input tokens alone. See <see cref="TokenKindSlice.EstUsd"/>.</summary>
    public decimal? InputUsd { get; init; }

    /// <inheritdoc cref="InputUsd"/>
    public decimal? CacheCreationUsd { get; init; }

    /// <inheritdoc cref="InputUsd"/>
    public decimal? CacheReadUsd { get; init; }

    /// <inheritdoc cref="InputUsd"/>
    public decimal? OutputUsd { get; init; }

    /// <summary>
    /// Everything billed in this window. No longer the tiles' headline (issue #253), and
    /// still identical by construction to the sum of <see cref="DailyRollup.TotalTokens"/>
    /// over the same rollups — the point of this type is to explain a window, never to
    /// compute a different figure for it.
    /// </summary>
    public long Total => Input + CacheCreation + CacheRead + Output;

    /// <summary>The total without cached prompt re-reads.</summary>
    public long ExcludingCacheReads => Input + CacheCreation + Output;

    /// <summary>Cache reads as a fraction of the total; zero when there is nothing to divide.</summary>
    public double CacheReadShare => ShareOf(CacheRead);

    /// <summary>Whether there is anything to break down. A composition of nothing explains nothing.</summary>
    public bool HasTokens => Total > 0;

    /// <summary>
    /// One kind's fraction of <see cref="Total"/>. Zero on an empty window rather than NaN:
    /// every caller renders this, and a NaN reaches the screen as text.
    /// </summary>
    public double ShareOf(long tokens) => Total == 0 ? 0 : (double)tokens / Total;

    /// <summary>
    /// The four kinds ready to draw — one shape serving the bar, its legend, the breakdown
    /// table and the hover cards, on both heads.
    ///
    /// <para>Built here rather than in each panel because the <i>order</i> carries meaning
    /// (see <see cref="TokenKind"/>) and two heads ordering it differently is the same class
    /// of defect as two heads computing it differently — which is what
    /// <see cref="PanelText"/> exists to prevent for words.</para>
    /// </summary>
    public IReadOnlyList<TokenKindSlice> InDisplayOrder =>
    [
        new(TokenKind.Output, Output, ShareOf(Output), OutputUsd),
        new(TokenKind.Input, Input, ShareOf(Input), InputUsd),
        new(TokenKind.CacheWrite, CacheCreation, ShareOf(CacheCreation), CacheCreationUsd),
        new(TokenKind.CacheRead, CacheRead, ShareOf(CacheRead), CacheReadUsd),
    ];

    /// <summary>
    /// Sums and prices the rollups, one token kind at a time.
    ///
    /// <para><b>Every figure goes through <see cref="CostEstimator"/>, never a second pricing
    /// path.</b> The four card values have to add up to the Est. tile above them, and the only
    /// way to guarantee that is for both to be the same arithmetic over the same rollups. A
    /// per-kind price computed here from its own multipliers would be a second implementation
    /// of the thing #169 was reported about.</para>
    ///
    /// <para>Est. values are null whenever <b>nothing</b> could be priced — matching the rule
    /// <c>PanelStatistics.EstimateTotal</c> applies to the tiles, so a card never reads
    /// "unknown" beside a tile showing a figure, or the reverse. A window that could be
    /// priced but happens to hold no tokens is a known zero, not an unknown.</para>
    /// </summary>
    public static TokenComposition From(IEnumerable<DailyRollup> rollups)
    {
        var total = TokenSplit.Empty;
        decimal inputUsd = 0, cacheCreationUsd = 0, cacheReadUsd = 0, outputUsd = 0;
        var pricedAny = false;

        foreach (var r in rollups)
        {
            total += r.Tokens;

            // An unpriced model contributes tokens but no value, and is named in
            // PanelStatistics.UnpricedModels — the tokens are still real.
            if (CostEstimator.EstimateUsd(
                    r.Model, TokenSplit.Empty with { Input = r.Tokens.Input }, r.Modifiers)
                is not { } inputPart)
            {
                continue;
            }

            // The remaining three are priceable by construction: EstimateUsd returns null only
            // when the rate card cannot resolve the model or its modifiers, and neither varies
            // by token kind. The three cache-write buckets are priced together — the bar draws
            // one "cache write" segment, and its value has to be the whole of what that segment
            // counts, at each bucket's own published rate.
            inputUsd += inputPart;
            cacheCreationUsd += CostEstimator.EstimateUsd(r.Model, CacheWritesOf(r.Tokens), r.Modifiers) ?? 0;
            cacheReadUsd += CostEstimator.EstimateUsd(
                r.Model, TokenSplit.Empty with { CacheRead = r.Tokens.CacheRead }, r.Modifiers) ?? 0;
            outputUsd += CostEstimator.EstimateUsd(
                r.Model, TokenSplit.Empty with { Output = r.Tokens.Output }, r.Modifiers) ?? 0;
            pricedAny = true;
        }

        // Unknown, not zero, whenever nothing could be priced — including the no-rollups
        // case, so From([]) is Empty exactly. Zero would say "this was worth nothing", which
        // is a different claim from "there is nothing here to value" (rule 6).
        var unknown = !pricedAny;

        // CacheWrite, not the three buckets: the split is a pricing distinction, and the values
        // above already honour it. One bar segment, one figure.
        return new TokenComposition(total.Input, total.CacheWrite, total.CacheRead, total.Output)
        {
            InputUsd = unknown ? null : inputUsd,
            CacheCreationUsd = unknown ? null : cacheCreationUsd,
            CacheReadUsd = unknown ? null : cacheReadUsd,
            OutputUsd = unknown ? null : outputUsd,
        };
    }

    /// <summary>
    /// The cache-write buckets alone, each keeping its own TTL so the segment's value is priced
    /// at the rates that actually apply rather than at one of them.
    /// </summary>
    private static TokenSplit CacheWritesOf(TokenSplit tokens) => TokenSplit.Empty with
    {
        CacheWrite5m = tokens.CacheWrite5m,
        CacheWrite1h = tokens.CacheWrite1h,
        CacheWriteTtlUnrecorded = tokens.CacheWriteTtlUnrecorded,
    };
}
