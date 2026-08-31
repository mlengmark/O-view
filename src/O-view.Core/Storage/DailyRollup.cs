using OView.Core.Pricing;

namespace OView.Core.Storage;

/// <summary>
/// Aggregated usage for one (date × model × modifiers) — the grain ADR-0006 serves.
///
/// <para><b><see cref="Date"/> is a local calendar date</b>, not the <c>utc_date</c> the row
/// was stored under. The column records what UTC day a request landed on; the panel reports
/// what day the <i>user</i> had, and one local day straddles two UTC ones, so the bucket is
/// computed from <c>last_timestamp</c> at query time (issue #211). Named without a suffix
/// deliberately: it was <c>DateUtc</c>, and a field carrying the wrong timezone in its own
/// name is how the mislabelled tile happened in the first place.</para>
///
/// <para><b><see cref="Modifiers"/> is part of the key, not a summary of it.</b> Fast mode and
/// US-pinned inference are priced differently from standard, so two requests to one model on
/// one day that differ in either cannot share a bucket — summing them would price the lot at
/// whichever modifier happened to be written down (issue #257). Every record measured here is
/// standard on both, so in practice this leaves one bucket per model, exactly as before.</para>
/// </summary>
public sealed record DailyRollup(
    DateOnly Date,
    string Model,
    TokenSplit Tokens,
    UsageModifiers Modifiers,
    long RequestCount)
{
    public long TotalTokens => Tokens.Total;
}
