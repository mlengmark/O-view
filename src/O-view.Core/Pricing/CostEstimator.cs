using OView.Core.Models;

namespace OView.Core.Pricing;

/// <summary>
/// Prices tokens at public API rates to produce the "Est. value" figures. This is
/// NOT money charged — within plan limits the marginal cost is £0/$0 (CLAUDE.md
/// rule 6; ui-spec.md). The UI must always prefix these figures "Est."
///
/// Rates live in <see cref="ModelCatalog"/>, beside each model's friendly name and
/// billing class, so adding a model is one row rather than three edits across three
/// files (GitHub issue #56). Cache writes ≈1.25× input (5-minute TTL rate), cache
/// reads ≈0.1× input. Unknown models return null — an honest unknown, never a
/// guessed rate.
/// </summary>
public static class CostEstimator
{
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

        if (ModelCatalog.Find(model) is not { } rate) return null;

        const decimal mtok = 1_000_000m;
        return (inputTokens * rate.InputPerMTok
                + cacheCreationTokens * rate.InputPerMTok * CacheWriteMultiplier
                + cacheReadTokens * rate.InputPerMTok * CacheReadMultiplier
                + outputTokens * rate.OutputPerMTok) / mtok;
    }
}
