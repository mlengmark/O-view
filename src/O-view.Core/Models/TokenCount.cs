namespace OView.Core.Models;

/// <summary>
/// A token count, paired with its <see cref="UsageValueStatus"/>.
/// <see cref="Value"/> is null exactly when <see cref="Status"/> is
/// <see cref="UsageValueStatus.Unavailable"/> — never a fabricated zero standing in for "no
/// source produced this".
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
}
