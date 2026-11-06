using System.Text.Json;

namespace OView.Core.Providers.CachedUsage;

/// <summary>
/// One model's own weekly limit, as Claude Code cached it in
/// <c>cachedUsageUtilization.utilization.limits[]</c>: a <c>weekly_scoped</c> row whose
/// <c>scope.model.display_name</c> names the model.
///
/// <para>The name is the display name and nothing else. <c>scope.model.id</c> reads null on
/// the one row seen, so there is no identifier to match against a model catalogue, and a name
/// this reader would have to guess is one it does not invent: a row without it is skipped.</para>
/// </summary>
/// <param name="Model">The model's display name, e.g. <c>Fable</c>.</param>
/// <param name="Bar">
/// Percentage and reset instant, the same pairing the two meters carry, so the one rule that
/// decides whether a cached percentage still describes its window
/// (<see cref="CachedUtilizationProvider.CurrentWindowPercent(UtilizationBar?, DateTimeOffset, TimeSpan)"/>)
/// applies to a model's limit without a second copy of it.
/// </param>
public sealed record ModelLimit(string Model, UtilizationBar Bar);

/// <summary>What the read made of <c>limits</c>, so a missing list can be told from a broken one.</summary>
public enum ModelLimitsState
{
    /// <summary>No <c>limits</c> in the block, or JSON <c>null</c>, or no <c>utilization</c> object to hold it.</summary>
    Absent,

    /// <summary>Present, but not a list.</summary>
    Unreadable,

    /// <summary>A list, even an empty one: the rows are counted.</summary>
    Read,
}

/// <summary>
/// Every model limit in a block, in file order, and what the read made of the rows it saw, so
/// <c>--diagnose</c> can say what happened rather than just show an empty list.
///
/// <para><b>A row whose reset has passed is kept.</b> Parsing does not gate on freshness, for
/// the reason <see cref="CachedUtilization.ExtraUsage"/> does not: whether a percentage still
/// describes its window is a question for the reader, with the clock to hand, and the account
/// still has a model limit when the figure behind it is out of date.</para>
///
/// <para><b>Nothing here depends on the plan.</b> A row qualifies by its shape, group
/// <c>weekly</c> with a named model and no surface, so an account that gains or loses a
/// scoped limit changes the list and not the code.</para>
/// </summary>
/// <param name="Limits">The rows that qualified, in the order the file lists them.</param>
/// <param name="RowsSeen">Entries in <c>limits[]</c>, qualifying or not. Zero when it is absent or not an array.</param>
/// <param name="RowsSkipped">
/// Entries that did not become a <see cref="ModelLimit"/>. That includes the account-wide
/// <c>session</c> and <c>weekly_all</c> rows, which the two meters already carry, so a healthy
/// block skips some; it is a count to read beside <paramref name="RowsSeen"/>, not an error count.
/// </param>
public sealed record ModelLimitRead(IReadOnlyList<ModelLimit> Limits, int RowsSeen, int RowsSkipped)
{
    /// <summary>Property inside <c>utilization</c> that holds the rows.</summary>
    public const string PropertyName = "limits";

    /// <summary>A block with no <c>limits</c>, or a JSON <c>null</c> one.</summary>
    public static readonly ModelLimitRead None = new([], 0, 0) { State = ModelLimitsState.Absent };

    private static readonly ModelLimitRead NotAList = new([], 0, 0) { State = ModelLimitsState.Unreadable };

    /// <summary>
    /// Whether <c>limits</c> was absent, not a list, or a list. An <c>init</c> property rather
    /// than a fourth positional parameter, so a row count built by hand reads as a list.
    /// </summary>
    public ModelLimitsState State { get; init; } = ModelLimitsState.Read;

    /// <summary>
    /// Reads the rows out of a <c>utilization</c> object. Never throws: a missing or null
    /// <c>limits</c> yields <see cref="None"/>, a non-array one a read in state
    /// <see cref="ModelLimitsState.Unreadable"/>, and a row of the wrong shape is
    /// skipped on its own without costing the rows beside it.
    /// </summary>
    public static ModelLimitRead Read(JsonElement utilization)
    {
        if (utilization.ValueKind != JsonValueKind.Object ||
            !utilization.TryGetProperty(PropertyName, out var rows) ||
            rows.ValueKind == JsonValueKind.Null)
        {
            return None;
        }

        if (rows.ValueKind != JsonValueKind.Array)
        {
            return NotAList;
        }

        var limits = new List<ModelLimit>();
        var seen = 0;
        foreach (var row in rows.EnumerateArray())
        {
            seen++;
            if (ReadRow(row) is { } limit)
            {
                limits.Add(limit);
            }
        }

        return new ModelLimitRead(limits, seen, seen - limits.Count);
    }

    private static ModelLimit? ReadRow(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object ||
            JsonFields.StringOrNull(row, "group") != "weekly" ||
            !row.TryGetProperty("scope", out var scope) ||
            scope.ValueKind != JsonValueKind.Object ||
            !scope.TryGetProperty("model", out var model) ||
            model.ValueKind != JsonValueKind.Object ||
            JsonFields.NonEmptyString(model, "display_name") is not { } name ||
            HasSurface(scope) ||
            !row.TryGetProperty("percent", out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out var percent) ||
            Math.Abs(percent) > int.MaxValue)
        {
            return null;
        }

        return new ModelLimit(
            name,
            new UtilizationBar((int)Math.Round(percent), UtcInstant.ParseOrNull(JsonFields.StringOrNull(row, "resets_at"))));
    }

    /// <summary>
    /// Whether the row is scoped to a surface as well, a limit on one model <i>within</i> one
    /// product rather than on the model. Absent and JSON <c>null</c> both mean no surface.
    /// </summary>
    private static bool HasSurface(JsonElement scope) =>
        scope.TryGetProperty("surface", out var surface) && surface.ValueKind != JsonValueKind.Null;
}
