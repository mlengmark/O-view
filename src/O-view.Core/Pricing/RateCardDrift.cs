using System.Globalization;
using System.Text.RegularExpressions;

namespace OView.Core.Pricing;

/// <summary>One column of one model where the bundled table and the published one disagree.</summary>
public sealed record RateDelta(string Model, string Column, decimal Ours, decimal Published);

/// <summary>
/// The result of comparing the bundled rate card against Anthropic's published table.
///
/// <para><b>A difference list, never a rate card.</b> The bundled table stays authoritative
/// until a human confirms a change. A broken parser then produces a false "check pricing"
/// nudge, which is noisy and harmless; a parser that broke while <i>writing</i> rates would
/// produce confident wrong money (GitHub issue #257).</para>
///
/// <para>An empty <see cref="Differences"/> means the two agree. The check having failed is
/// reported as <c>null</c> by the caller — an honest "did not check", never a silent pass.</para>
/// </summary>
public sealed record RateCardDrift(DateOnly CheckedOn, IReadOnlyList<RateDelta> Differences)
{
    public bool Agrees => Differences.Count == 0;

    /// <summary>One line for the log and the diagnostics bundle.</summary>
    public string Describe() => Agrees
        ? $"rate check {CheckedOn:yyyy-MM-dd}: published table agrees"
        : $"rate check {CheckedOn:yyyy-MM-dd}: {Differences.Count} difference(s) — "
          + string.Join("; ", Differences.Select(d =>
              string.Create(CultureInfo.InvariantCulture,
                  $"{d.Model} {d.Column} ours ${d.Ours} published ${d.Published}")));
}

/// <summary>
/// Reads Anthropic's published pricing table and compares it against a
/// <see cref="RateCard"/>.
///
/// <para>Kept in Core, away from the HTTP that fetches the page, for the same reason
/// <c>UpdateCheck</c> is kept away from <c>ReleaseFeed</c>: the comparison is the part with
/// rules worth pinning, and it is testable against a fixture rather than against a network.</para>
/// </summary>
public static class PublishedRates
{
    /// <summary>
    /// The published table as markdown. It carries all five columns, including the two cache
    /// writes, which is what makes a value-by-value comparison possible at all.
    /// </summary>
    public const string PricingUrl = "https://platform.claude.com/docs/en/about-claude/pricing.md";

    /// <summary>
    /// Compares <paramref name="card"/> against the published markdown, or returns null when
    /// the page could not be understood.
    ///
    /// <para><b>Null rather than a partial answer.</b> A table that parsed to two rows out of
    /// fifteen would report thirteen models as "not published" and read as a wholesale price
    /// change. The floor is deliberately crude — a header naming the five columns, and at
    /// least one model row — because the failure being guarded against is the page's shape
    /// changing, not a single row going missing.</para>
    ///
    /// <para>A model in the card that the page does not list is <b>not</b> a difference. The
    /// published table names models by display name and this matches on that, so a rename
    /// upstream must read as "could not check that row" rather than as a price change.</para>
    /// </summary>
    public static RateCardDrift? Compare(RateCard card, string markdown, DateOnly checkedOn)
    {
        if (ParseTable(markdown) is not { Count: > 0 } published)
        {
            return null;
        }

        var differences = new List<RateDelta>();

        foreach (var entry in card.Models)
        {
            if (!published.TryGetValue(entry.DisplayName, out var theirs))
            {
                continue;
            }

            Compare(entry.DisplayName, "input", entry.Rates.InputPerMTok, theirs.InputPerMTok);
            Compare(entry.DisplayName, "5m cache write", entry.Rates.CacheWrite5mPerMTok, theirs.CacheWrite5mPerMTok);
            Compare(entry.DisplayName, "1h cache write", entry.Rates.CacheWrite1hPerMTok, theirs.CacheWrite1hPerMTok);
            Compare(entry.DisplayName, "cache read", entry.Rates.CacheReadPerMTok, theirs.CacheReadPerMTok);
            Compare(entry.DisplayName, "output", entry.Rates.OutputPerMTok, theirs.OutputPerMTok);
        }

        return new RateCardDrift(checkedOn, differences);

        void Compare(string model, string column, decimal ours, decimal them)
        {
            if (ours != them)
            {
                differences.Add(new RateDelta(model, column, ours, them));
            }
        }
    }

    /// <summary>
    /// The model-pricing table, keyed by the display name the page uses, or null when no such
    /// table was found.
    ///
    /// <para>The page carries several tables — batch pricing, fast mode, tool-use token counts
    /// — with the same <c>| Model | … |</c> opening, so the header is matched on all five
    /// column names rather than on the first pipe row. Rows stop at the first line that is not
    /// a table row, which is what keeps a later table from being read as more of this one.</para>
    /// </summary>
    private static Dictionary<string, ModelRates>? ParseTable(string markdown)
    {
        var lines = markdown.Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            if (!IsModelPricingHeader(lines[i]))
            {
                continue;
            }

            var rows = new Dictionary<string, ModelRates>(StringComparer.OrdinalIgnoreCase);

            // Skip the header and the separator row beneath it.
            for (var r = i + 2; r < lines.Length && lines[r].TrimStart().StartsWith('|'); r++)
            {
                if (ParseRow(lines[r]) is { } row)
                {
                    rows[row.Model] = row.Rates;
                }
            }

            return rows.Count > 0 ? rows : null;
        }

        return null;
    }

    private static bool IsModelPricingHeader(string line)
    {
        var cells = Cells(line);
        return cells.Length == 6
            && cells[0].Equals("Model", StringComparison.OrdinalIgnoreCase)
            && cells[1].Contains("Base Input", StringComparison.OrdinalIgnoreCase)
            && cells[2].Contains("5m Cache", StringComparison.OrdinalIgnoreCase)
            && cells[3].Contains("1h Cache", StringComparison.OrdinalIgnoreCase)
            && cells[4].Contains("Cache Hits", StringComparison.OrdinalIgnoreCase)
            && cells[5].Contains("Output", StringComparison.OrdinalIgnoreCase);
    }

    private static (string Model, ModelRates Rates)? ParseRow(string line)
    {
        var cells = Cells(line);
        if (cells.Length != 6)
        {
            return null;
        }

        var name = ModelName(cells[0]);
        if (name.Length == 0)
        {
            return null;
        }

        if (Price(cells[1]) is not { } input || Price(cells[2]) is not { } write5m
            || Price(cells[3]) is not { } write1h || Price(cells[4]) is not { } read
            || Price(cells[5]) is not { } output)
        {
            return null;
        }

        return (name, new ModelRates(input, output, write5m, write1h, read));
    }

    /// <summary>The cells of a markdown table row, trimmed, without the leading/trailing pipes.</summary>
    private static string[] Cells(string line)
    {
        var trimmed = line.Trim();
        if (!trimmed.StartsWith('|') || !trimmed.EndsWith('|'))
        {
            return [];
        }

        return trimmed[1..^1].Split('|').Select(c => c.Trim()).ToArray();
    }

    /// <summary>
    /// <c>Claude Opus 4.1 ([retired, …](…))</c> → <c>Opus 4.1</c>.
    ///
    /// <para>The page qualifies several rows with a parenthesised link, and prefixes every name
    /// with "Claude" where <see cref="Models.ModelCatalog"/> does not. Both are stripped so the
    /// comparison is between model names rather than between two spellings of one.</para>
    /// </summary>
    private static string ModelName(string cell)
    {
        var name = cell;
        var bracket = name.IndexOf('(', StringComparison.Ordinal);
        if (bracket >= 0)
        {
            name = name[..bracket];
        }

        name = name.Trim();
        return name.StartsWith("Claude ", StringComparison.OrdinalIgnoreCase)
            ? name["Claude ".Length..].Trim()
            : name;
    }

    /// <summary><c>$12.50 / MTok</c> → <c>12.50</c>. Null when the cell is not a price.</summary>
    private static decimal? Price(string cell)
    {
        var match = PriceCell.Match(cell);
        return match.Success
               && decimal.TryParse(match.Groups[1].Value, NumberStyles.Number,
                   CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static readonly Regex PriceCell =
        new(@"^\$([0-9]+(?:\.[0-9]+)?)\s*/\s*MTok$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
}
