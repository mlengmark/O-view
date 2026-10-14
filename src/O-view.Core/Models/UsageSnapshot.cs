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
    /// <summary>
    /// <see cref="SessionPercent"/>, paired with the status the provider that built this
    /// snapshot gave it — <see cref="UsageValueStatus.Real"/> for a figure read straight from a
    /// source, <see cref="UsageValueStatus.Estimated"/> for one this app derived, and
    /// <see cref="UsageValueStatus.Unavailable"/> for "nothing was read". Built alongside the
    /// plain member above, from the same read, never guessed afterwards from whether
    /// <see cref="SessionPercent"/> happens to be null — a value can be unavailable for reasons
    /// other than "nothing was read" (see <see cref="UsagePercent"/>).
    ///
    /// <para>Defaults to <c>default(UsagePercent)</c> — <see cref="UsageValueStatus.Real"/> with
    /// no value — for the same reason <see cref="UsagePercent"/> itself documents: a provider
    /// that has not set this has not yet said anything about it. Every provider in this app that
    /// reports <see cref="SessionPercent"/> sets it explicitly.</para>
    /// </summary>
    public UsagePercent SessionPercentValue { get; init; }

    /// <inheritdoc cref="SessionPercentValue"/>
    public UsagePercent WeeklyPercentValue { get; init; }

    /// <inheritdoc cref="SessionPercentValue"/>
    public UsageInstant SessionResetAtUtcValue { get; init; }

    /// <inheritdoc cref="SessionPercentValue"/>
    public UsageInstant CapturedAtUtcValue { get; init; }

    /// <inheritdoc cref="SessionPercentValue"/>
    public UsageInstant WeeklyResetAtUtcValue { get; init; }

    /// <summary>The canonical "no data" snapshot: absent on every typed value, not just null.</summary>
    public static UsageSnapshot None { get; } = new(DataSource.None, null, null, null, null)
    {
        SessionPercentValue = new(null, UsageValueStatus.Unavailable),
        WeeklyPercentValue = new(null, UsageValueStatus.Unavailable),
        SessionResetAtUtcValue = new(null, UsageValueStatus.Unavailable),
        CapturedAtUtcValue = new(null, UsageValueStatus.Unavailable),
        WeeklyResetAtUtcValue = new(null, UsageValueStatus.Unavailable),
    };

    /// <summary>A figure read straight from a source: measured when present, absent when not.</summary>
    internal static UsagePercent Measured(int? value) =>
        new(value, value is null ? UsageValueStatus.Unavailable : UsageValueStatus.Real);

    /// <inheritdoc cref="Measured(int?)"/>
    internal static UsageInstant Measured(DateTimeOffset? value) =>
        new(value, value is null ? UsageValueStatus.Unavailable : UsageValueStatus.Real);

    /// <summary>An instant this app derived or predicted, rather than one a source reported.</summary>
    internal static UsageInstant Estimated(DateTimeOffset? value) =>
        new(value, value is null ? UsageValueStatus.Unavailable : UsageValueStatus.Estimated);
}
