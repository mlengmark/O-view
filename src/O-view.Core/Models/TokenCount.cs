namespace OView.Core.Models;

/// <summary>
/// A token count, paired with its <see cref="UsageValueStatus"/>. A value flagged
/// <see cref="UsageValueStatus.Unavailable"/> never carries a figure — never a fabricated zero
/// standing in for "no source produced this". The converse is not guaranteed:
/// <see cref="Value"/> can still be null while <see cref="Status"/> is
/// <see cref="UsageValueStatus.Real"/> or <see cref="UsageValueStatus.Estimated"/>, a value that
/// happens to be unknown right now rather than one nothing can ever produce.
/// <para>
/// Both properties are get-only, so the pair can be set only through the constructor, which
/// checks the rule; no <c>with</c> expression or object initializer can bypass it.
/// <c>default(TokenCount)</c> stays legal — it skips the constructor entirely and reads as
/// (<see cref="UsageValueStatus.Real"/>, null).
/// </para>
/// </summary>
public readonly record struct TokenCount(long? Value, UsageValueStatus Status)
{
    /// <summary>The token count, or null when no source produced one.</summary>
    public long? Value { get; } = UsageValueRule.NoValueWhenUnavailable(Value, Status);

    /// <summary>The trust status of <see cref="Value"/>.</summary>
    public UsageValueStatus Status { get; } = Status;

    /// <summary>
    /// A count read directly from recorded transcripts or rollups, so it is
    /// <see cref="UsageValueStatus.Real"/>: the only status a counted figure can carry.
    /// </summary>
    public static TokenCount Measured(long value) => new(value, UsageValueStatus.Real);
}
