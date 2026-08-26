namespace OView.Core.Storage;

/// <summary>
/// Aggregated usage for one (date × model) — the grain ADR-0006 serves.
///
/// <para><b><see cref="Date"/> is a local calendar date</b>, not the <c>utc_date</c> the row
/// was stored under. The column records what UTC day a request landed on; the panel reports
/// what day the <i>user</i> had, and one local day straddles two UTC ones, so the bucket is
/// computed from <c>last_timestamp</c> at query time (issue #211). Named without a suffix
/// deliberately: it was <c>DateUtc</c>, and a field carrying the wrong timezone in its own
/// name is how the mislabelled tile happened in the first place.</para>
/// </summary>
public sealed record DailyRollup(
    DateOnly Date,
    string Model,
    long InputTokens,
    long CacheCreationTokens,
    long CacheReadTokens,
    long OutputTokens,
    long RequestCount)
{
    public long TotalTokens => InputTokens + CacheCreationTokens + CacheReadTokens + OutputTokens;
}
