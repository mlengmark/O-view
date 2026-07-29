using System.Globalization;

namespace OView.Core.Models;

/// <summary>
/// Display formatting for token counts and estimated values.
///
/// <para>Sibling of <see cref="TooltipFormatter"/>, and here for the same reason:
/// display-edge formatting belongs in Core, where it is testable without a desktop
/// session. These two methods previously existed as four copies across a
/// <c>Window</c> code-behind and an <c>Application</c> subclass — <c>FormatTokens</c>
/// byte-identical in two places, and money written two different ways (GitHub issue
/// #55). The off-plan balloon composed <c>"$" + "0.00"</c> in one place and asked ICU
/// for the <c>"C"</c> pattern under a pinned <c>en-US</c> in another; they agree today,
/// but they are not the same instruction, and the balloon points the user at the very
/// tile it must match.</para>
///
/// <para>Culture-invariant throughout: these are figures, and the app pins its own
/// presentation rather than inheriting the machine's.</para>
/// </summary>
public static class UsageFormatter
{
    /// <summary>
    /// Token counts, abbreviated at thousands and millions to one decimal — the tiles are
    /// ~180px wide and a raw nine-digit figure does not fit beside its label.
    /// </summary>
    public static string Tokens(long tokens) => tokens switch
    {
        >= 1_000_000 => string.Create(CultureInfo.InvariantCulture, $"{tokens / 1_000_000.0:0.0}M"),
        >= 1_000 => string.Create(CultureInfo.InvariantCulture, $"{tokens / 1_000.0:0.0}K"),
        _ => tokens.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// An estimated value. Null renders "unknown", never "$0.00": an unpriced model has an
    /// unknown value, not a zero one, and a zero would read as "this cost nothing"
    /// (CLAUDE.md rule 6).
    /// </summary>
    public static string Usd(decimal? usd) => usd is { } value
        ? "$" + value.ToString("0.00", CultureInfo.InvariantCulture)
        : "unknown";
}
