namespace OView.Core.Pricing;

/// <summary>
/// Prices tokens at public API rates to produce the "Est. value" figures. This is
/// NOT money charged — within plan limits the marginal cost is £0/$0 (CLAUDE.md
/// rule 6; ui-spec.md). The UI must always prefix these figures "Est."
///
/// Rates per million tokens, from Anthropic's published API pricing as cached
/// 2026-06-24. Cache writes ≈1.25× input (5-minute TTL rate), cache reads ≈0.1×
/// input. Unknown models return null — an honest unknown, never a guessed rate.
/// </summary>
public static class CostEstimator
{
    private sealed record Rate(decimal InputPerMTok, decimal OutputPerMTok);

    // Longest-prefix wins; order here is most-specific first.
    private static readonly (string Prefix, Rate Rate)[] Rates =
    [
        ("claude-fable-5", new Rate(10.00m, 50.00m)),
        ("claude-mythos-5", new Rate(10.00m, 50.00m)),
        ("claude-opus-5", new Rate(5.00m, 25.00m)),
        ("claude-opus-4-8", new Rate(5.00m, 25.00m)),
        ("claude-opus-4-7", new Rate(5.00m, 25.00m)),
        ("claude-opus-4-6", new Rate(5.00m, 25.00m)),
        ("claude-opus-4-5", new Rate(5.00m, 25.00m)),
        ("claude-sonnet-5", new Rate(3.00m, 15.00m)),
        ("claude-sonnet-4", new Rate(3.00m, 15.00m)),
        ("claude-haiku-4-5", new Rate(1.00m, 5.00m)),
    ];

    private const decimal CacheWriteMultiplier = 1.25m;
    private const decimal CacheReadMultiplier = 0.10m;

    /// <summary>
    /// Transcript records that stand for no billable model call. Claude Code writes
    /// <c>&lt;synthetic&gt;</c> for locally generated assistant messages (interrupts,
    /// "prompt too long" notices) — there was no API request, so the value is genuinely
    /// zero rather than unknown. Treating it as an unpriced *model* is what blanked the
    /// "Est. value" tiles, since one unpriced entry voided the whole total.
    /// </summary>
    public static bool IsNonBillable(string model) =>
        model.Equals("<synthetic>", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Estimated USD value of the given token counts; 0 for non-billable records, and
    /// null when the model's rate is unknown (the caller labels it, never guessing a rate).
    /// </summary>
    public static decimal? EstimateUsd(
        string model, long inputTokens, long cacheCreationTokens, long cacheReadTokens, long outputTokens)
    {
        if (IsNonBillable(model)) return 0m;

        var rate = Rates.FirstOrDefault(r => model.StartsWith(r.Prefix, StringComparison.OrdinalIgnoreCase)).Rate;
        if (rate is null) return null;

        const decimal mtok = 1_000_000m;
        return (inputTokens * rate.InputPerMTok
                + cacheCreationTokens * rate.InputPerMTok * CacheWriteMultiplier
                + cacheReadTokens * rate.InputPerMTok * CacheReadMultiplier
                + outputTokens * rate.OutputPerMTok) / mtok;
    }
}
