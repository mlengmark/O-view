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
/// </param>
/// <param name="EstUsd">
/// Null when the model has no published rate. Distinct from zero — zero is a real,
/// known value (a priced model that happened to consume no tokens), whereas null means
/// O-view cannot price it and must say so rather than dropping the model from the
/// picture.
/// </param>
public sealed record ModelSlice(string Model, string DisplayName, long Tokens, decimal? EstUsd);

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
    /// <summary>Label for the aggregated remainder when there are more models than slots.</summary>
    public const string Other = "Other";

    // No <synthetic> branch: Claude Code's marker for locally generated messages is
    // dropped at parse time by TranscriptReader, so it never reaches a ModelSlice. The
    // "Local" case that used to live here was unreachable (issue #57).
    public static string For(string model) => ModelCatalog.Find(model)?.DisplayName ?? model;
}
