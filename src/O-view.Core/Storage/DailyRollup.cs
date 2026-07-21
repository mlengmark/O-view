namespace OView.Core.Storage;

/// <summary>Aggregated usage for one (UTC date × model) — the grain ADR-0006 serves.</summary>
public sealed record DailyRollup(
    DateOnly DateUtc,
    string Model,
    long InputTokens,
    long CacheCreationTokens,
    long CacheReadTokens,
    long OutputTokens,
    long RequestCount)
{
    public long TotalTokens => InputTokens + CacheCreationTokens + CacheReadTokens + OutputTokens;
}
