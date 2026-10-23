namespace OView.Core.Models;

/// <summary>
/// One model's contribution to a tile's total (GitHub issue #37) — the per-model
/// breakdown behind "14.6K output tokens today".
/// </summary>
/// <param name="Model">The raw model id as recorded, e.g. <c>claude-opus-5</c>.</param>
/// <param name="Tokens">
/// <b>Output</b> tokens, matching the tile it sits behind (issue #253). Kept as
/// <c>Tokens</c> rather than renamed because this type also backs the Est. value tiles,
/// where the measure is money — <see cref="BreakdownMeasure"/> is what says which.
/// </param>
/// <param name="DisplayName">
/// The friendly name, or the raw id when the model is not recognised. Never invented:
/// an unknown id is shown as-is rather than guessed at (CLAUDE.md rule 6).
///
/// <para><b>Stays in Core, unlike most panel-facing text.</b> This is the model's own
/// published name — <see cref="ModelCatalog"/>'s data, carried alongside the id it
/// names rather than invented at render time — not prose composed about the model.</para>
/// </param>
/// <param name="FoldedCount">
/// Null for an ordinary model slice. <see cref="ModelBreakdown.Segments"/> sets it to the
/// number of models folded into its remainder segment — a non-null count is what marks a
/// slice as that remainder, rather than any word in <see cref="Model"/> or
/// <see cref="DisplayName"/>. What to call it — "Other", "<c>&lt;n&gt;</c> more
/// models" — is panel copy, built in Presentation from this count; Core does not write
/// those words. A trailing parameter with a default so slices built before this field
/// existed keep compiling.
///
/// <para><b>Stays a plain nullable, unlike <see cref="EstUsdValue"/>.</b> Null here says
/// "this is an ordinary model slice", not "nobody could count the models" — it discriminates
/// between two kinds of slice rather than reporting a figure whose trust a reader has to
/// weigh, so there is no status to pair it with.</para>
/// </param>
public sealed record ModelSlice(
    string Model, string DisplayName, long Tokens, int? FoldedCount = null)
{
    /// <summary>
    /// <see cref="Tokens"/>, paired with its <see cref="UsageValueStatus"/>. Always
    /// <see cref="UsageValueStatus.Real"/> — a model's token count is read directly from its
    /// rollups, never modelled, regardless of whether the model can be priced.
    /// </summary>
    public TokenCount TokensCount { get; init; }

    /// <summary>
    /// What this model's tokens would have cost at published API rates:
    /// <see cref="UsageValueStatus.Unavailable"/> when the model has no published rate,
    /// <see cref="UsageValueStatus.Estimated"/> otherwise. Unavailable is distinct from a
    /// figure of zero — zero is a real, known value (a priced model that happened to consume
    /// no tokens), whereas unavailable means O-view cannot price the model and says so rather
    /// than dropping it from the picture.
    ///
    /// <para>Unavailable unless the slice's builder prices it, so an unpriced slice can never
    /// read as a known zero.</para>
    /// </summary>
    public EstimatedUsd EstUsdValue { get; init; } = new(null, UsageValueStatus.Unavailable);
}

/// <summary>
/// Friendly names for model ids, read from <see cref="ModelCatalog"/> — the one table that
/// also carries each model's rate and billing class, so a model cannot be priced without
/// also being nameable (GitHub issue #56).
///
/// An unrecognised id yields no guess: it renders as its raw id, which is honest and still
/// identifies the row. Inventing "Opus 6" from a pattern would be a fabricated fact.
/// </summary>
public static class ModelDisplayName
{
    // No <synthetic> branch: TranscriptReader drops that marker at parse time, so it never
    // reaches a ModelSlice (issue #57).
    public static string For(string model) => ModelCatalog.Find(model)?.DisplayName ?? model;
}
