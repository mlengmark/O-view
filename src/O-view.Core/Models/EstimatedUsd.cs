namespace OView.Core.Models;

/// <summary>
/// A modelled USD figure, paired with its <see cref="UsageValueStatus"/>. Spend is always
/// priced from token counts in this app, never read measured from a vendor, so
/// <see cref="Status"/> can only ever be <see cref="UsageValueStatus.Estimated"/> or
/// <see cref="UsageValueStatus.Unavailable"/> — constructing one with
/// <see cref="UsageValueStatus.Real"/> throws, independently of whether a value is present.
/// A value flagged <see cref="UsageValueStatus.Unavailable"/> never carries a figure — never a
/// fabricated "$0.00" standing in for "no source produced this". The converse is not guaranteed:
/// <see cref="Value"/> can still be null while <see cref="Status"/> is
/// <see cref="UsageValueStatus.Estimated"/>, a value that happens to be unknown right now rather
/// than one nothing can ever produce.
/// <para>
/// Both properties are get-only, so the pair can be set only through the constructor, which
/// checks both rules; no <c>with</c> expression or object initializer can bypass them.
/// <c>default(EstimatedUsd)</c> stays legal — it skips the constructor entirely and reads as
/// (<see cref="UsageValueStatus.Real"/>, null), the one place that status reaches this type.
/// </para>
/// </summary>
public readonly record struct EstimatedUsd(decimal? Value, UsageValueStatus Status)
{
    /// <summary>The modelled USD figure, or null when no source produced one.</summary>
    public decimal? Value { get; } = UsageValueRule.NoValueWhenUnavailable(Value, Status);

    /// <summary>The trust status of <see cref="Value"/>. Never <see cref="UsageValueStatus.Real"/>.</summary>
    public UsageValueStatus Status { get; } = UsageValueRule.NeverReal(Status);
}
