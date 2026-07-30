namespace OView.App;

/// <summary>
/// The current time, injectable so the engine's cadence and window arithmetic can be
/// tested without waiting for a real clock. Times are UTC throughout and converted to
/// local only at the display edge (CLAUDE.md conventions).
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>The real clock. Stateless, so one instance serves the process.</summary>
public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
