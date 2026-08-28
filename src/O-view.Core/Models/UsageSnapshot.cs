namespace OView.Core.Models;

/// <summary>
/// A point-in-time view of Claude usage. Every field except <see cref="Source"/> is
/// nullable: null means "genuinely unknown", and the UI shows it as unknown rather
/// than guessing (CLAUDE.md rule 6).
/// </summary>
/// <param name="Source">Where this data came from and how fresh it is.</param>
/// <param name="SessionPercent">Five-hour window utilisation, 0–100. Integer precision only.</param>
/// <param name="WeeklyPercent">Seven-day window utilisation, 0–100. Integer precision only.</param>
/// <param name="SessionResetAtUtc">
/// Predicted next five-hour window reset. Derived from observed drops, not reported by
/// the source — null until at least one drop has been observed (ADR-0007).
/// </param>
/// <param name="CapturedAtUtc">When the underlying sample was taken. Drives staleness labels.</param>
/// <param name="WeeklyResetAtUtc">
/// Predicted next seven-day window reset, derived from persisted `sd` drops — null until
/// one has been observed, which the UI reports as still waiting rather than as broken
/// (GitHub issue #6, ADR-0011).
/// </param>
/// <param name="WeeklyResetPeriod">
/// Cadence between weekly resets, as derived. Carried so the usage graph can step back from
/// <paramref name="WeeklyResetAtUtc"/> to draw past week boundaries on exactly the cadence
/// the countdown uses. Null alongside <paramref name="WeeklyResetAtUtc"/>.
/// </param>
public sealed record UsageSnapshot(
    DataSource Source,
    int? SessionPercent,
    int? WeeklyPercent,
    DateTimeOffset? SessionResetAtUtc,
    DateTimeOffset? CapturedAtUtc,
    DateTimeOffset? WeeklyResetAtUtc = null,
    TimeSpan? WeeklyResetPeriod = null,
    TimeSpan? SessionResetUncertainty = null)
{
    /// <summary>The canonical "no data" snapshot.</summary>
    public static UsageSnapshot None { get; } = new(DataSource.None, null, null, null, null);
}
