namespace OView.Core.Models;

/// <summary>
/// A point-in-time view of Claude usage. Every reading except <see cref="Source"/> carries its
/// own <see cref="UsageValueStatus"/> alongside its figure, so a reader is told whether the
/// figure was measured, derived, or never produced at all — rather than inferring that from a
/// null it has to guess the meaning of (CLAUDE.md rule 6).
/// </summary>
/// <param name="Source">Where this data came from and how fresh it is.</param>
/// <param name="SessionPercentValue">Five-hour window utilisation, 0–100. Integer precision only.</param>
/// <param name="WeeklyPercentValue">Seven-day window utilisation, 0–100. Integer precision only.</param>
/// <param name="SessionResetAtUtcValue">
/// Predicted next five-hour window reset. Derived from observed drops, not reported by the
/// source, so it is <see cref="UsageValueStatus.Estimated"/> where it is known and
/// <see cref="UsageValueStatus.Unavailable"/> until at least one drop has been observed
/// (ADR-0007).
/// </param>
/// <param name="CapturedAtUtcValue">When the underlying sample was taken. Drives staleness labels.</param>
/// <param name="WeeklyResetAtUtcValue">
/// Predicted next seven-day window reset, derived from persisted `sd` drops —
/// <see cref="UsageValueStatus.Unavailable"/> until one has been observed, which the UI reports
/// as still waiting rather than as broken (GitHub issue #6, ADR-0011).
/// </param>
/// <param name="WeeklyResetPeriod">
/// Cadence between weekly resets, as derived. Carried so the usage graph can step back from
/// <paramref name="WeeklyResetAtUtcValue"/> to draw past week boundaries on exactly the cadence
/// the countdown uses. Null alongside an unavailable <paramref name="WeeklyResetAtUtcValue"/>.
///
/// <para>Stays a plain nullable, unlike the readings above: it qualifies another value rather
/// than being one the panel states, and there is no status to attach to a cadence nothing
/// reports.</para>
/// </param>
/// <param name="SessionResetUncertainty">
/// How wide the bracket around <paramref name="SessionResetAtUtcValue"/> is, so a window start
/// inferred across a sampling gap is marked approximate rather than printed to the minute. A
/// plain nullable for the same reason as <paramref name="WeeklyResetPeriod"/>: null means there
/// is no bracket, not that one is unknown.
/// </param>
public sealed record UsageSnapshot(
    DataSource Source,
    UsagePercent SessionPercentValue,
    UsagePercent WeeklyPercentValue,
    UsageInstant SessionResetAtUtcValue,
    UsageInstant CapturedAtUtcValue,
    UsageInstant WeeklyResetAtUtcValue,
    TimeSpan? WeeklyResetPeriod = null,
    TimeSpan? SessionResetUncertainty = null)
{
    /// <summary>
    /// The canonical "no data" snapshot: every reading unavailable, which is a different claim
    /// from every reading being zero.
    /// </summary>
    public static UsageSnapshot None { get; } = new(
        DataSource.None, Percent(null), Percent(null), Instant(null), Instant(null), Instant(null));

    /// <summary>
    /// A percentage as read straight from a source: <see cref="UsageValueStatus.Real"/> where
    /// there is a figure, <see cref="UsageValueStatus.Unavailable"/> where the source produced
    /// none.
    ///
    /// <para>"Measured" is what every reading in this app is unless a provider says otherwise,
    /// so this is the shape a provider reaches for by default; only one reporting a derived or
    /// modelled figure needs <see cref="DerivedInstant"/> or a status of its own. The pairing
    /// lives here, in one place, rather than being re-decided at each of the call sites that
    /// build a snapshot.</para>
    /// </summary>
    public static UsagePercent Percent(int? value) => new(value, Measured(value));

    /// <inheritdoc cref="Percent"/>
    public static UsageInstant Instant(DateTimeOffset? value) => new(value, Measured(value));

    /// <summary>
    /// An instant this app derived or predicted rather than one a source reported: estimated
    /// where there is a figure to label, unavailable where there is not — never "estimated" for
    /// a value nothing produced.
    /// </summary>
    internal static UsageInstant DerivedInstant(DateTimeOffset? value) =>
        new(value, value is null ? UsageValueStatus.Unavailable : UsageValueStatus.Estimated);

    private static UsageValueStatus Measured<T>(T? value) where T : struct =>
        value is null ? UsageValueStatus.Unavailable : UsageValueStatus.Real;
}
