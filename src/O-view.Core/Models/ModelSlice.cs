namespace OView.Core.Models;

/// <summary>
/// One model's contribution to a tile's total (GitHub issue #37) — the per-model
/// breakdown behind "20.5M tokens today".
/// </summary>
/// <param name="Model">The raw model id as recorded, e.g. <c>claude-opus-5</c>.</param>
/// <param name="DisplayName">
/// The friendly name, or the raw id when the model is not recognised. Never invented:
/// an unknown id is shown as-is rather than guessed at (CLAUDE.md rule 6).
/// </param>
/// <param name="EstUsd">
/// Null when the model has no published rate. Distinct from zero — zero is a real,
/// known value (<c>&lt;synthetic&gt;</c> records cost nothing because no API call was
/// made), whereas null means O-view cannot price it and must say so rather than
/// dropping the model from the picture.
/// </param>
public sealed record ModelSlice(string Model, string DisplayName, long Tokens, decimal? EstUsd);

/// <summary>
/// Friendly names for model ids. Longest-prefix-wins, mirroring
/// <see cref="Pricing.CostEstimator"/> — and, like it, an unrecognised id yields no
/// guess. A model Anthropic ships after this table was written renders as its raw id,
/// which is honest and still identifies the row; inventing "Opus 6" from a pattern
/// would be a fabricated fact.
/// </summary>
public static class ModelDisplayName
{
    private static readonly (string Prefix, string Name)[] Names =
    [
        ("claude-fable-5", "Fable 5"),
        ("claude-mythos-5", "Mythos 5"),
        ("claude-opus-5", "Opus 5"),
        ("claude-opus-4-8", "Opus 4.8"),
        ("claude-opus-4-7", "Opus 4.7"),
        ("claude-opus-4-6", "Opus 4.6"),
        ("claude-opus-4-5", "Opus 4.5"),
        ("claude-sonnet-5", "Sonnet 5"),
        ("claude-sonnet-4", "Sonnet 4"),
        ("claude-haiku-4-5", "Haiku 4.5"),
    ];

    /// <summary>Label for the aggregated remainder when there are more models than slots.</summary>
    public const string Other = "Other";

    public static string For(string model)
    {
        // Not a model at all — Claude Code's marker for a locally generated message.
        // "Local" says what it is without implying an API call happened.
        if (Pricing.CostEstimator.IsNonBillable(model))
        {
            return "Local";
        }

        foreach (var (prefix, name) in Names)
        {
            if (model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        return model;
    }
}
