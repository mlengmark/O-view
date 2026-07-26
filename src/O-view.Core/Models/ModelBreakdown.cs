namespace OView.Core.Models;

/// <summary>Which measure a tile's breakdown is split by (GitHub issue #37).</summary>
public enum BreakdownMeasure
{
    Tokens,
    EstValue,
}

/// <summary>
/// Shapes a per-model list into the segments a tile chart can actually show.
///
/// The cap is not arbitrary and not a layout choice — it comes from the colour
/// validator. The chart's segment order follows the data (largest first), so any two
/// colours can end up adjacent and the palette has to clear the separation gates on
/// ALL pairs, not just neighbours. Measured against the tile surfaces:
///
///   • three chromatic slots (blue/orange/aqua) clear every gate in both themes —
///     worst CVD ΔE 9.2 light / 9.4 dark against a target of 8;
///   • adding a neutral "Other" alongside them does not: no grey exists that is both
///     inside the dark lightness band and separable from the aqua slot for
///     deuteranopes (the sweep bottoms out at ΔE 3.0), because grey has no hue to
///     separate on and sits at the same lightness.
///
/// So the shape tiers: up to three models get their own colour, and four or more get
/// two named models plus a validated neutral remainder (worst CVD ΔE 11.0 / 12.2).
/// Dropping to two named models is the cost of showing an honest "Other" at all.
/// </summary>
public static class ModelBreakdown
{
    /// <summary>Most models that can be named before the remainder is folded into "Other".</summary>
    public const int MaxChromaticSlots = 3;

    /// <summary>Named models shown once an "Other" segment is present.</summary>
    public const int NamedSlotsBesideOther = 2;

    /// <summary>
    /// Orders by the given measure, largest first, and folds the tail into "Other" when
    /// there are more models than slots.
    ///
    /// Zero-measure models are dropped: a segment of width zero is invisible but its
    /// legend entry is not, so it reads as a model that contributed nothing to a total
    /// it is listed under. For <see cref="BreakdownMeasure.EstValue"/> that also removes
    /// unpriced models (null) — they are NOT silently lost, they are reported by
    /// <see cref="Unpriced"/> so the caller can state the gap (rule 6).
    /// </summary>
    public static IReadOnlyList<ModelSlice> Segments(
        IReadOnlyList<ModelSlice> slices, BreakdownMeasure measure)
    {
        var ranked = slices
            .Where(s => Measure(s, measure) > 0)
            .OrderByDescending(s => Measure(s, measure))
            .ThenBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ranked.Count <= MaxChromaticSlots)
        {
            return ranked;
        }

        var named = ranked.Take(NamedSlotsBesideOther).ToList();
        var rest = ranked.Skip(NamedSlotsBesideOther).ToList();

        // The remainder keeps a real summed value in both measures, so the segment's
        // width is the truth rather than a placeholder. Its Model field carries the
        // count so the UI can name what was folded in.
        named.Add(new ModelSlice(
            Model: $"{rest.Count} more",
            DisplayName: ModelDisplayName.Other,
            Tokens: rest.Sum(s => s.Tokens),
            EstUsd: rest.Any(s => s.EstUsd is null) ? null : rest.Sum(s => s.EstUsd ?? 0m)));

        return named;
    }

    /// <summary>
    /// Models excluded from an <see cref="BreakdownMeasure.EstValue"/> breakdown because
    /// they have no published rate. The value chart cannot place them — their dollar
    /// value is unknown, not zero — so the caller names them instead of letting the
    /// chart imply the total is complete.
    /// </summary>
    public static IReadOnlyList<string> Unpriced(IReadOnlyList<ModelSlice> slices) =>
        slices.Where(s => s.EstUsd is null).Select(s => s.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The measure's value for one slice, as a double for proportioning.</summary>
    public static double Measure(ModelSlice slice, BreakdownMeasure measure) => measure switch
    {
        BreakdownMeasure.Tokens => slice.Tokens,
        BreakdownMeasure.EstValue => (double)(slice.EstUsd ?? 0m),
        _ => 0,
    };
}
