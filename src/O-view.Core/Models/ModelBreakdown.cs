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
    /// The models that get their own colour, in slot order — computed once for the whole
    /// panel from the widest window, and used by every tile.
    ///
    /// This exists because colour must follow the MODEL, never its rank within one tile.
    /// Colouring by rank meant Opus 5 was blue where it happened to be the largest and
    /// orange where it was second, so the same model wore two colours on one panel and
    /// the four tiles could not be read against each other.
    ///
    /// Ranked by tokens over the 31-day window rather than by value, and rather than
    /// per-tile: it is the superset every other tile draws from, so a model's colour is
    /// decided once and holds everywhere, including on the value tiles where the ordering
    /// differs.
    /// </summary>
    public static IReadOnlyList<string> ColourOrder(IReadOnlyList<ModelSlice> windowSlices)
    {
        var ranked = windowSlices
            .Where(s => s.Tokens > 0)
            .OrderByDescending(s => s.Tokens)
            .ThenBy(s => s.Model, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Same tiering as before, now decided once for the panel instead of per tile:
        // three models each keep a colour, four or more give up the third slot so the
        // neutral remainder has something it is separable from.
        var slots = ranked.Count <= MaxChromaticSlots ? MaxChromaticSlots : NamedSlotsBesideOther;
        return ranked.Take(slots).Select(s => s.Model).ToList();
    }

    /// <summary>The slot a model's colour comes from, or -1 for the neutral remainder.</summary>
    public static int SlotFor(string model, IReadOnlyList<string> colourOrder)
    {
        for (var i = 0; i < colourOrder.Count; i++)
        {
            if (string.Equals(colourOrder[i], model, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Orders by the given measure, largest first, folding anything outside
    /// <paramref name="colourOrder"/> into the remainder.
    ///
    /// The fold is driven by the shared colour order, not by this tile's own ranking, so
    /// a model that is "Other" on one tile is "Other" on all of them.
    ///
    /// Zero-measure models are dropped: a segment of width zero is invisible but its
    /// legend entry is not, so it reads as a model that contributed nothing to a total
    /// it is listed under. For <see cref="BreakdownMeasure.EstValue"/> that also removes
    /// unpriced models (null) — they are NOT silently lost, they are reported by
    /// <see cref="Unpriced"/> so the caller can state the gap (rule 6).
    /// </summary>
    public static IReadOnlyList<ModelSlice> Segments(
        IReadOnlyList<ModelSlice> slices, BreakdownMeasure measure, IReadOnlyList<string> colourOrder)
    {
        var present = slices.Where(s => Measure(s, measure) > 0).ToList();

        var named = present
            .Where(s => SlotFor(s.Model, colourOrder) >= 0)
            .OrderByDescending(s => Measure(s, measure))
            .ThenBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rest = present.Where(s => SlotFor(s.Model, colourOrder) < 0).ToList();
        if (rest.Count == 0)
        {
            return named;
        }

        // A single leftover keeps its own name — "Other · 1 more models" would be a
        // clumsy way to say "Haiku 4.5". It still takes the neutral colour, because it is
        // not one of the panel's coloured models.
        named.Add(rest.Count == 1
            ? rest[0]
            : new ModelSlice(
                Model: $"{rest.Count} more",
                DisplayName: ModelDisplayName.Other,
                Tokens: rest.Sum(s => s.Tokens),
                // The remainder keeps a real summed value so the segment's width is the
                // truth — but stays unknown if anything in it was unpriced.
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
