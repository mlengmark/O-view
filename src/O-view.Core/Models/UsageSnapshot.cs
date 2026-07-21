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
/// Predicted next seven-day window reset. Derived from persisted `sd` drops with a
/// measured period — null until two resets have been observed (GitHub issue #6).
/// </param>
public sealed record UsageSnapshot(
    DataSource Source,
    int? SessionPercent,
    int? WeeklyPercent,
    DateTimeOffset? SessionResetAtUtc,
    DateTimeOffset? CapturedAtUtc,
    DateTimeOffset? WeeklyResetAtUtc = null)
{
    /// <summary>The canonical "no data" snapshot.</summary>
    public static UsageSnapshot None { get; } = new(DataSource.None, null, null, null, null);
}
