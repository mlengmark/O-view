namespace OView.Core.Models;

/// <summary>
/// A UTC instant, paired with its <see cref="UsageValueStatus"/>. Core hands over the instant
/// itself, never a formatted string — locale and format belong to a skin, not Core.
/// <see cref="Value"/> is null exactly when <see cref="Status"/> is
/// <see cref="UsageValueStatus.Unavailable"/> — never a fabricated time standing in for "no
/// source produced this".
/// <para>
/// Both properties are get-only, so the pair can be set only through the constructor, which
/// checks the rule; no <c>with</c> expression or object initializer can bypass it.
/// <c>default(UsageInstant)</c> stays legal — it skips the constructor entirely and reads as
/// (<see cref="UsageValueStatus.Real"/>, null).
/// </para>
/// </summary>
public readonly record struct UsageInstant(DateTimeOffset? Value, UsageValueStatus Status)
{
    /// <summary>The instant, or null when no source produced one.</summary>
    public DateTimeOffset? Value { get; } = UsageValueRule.NoValueWhenUnavailable(Value, Status);

    /// <summary>The trust status of <see cref="Value"/>.</summary>
    public UsageValueStatus Status { get; } = Status;
}
