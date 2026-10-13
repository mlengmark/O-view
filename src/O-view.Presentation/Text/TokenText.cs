using System.Globalization;
using OView.Core.Models;
using OView.Core.Pricing;

namespace OView.Presentation.Text;

/// <summary>
/// The token tiles, bars and breakdown table: labels, captions and the 31-day caveat (see
/// <see cref="PanelText"/> for why wording like this is shared rather than written per
/// platform).
/// </summary>
public static class TokenText
{
    // Token and money formatting live in UsageFormatter, right beside this class. The handful
    // of sentences below that embed a figure still duplicate its two one-line rules rather than
    // call it directly.
    private static string FormatTokens(long tokens) => tokens switch
    {
        >= 1_000_000 => string.Create(CultureInfo.InvariantCulture, $"{tokens / 1_000_000.0:0.0}M"),
        >= 1_000 => string.Create(CultureInfo.InvariantCulture, $"{tokens / 1_000.0:0.0}K"),
        _ => tokens.ToString(CultureInfo.InvariantCulture),
    };

    private static string FormatUsd(decimal usd) =>
        "$" + usd.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>
    /// The coverage caveat: how much of the 31-day window is actually recorded, or empty
    /// when the window is fully covered.
    ///
    /// <para>ADR-0006 makes this text a requirement rather than a nicety: a small 31-day
    /// figure without it reads as low usage rather than as short history. That makes it
    /// exactly the kind of string that should have one definition — it was built by hand
    /// in two separate places in the panel, which is two places to forget it.</para>
    ///
    /// <para>Built from <see cref="PanelStatistics.RecordedDays"/> and
    /// <see cref="PanelStatistics.WindowDays"/> rather than read off the model, because the
    /// sentence is panel copy and Core's job stops at reporting the two numbers (GitHub issue
    /// #142 — <see cref="PanelStatistics.HasPartialHistory"/> carries the reasoning on why
    /// those numbers, not this one, are the ones that must stay exact).</para>
    /// </summary>
    public static string CoverageNote(PanelStatistics stats) =>
        stats.HasPartialHistory ? $"{stats.RecordedDays} of {stats.WindowDays} days recorded" : "";

    /// <summary>
    /// The caveat under the 31-day tiles: how much of the window is actually recorded, which
    /// models were left out of the money figures, what O-view assumed about cache writes it
    /// could not attribute, and how old the rates behind the figures are.
    ///
    /// <para>All four are the same class of statement — the total is real but qualified — and
    /// omitting any of them lets a figure read as the whole picture. An unpriced model must be
    /// named rather than silently dropped, and must not void the total (a single newly
    /// released Claude once blanked both Est. tiles entirely).</para>
    ///
    /// <para><b>The last two are conditions that clear.</b> The TTL note applies only to rows a
    /// build before GitHub issue #255 ingested and that are out of reach of the re-ingest, so it
    /// disappears as that history ages out of the window. The rate age appears only past
    /// <see cref="RateCard.StaleAfter"/> — a caveat that is always on says nothing, and the
    /// figures are not less true for being priced at a rate that has not changed.</para>
    /// </summary>
    public static string Caveat(PanelStatistics stats)
    {
        var parts = new List<string>(4);

        if (CoverageNote(stats) is { Length: > 0 } coverage)
        {
            parts.Add(coverage);
        }

        if (stats.UnpricedModels.Count > 0)
        {
            parts.Add($"excludes {string.Join(", ", stats.UnpricedModels)} (no published rate)");
        }

        if (stats.TtlUnrecordedCacheWrites > 0)
        {
            parts.Add($"{FormatTokens(stats.TtlUnrecordedCacheWrites)} cache writes "
                      + "with no recorded duration, priced at the 5-minute rate");
        }

        if (stats.RatesAreStale)
        {
            parts.Add(RateAge(stats.Rates));
        }

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// How old the rates are and where they came from: <c>rates: bundled, as of 24 Jun 2026</c>.
    ///
    /// <para><b>The source is named as well as the date</b>, because the two answer different
    /// halves of "can I check this figure". A date says how likely the table is to have moved;
    /// a source says whose table it is. Only <see cref="RateCardSource.Bundled"/> exists today,
    /// and the line is written to carry the other from the day it does — a user-editable
    /// pricing file with no provenance on screen is a fabricated-number vector (issue #255).</para>
    /// </summary>
    public static string RateAge(RateCard card) => string.Create(
        CultureInfo.InvariantCulture,
        $"rates: {card.SourceLabel}, as of {card.AsOf:d MMM yyyy}");

    /// <summary>
    /// What the token and cost tiles permanently do not cover (issue #235).
    ///
    /// <para><b>Deliberately not part of <see cref="Caveat"/>.</b> That one is a per-tile hint
    /// carried by the two 31-day tiles, and it is right for statements about the 31-day window.
    /// This applies to every token and cost figure on the panel, so riding the same channel would
    /// print it twice on adjacent tiles and never on the "today" pair. It renders once, beneath
    /// the whole tile block.</para>
    ///
    /// <para><b>The plan bars are account-wide; these tiles are not.</b> Cloud-container Cowork
    /// sessions and Claude chat leave no transcript on this machine — measured directly on
    /// 2026-08-28, a cloud Cowork session wrote no registration and no transcript at all — so the
    /// bars include that work and the tiles cannot. The machine that reported
    /// [#218](https://github.com/mlengmark/O-view/issues/218) sat at 100% on the plan meter with a
    /// two-day-old local record, which is that pairing in its most alarming form.</para>
    ///
    /// <para><b>Always present, unlike the caveats it joins.</b> Partial history and an unpriced
    /// model are conditions that pass; this one is the standing shape of the data. A caveat that
    /// appears only sometimes teaches a reader that its absence means full coverage, and here that
    /// would be false every time.</para>
    ///
    /// <para><b>This is not the line issue #232 removed.</b> That one sat under the session
    /// <i>bar</i>, described the session window alone, and carried a disclosure; it was removed as
    /// out of place with the explicit instruction that nothing replace it there. This states the
    /// scope of the <i>tiles</i>, in the caveat line they already carry, which is where the panel
    /// has always admitted a figure is real but incomplete.</para>
    /// </summary>
    public const string TokenScopeCaveat = "chat and cloud sessions not counted";

    /// <summary>
    /// Tile headings for estimated money. <b>"Est." is never dropped</b> — within plan
    /// limits the marginal cost is £0 and these price tokens at public API rates, so the
    /// figure is a valuation, not a charge.
    ///
    /// <para>The framing flips when usage goes off-plan, because then it genuinely is
    /// spend.</para>
    ///
    /// <para>It read "(UTC)" for one release, because the figure was bucketed by UTC date and
    /// had to say so (issue #210). The suffix is off again now the bucket is the reader's own
    /// day — a qualifier that is no longer true is not a safe thing to leave on.</para>
    /// </summary>
    public static string EstTodayLabel(bool offPlan) => offPlan ? "Est. spend today" : "Est. value today";

    /// <summary>
    /// The 31-day heading, which deliberately does <b>not</b> flip with off-plan state.
    /// Divergence is detected for the <i>current session window</i> only, so relabelling a
    /// 31-day total as "spend" would extend a claim over 30 days it was never measured
    /// against.
    /// </summary>
    public const string Est31DaysLabel = "Est. value · 31 days";

    /// <summary>
    /// The token tile headings, which name what they count.
    ///
    /// <para>They read "Tokens today" alone until a user reported the figure as inflated:
    /// <c>235.6M</c> beside the thousands Claude's own UI shows (issue #169). The sum was
    /// right — roughly 90% of it is cached prompt re-reads, which are billed and counted —
    /// but an unqualified "Tokens today" invites the comparison that makes it look like a
    /// 1000× over-count. #169 answered that by qualifying the heading: <c>incl. cache</c>.</para>
    ///
    /// <para><b>Issue #253 changed the figure instead.</b> The qualifier was correct and
    /// still left the panel leading with a number nobody reads — at a stable ~89% cache-read
    /// share, output never exceeds about 1.5% of the total, so the one quantity that tracks
    /// work done was buried in a figure dominated by conversation re-sends. The tiles now
    /// headline output alone and say so. The composition moved beneath them
    /// (<see cref="TokensUsedTodayLabel"/>), where explaining the rest is its whole job.</para>
    ///
    /// <para><b>"Today" means the reader's today.</b> It said "(UTC)" for one release because
    /// the bucket genuinely was a UTC day and a label that does not match its figure is the
    /// same rule-6 failure as a wrong figure (issue #210). The bucket is now the local day
    /// (issue #211), so the qualifier is gone: leaving it on would be the mislabelling
    /// pointed the other way.</para>
    /// </summary>
    public const string TokensTodayLabel = "Output tokens today";

    /// <inheritdoc cref="TokensTodayLabel"/>
    public const string Tokens31DaysLabel = "Output tokens · 31 days";

    /// <summary>
    /// Headings for the two composition bars.
    ///
    /// <para><b>"Tokens used" rather than "Tokens", and deliberately not the tiles' wording.</b>
    /// A bar totalling 1.4M sitting under a tile reading <c>Output tokens today 14.6K</c> is a
    /// contradiction unless something says the two count different things. These name the
    /// billed whole; the tiles name the work.</para>
    /// </summary>
    /// <summary>
    /// The section heading above both bars and the view switch. Names the section once so the
    /// two bar headings can name only their windows.
    /// </summary>
    public const string TokensUsedHeading = "Tokens used";

    /// <inheritdoc cref="TokensUsedHeading"/>
    public const string TokensUsedTodayLabel = "Tokens used · today";

    /// <inheritdoc cref="TokensUsedTodayLabel"/>
    public const string TokensUsed31DaysLabel = "Tokens used · last 31 days";

    /// <summary>
    /// One token kind's name.
    ///
    /// <para>"Cache write" rather than "cache creation" because the pair reads as a pair — a
    /// reader scanning the legend needs to see at once that two of the four entries are cache
    /// traffic, which is the whole point of showing the split.</para>
    /// </summary>
    public static string TokenKindLabel(TokenKind kind) => kind switch
    {
        TokenKind.Output => "output",
        TokenKind.Input => "input",
        TokenKind.CacheWrite => "cache write",
        TokenKind.CacheRead => "cache read",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a token kind."),
    };

    /// <summary>
    /// A kind's share of its own window, to two decimals.
    ///
    /// <para>Built rather than formatted with <c>"P2"</c>, which renders "1.08 %" — with a
    /// space — under the INVARIANT culture as well as most European ones. The panel states
    /// its own figures rather than inheriting the desktop's, and invariant alone does not get
    /// there here.</para>
    ///
    /// <para><b>Floored at <c>&lt;0.01%</c> rather than rounded to <c>0.00%</c>.</b> Input
    /// runs at 0.003% of a day; printing that as zero says it did not happen, and printing
    /// enough decimals to show it would set the column width for every other row.</para>
    /// </summary>
    public static string TokenShare(double share) => share switch
    {
        >= 1 => "100%",
        <= 0 => "0%",
        < 0.0001 => "<0.01%",
        _ => (share * 100).ToString("0.00", CultureInfo.InvariantCulture) + "%",
    };

    /// <summary>Scope words for a segment's hover card, naming which window it belongs to.</summary>
    public const string TokenWindowToday = "today";

    /// <inheritdoc cref="TokenWindowToday"/>
    public const string TokenWindow31Days = "the last 31 days";

    /// <summary>
    /// The identifying line under a segment card's token figure:
    /// <c>cache write · 10.30% of today · Est. $0.87</c>.
    ///
    /// <para>Built as one caption rather than as a third hover-card shape. The panel has two —
    /// a figure with a caption, and a sentence — and <c>HoverCard</c> says to reuse them; a
    /// per-kind card is a figure with a caption, so it is one.</para>
    ///
    /// <para><b>"Est." is never dropped here either.</b> A hover card is not an exemption from
    /// the rule the tiles follow, and this is the figure a reader is most likely to quote back
    /// as money spent. An unpriced window says so outright rather than showing <c>$0.00</c>,
    /// which would read as "this cost nothing" (rule 6).</para>
    ///
    /// <para>The window is named because <b>the two bars have measurably different shapes</b> —
    /// cache write ran 10.3% of a day against 0.93% of the month on the machine this was
    /// designed against — so a share with no window attached can be read against the wrong bar.</para>
    /// </summary>
    public static string TokenCardCaption(TokenKind kind, double share, decimal? estUsd, string window)
    {
        var value = estUsd is null ? "value unknown" : $"Est. {FormatUsd(estUsd.Value)}";
        return $"{TokenKindLabel(kind)} · {TokenShare(share)} of {window} · {value}";
    }

    /// <summary>Column headings for the breakdown view.</summary>
    public const string TokenBreakdownKindHeader = "Kind";

    /// <inheritdoc cref="TokenBreakdownKindHeader"/>
    public const string TokenBreakdownTodayHeader = "Today";

    /// <inheritdoc cref="TokenBreakdownKindHeader"/>
    public const string TokenBreakdown31DaysHeader = "31 days";

    /// <summary>
    /// Heading for a share column. <b>There is one per window, not one shared.</b> The two
    /// windows have measurably different shapes — cache write ran 10.3% of a day against
    /// 0.93% of the month on the machine this was designed against — so a single share column
    /// beside two token columns would be read against whichever one it sat nearer.
    /// </summary>
    public const string TokenBreakdownShareHeader = "Share";

    /// <inheritdoc cref="TokenBreakdownKindHeader"/>
    public const string TokenBreakdownTotalLabel = "Total";

    /// <summary>Labels for the two views of the composition.</summary>
    public const string TokenViewBarsLabel = "Bars";

    /// <inheritdoc cref="TokenViewBarsLabel"/>
    public const string TokenViewBreakdownLabel = "Breakdown";
}
