using OView.Core.Models;

namespace OView.Core.Providers;

/// <summary>
/// A source of usage data. Implementations must never throw from
/// <see cref="GetSnapshot"/>: unavailable or malformed data yields
/// <see cref="UsageSnapshot.None"/> (build-plan Phase 1 acceptance).
/// </summary>
public interface IUsageProvider
{
    /// <summary>
    /// Produce the current snapshot. <paramref name="utcNow"/> is injected rather than
    /// read from the clock so staleness and reset prediction are testable.
    /// </summary>
    UsageSnapshot GetSnapshot(DateTimeOffset utcNow);
}
