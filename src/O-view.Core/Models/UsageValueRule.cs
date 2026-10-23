namespace OView.Core.Models;

/// <summary>
/// The construction rules the status-paired value types in this folder enforce. Each type
/// calls into here from its own primary constructor, so the rule lives once rather than
/// being re-typed per type.
/// </summary>
internal static class UsageValueRule
{
    /// <summary>
    /// Returns <paramref name="value"/> unchanged, or throws when it is non-null while
    /// <paramref name="status"/> is <see cref="UsageValueStatus.Unavailable"/>.
    /// <see cref="UsageValueStatus.Unavailable"/> means no source produced a value, so a pair
    /// that carries both is a bug at the producer's call site, not a state a skin should ever
    /// have to render. The converse, a null value paired with
    /// <see cref="UsageValueStatus.Real"/> or <see cref="UsageValueStatus.Estimated"/>, is
    /// left alone: a value that happens to be unknown right now is a different claim from one
    /// nothing can ever produce.
    /// </summary>
    public static T? NoValueWhenUnavailable<T>(T? value, UsageValueStatus status)
        where T : struct
    {
        if (status == UsageValueStatus.Unavailable && value is not null)
        {
            throw new ArgumentException("A value flagged Unavailable must carry no value.", nameof(value));
        }

        return value;
    }

    /// <summary>
    /// Returns <paramref name="status"/> unchanged, or throws when it is
    /// <see cref="UsageValueStatus.Real"/>. For a figure this app only ever derives, never
    /// reads measured from a vendor, claiming <see cref="UsageValueStatus.Real"/> would be
    /// false regardless of whether a value is present.
    /// </summary>
    public static UsageValueStatus NeverReal(UsageValueStatus status)
    {
        if (status == UsageValueStatus.Real)
        {
            throw new ArgumentException("A modelled figure can never claim to be measured.", nameof(status));
        }

        return status;
    }
}
