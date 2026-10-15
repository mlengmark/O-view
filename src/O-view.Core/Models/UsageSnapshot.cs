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
    /// The status behind <see cref="SessionPercent"/> — <see cref="UsageValueStatus.Real"/> for
    /// a figure read straight from a source, <see cref="UsageValueStatus.Estimated"/> for one
    /// this app derived, and <see cref="UsageValueStatus.Unavailable"/> for "nothing was read".
    ///
    /// <para>Defaults to <see cref="UsageValueStatus.Real"/> when <see cref="SessionPercent"/>
    /// is set and <see cref="UsageValueStatus.Unavailable"/> when it is null — "measured" is
    /// what every value in this app is unless a provider says otherwise, and a provider that
    /// has not set this has not yet said anything about it. Only a provider reporting an
    /// estimated or modelled figure needs to override it.</para>
    /// </summary>
    public UsageValueStatus SessionPercentStatus { get; init; } =
        SessionPercent is null ? UsageValueStatus.Unavailable : UsageValueStatus.Real;

    /// <inheritdoc cref="SessionPercentStatus"/>
    public UsageValueStatus WeeklyPercentStatus { get; init; } =
        WeeklyPercent is null ? UsageValueStatus.Unavailable : UsageValueStatus.Real;

    /// <inheritdoc cref="SessionPercentStatus"/>
    public UsageValueStatus SessionResetAtUtcStatus { get; init; } =
        SessionResetAtUtc is null ? UsageValueStatus.Unavailable : UsageValueStatus.Real;

    /// <inheritdoc cref="SessionPercentStatus"/>
    public UsageValueStatus CapturedAtUtcStatus { get; init; } =
        CapturedAtUtc is null ? UsageValueStatus.Unavailable : UsageValueStatus.Real;

    /// <inheritdoc cref="SessionPercentStatus"/>
    public UsageValueStatus WeeklyResetAtUtcStatus { get; init; } =
        WeeklyResetAtUtc is null ? UsageValueStatus.Unavailable : UsageValueStatus.Real;

    /// <summary>
    /// <see cref="SessionPercent"/>, paired with <see cref="SessionPercentStatus"/>. Computed
    /// from the plain member above rather than stored alongside it, so the two can never
    /// disagree — including across a <c>with</c> expression that changes only
    /// <see cref="SessionPercent"/>, which carries <see cref="SessionPercentStatus"/> forward
    /// unchanged and this recomputes against the new figure.
    /// </summary>
    public UsagePercent SessionPercentValue => new(SessionPercent, SessionPercentStatus);

    /// <inheritdoc cref="SessionPercentValue"/>
    public UsagePercent WeeklyPercentValue => new(WeeklyPercent, WeeklyPercentStatus);

    /// <inheritdoc cref="SessionPercentValue"/>
    public UsageInstant SessionResetAtUtcValue => new(SessionResetAtUtc, SessionResetAtUtcStatus);

    /// <inheritdoc cref="SessionPercentValue"/>
    public UsageInstant CapturedAtUtcValue => new(CapturedAtUtc, CapturedAtUtcStatus);

    /// <inheritdoc cref="SessionPercentValue"/>
    public UsageInstant WeeklyResetAtUtcValue => new(WeeklyResetAtUtc, WeeklyResetAtUtcStatus);

    /// <summary>
    /// The canonical "no data" snapshot: absent on every typed value, not just null — true by
    /// construction here, since every plain member above is null and the status defaults follow.
    /// </summary>
    public static UsageSnapshot None { get; } = new(DataSource.None, null, null, null, null);

    /// <summary>
    /// The status for a reading this app derived or predicted, rather than one a source
    /// reported: estimated when there is a figure to label, absent when there is not — never
    /// "estimated" for a value nothing produced.
    /// </summary>
    internal static UsageValueStatus EstimatedStatus(DateTimeOffset? value) =>
        value is null ? UsageValueStatus.Unavailable : UsageValueStatus.Estimated;
}
