using OView.Core.Models;

namespace OView.Presentation.Panel;

/// <summary>
/// The "Tokens used" section, built once for both heads: a bar per window with its legend, the
/// Bars/Breakdown switch's labels, the table behind the switch, and the coverage line beneath
/// it all. A head lays these out and computes nothing: which kinds draw, what each legend entry
/// reads, which window's kinds drive the table's rows and what the total row says are decided
/// here, so the two panels cannot describe the same tokens differently (issue #253 for the
/// section, issue #169 for the table's two share columns).
/// </summary>
/// <param name="Show">
/// Whether the section appears at all. Today can be empty on a machine that has history but
/// has not worked yet, so it shows while either window has tokens; both empty means there is
/// nothing to break down, and the scope note above already says considerably more.
/// </param>
/// <param name="Heading">The section heading.</param>
/// <param name="BarsLabel">The switch's first label, for the bars view.</param>
/// <param name="BreakdownLabel">The switch's second label, for the table view.</param>
/// <param name="Today">Today's bar.</param>
/// <param name="Window31">The 31-day bar.</param>
/// <param name="Breakdown">The table's rows, header and total included, in display order.</param>
/// <param name="CoverageLine">
/// Which surfaces the figures are made of, or empty when nothing was found at all; the scope
/// note owns that state and says considerably more (issue #171).
/// </param>
public sealed record PanelTokenBarsView(
    bool Show,
    string Heading,
    string BarsLabel,
    string BreakdownLabel,
    PanelTokenBarView Today,
    PanelTokenBarView Window31,
    IReadOnlyList<PanelTokenBreakdownRow> Breakdown,
    string CoverageLine);

/// <summary>One composition bar: its heading and total, the kinds that draw, and the legend.</summary>
/// <param name="Heading">The bar's heading.</param>
/// <param name="Window">The window's name, for the card behind each segment.</param>
/// <param name="Total">The billed total, formatted.</param>
/// <param name="Composition">What the bar draws, for the shared <c>TokenBarGeometry</c> to lay the track out from.</param>
/// <param name="Segments">
/// The kinds that draw: those with any tokens, in display order. The entry at the same index
/// in <paramref name="Legend"/> names it.
/// </param>
/// <param name="Legend">
/// Every exact figure the track's width floors cannot draw honestly, one entry per segment, so
/// it is not optional decoration.
/// </param>
public sealed record PanelTokenBarView(
    string Heading,
    string Window,
    string Total,
    TokenComposition Composition,
    IReadOnlyList<TokenKindSlice> Segments,
    IReadOnlyList<PanelTokenLegendEntry> Legend);

/// <summary>One legend entry: the kind, and its label with its figure, as in "output 128.6K".</summary>
public sealed record PanelTokenLegendEntry(TokenKind Kind, string Text);

/// <summary>Which of the table's three row shapes a row is; each head styles them differently.</summary>
public enum PanelTokenRowKind
{
    Header,
    Kind,
    Total,
}

/// <summary>
/// One row of the breakdown table. <paramref name="Kind"/> carries the colour chip for a kind
/// row and is null on the header and total rows.
/// </summary>
public sealed record PanelTokenBreakdownRow(
    PanelTokenRowKind RowKind,
    TokenKind? Kind,
    string Label,
    string TodayTokens,
    string TodayShare,
    string WindowTokens,
    string WindowShare);
