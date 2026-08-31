using System.Globalization;
using System.Text.RegularExpressions;

namespace OView.Core.Providers.CachedUsage;

/// <summary>
/// The two figures that can be lifted out of a promo notice's sentence — when it ends, and by
/// how much limits went up.
///
/// <para><b>Why parse prose at all.</b> The notice Claude Code caches is marketing copy, not a
/// structured entitlement: <c>"+50% weekly limits promo through Aug 31 · clau.de/cc-50-promo"</c>.
/// The message itself is always shown verbatim, so nothing here is load-bearing for correctness —
/// but the end date is a real date, and the machine's own clock can therefore decide whether the
/// promo has passed. That is a far better expiry test than asking how recently Claude Code ran,
/// which is a question about O-view's reader rather than about the promo.</para>
///
/// <para><b>Both extractors are deliberately conservative: a confident hit, or nothing.</b>
/// Falling back costs a detail on a chip. Mis-parsing produces a confidently wrong statement on
/// a panel whose whole job is not to make those (rule 6), so every shape that could mean two
/// things is refused rather than guessed.</para>
///
/// <para>Pure functions over <c>(text, anchor)</c>, with no I/O, so the fixture tables in
/// <c>PromoTextTests</c> are the specification — including every row that must <i>not</i>
/// resolve.</para>
/// </summary>
public static class PromoText
{
    /// <summary>
    /// Month-name and ISO date shapes only. <b>Numeric <c>M/d</c> is deliberately absent:</b>
    /// <c>9/4</c> is 4 September to the writer and 9 April to a British reader, and nothing in
    /// the payload says which. Unrecognised means the message shows without a date, which is
    /// merely less useful; recognising it wrongly would put the wrong month on screen.
    /// </summary>
    private static readonly string[] NoYearFormats =
        ["MMM d", "MMM dd", "MMMM d", "MMMM dd", "d MMM", "dd MMM", "d MMMM", "dd MMMM"];

    /// <inheritdoc cref="NoYearFormats"/>
    private static readonly string[] WithYearFormats =
        ["MMM d yyyy", "MMMM d yyyy", "d MMM yyyy", "yyyy-MM-dd", "MMM d, yyyy", "MMMM d, yyyy"];

    /// <summary>
    /// A word that marks the date as the promo's <i>end</i>. Without one, a date in the sentence
    /// could be anything — a start, a billing date, a support article's publication.
    /// </summary>
    private const string EndLeadIn =
        @"(?:through|thru|until|till|til|ends?(?:\s+on)?|expires?(?:\s+on)?|valid\s+(?:through|until))";

    private const string DateShape =
        @"(?<d>[A-Za-z]{3,9}\.?\s+\d{1,2}(?:,?\s+\d{4})?|\d{1,2}\s+[A-Za-z]{3,9}(?:\s+\d{4})?|\d{4}-\d{2}-\d{2})";

    private static readonly Regex EndDatePattern =
        new($@"\b{EndLeadIn}\s+{DateShape}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>At most three digits, optionally signed. <c>50%</c>, <c>+50%</c>, <c>100%</c>.</summary>
    private static readonly Regex PercentPattern =
        new(@"(?<sign>\+)?(?<v>\d{1,3})\s*%", RegexOptions.CultureInvariant);

    /// <summary>
    /// Words that make a percentage an increase rather than a level. Checked in a window around
    /// the match, because <c>"50% higher"</c> and <c>"Extra 25%"</c> put the word on opposite
    /// sides of the figure.
    /// </summary>
    private static readonly Regex IncreaseWord =
        new(@"higher|more|extra|additional|boost(?:ed)?|increase[d]?|bigger|\bup\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>How far either side of the figure an increase word still counts.</summary>
    private const int IncreaseWindow = 20;

    /// <summary>
    /// The promo's last day, or null when the sentence does not carry one that can be read
    /// without guessing.
    ///
    /// <para><b>A missing year is inferred from the anchor</b> — the moment the notice was
    /// cached — by taking the reading nearest it within
    /// <c>[anchor − 30d, anchor + 180d]</c>. That window is what makes <c>"through Jan 2"</c>
    /// read on an August anchor resolve to the following January rather than the one already
    /// past. Outside it, nothing resolves: a date a year away is not a promo.</para>
    /// </summary>
    /// <param name="text">The notice's message, exactly as the source wrote it.</param>
    /// <param name="anchorUtc">
    /// When the flag cache was fetched. The year is inferred relative to this rather than to
    /// <c>now</c>, so a cache read months later still resolves the year its author meant.
    /// </param>
    public static DateOnly? EndDate(string? text, DateTimeOffset anchorUtc)
    {
        if (text is not { Length: > 0 })
        {
            return null;
        }

        var match = EndDatePattern.Match(text);
        if (!match.Success)
        {
            return null;
        }

        var raw = match.Groups["d"].Value.Replace(".", "", StringComparison.Ordinal).Trim();

        foreach (var format in WithYearFormats)
        {
            if (DateTime.TryParseExact(raw, format, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var exact))
            {
                return DateOnly.FromDateTime(exact);
            }
        }

        foreach (var format in NoYearFormats)
        {
            if (!DateTime.TryParseExact(raw, format, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var partial))
            {
                continue;
            }

            return NearestYear(partial.Month, partial.Day, anchorUtc);
        }

        return null;
    }

    /// <summary>
    /// The candidate year whose date sits nearest the anchor inside the accepted window, or null
    /// when none does. Tries the year before as well as after, so a notice read just after New
    /// Year still resolves a December end date backwards rather than a year forward.
    /// </summary>
    private static DateOnly? NearestYear(int month, int day, DateTimeOffset anchorUtc)
    {
        var anchor = DateOnly.FromDateTime(anchorUtc.UtcDateTime);
        DateOnly? best = null;
        var bestDistance = int.MaxValue;

        for (var year = anchor.Year - 1; year <= anchor.Year + 1; year++)
        {
            // 29 February is not a date in every candidate year.
            if (day > DateTime.DaysInMonth(year, month))
            {
                continue;
            }

            var candidate = new DateOnly(year, month, day);
            var delta = candidate.DayNumber - anchor.DayNumber;
            if (delta is < -30 or > 180)
            {
                continue;
            }

            if (Math.Abs(delta) < bestDistance)
            {
                best = candidate;
                bestDistance = Math.Abs(delta);
            }
        }

        return best;
    }

    /// <summary>
    /// How much bigger the limit is, as a whole percentage, or null when the sentence does not
    /// say it unambiguously.
    ///
    /// <para><b>The hazard here is not extraction — it is the panel.</b> The weekly row already
    /// shows a percentage: utilisation, a <i>level</i>. A boost magnitude is a <i>delta</i>. Two
    /// unrelated percentages a hundred pixels apart is a genuine misreading risk, so this refuses
    /// every figure that is not plainly an increase:</para>
    ///
    /// <list type="number">
    /// <item><b>Exactly one percentage in the string</b>, or nothing is taken. Two means the
    /// sentence is doing something never observed, and picking one would be a guess.</item>
    /// <item><b>It must read as an increase</b> — an explicit <c>+</c>, or an increase word
    /// nearby. <c>"You have used 50% of your weekly limit"</c> must never become a boost
    /// figure, and that sentence is the reason this rule exists rather than a bare regex.</item>
    /// </list>
    ///
    /// <para>Note that a URL like <c>clau.de/cc-50-promo</c> carries <c>50</c> but not
    /// <c>50%</c>, so it does not count as a second match — checked against the real payload,
    /// not assumed.</para>
    /// </summary>
    public static int? Percent(string? text)
    {
        if (text is not { Length: > 0 })
        {
            return null;
        }

        var matches = PercentPattern.Matches(text);
        if (matches.Count != 1)
        {
            return null;
        }

        var match = matches[0];
        if (!int.TryParse(match.Groups["v"].Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            return null;
        }

        if (match.Groups["sign"].Success)
        {
            return value;
        }

        var start = Math.Max(0, match.Index - IncreaseWindow);
        var length = Math.Min(text.Length - start, match.Length + (2 * IncreaseWindow));
        return IncreaseWord.IsMatch(text.Substring(start, length)) ? value : null;
    }
}
