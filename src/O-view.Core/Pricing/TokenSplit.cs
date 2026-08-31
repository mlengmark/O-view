namespace OView.Core.Pricing;

/// <summary>
/// The token counts behind one priced figure, split the way the published rate card charges
/// them — which is finer than the four fields a transcript's <c>usage</c> block leads with.
///
/// <para><b>Cache writes are two prices, not one.</b> Anthropic bills a 5-minute-TTL write at
/// 1.25× the base input rate and a 1-hour-TTL write at 2×, and the transcript records which
/// was used in <c>usage.cache_creation</c>. O-view priced every write at 1.25× — the 5-minute
/// rate — while the transcripts on the development machine were almost entirely 1-hour, so
/// cache-write value was understating by 37.5% of its true amount (GitHub issue #255).</para>
///
/// <para><b><see cref="CacheWriteTtlUnrecorded"/> is the third bucket, and it is a migration
/// artefact rather than a rate.</b> Rows ingested before the split existed carry a cache-write
/// total with no TTL attribution, and it cannot be recovered from the store — the transcripts
/// that would answer are the ones Claude Code has since deleted. Re-ingesting what is still on
/// disk recovers recent history; what is out of that reach lands here, is priced at the
/// 5-minute rate, and the panel says so (<see cref="Models.PanelText.Caveat"/>). Pricing it
/// silently at 1.25× with no bucket and no caveat was rejected outright: that is the original
/// defect with a migration in front of it.</para>
/// </summary>
public readonly record struct TokenSplit(
    long Input,
    long CacheWrite5m,
    long CacheWrite1h,
    long CacheWriteTtlUnrecorded,
    long CacheRead,
    long Output)
{
    public static readonly TokenSplit Empty = default;

    /// <summary>Every cache write, whatever its TTL — the flat <c>cache_creation_input_tokens</c>.</summary>
    public long CacheWrite => CacheWrite5m + CacheWrite1h + CacheWriteTtlUnrecorded;

    /// <summary>Everything billed.</summary>
    public long Total => Input + CacheWrite + CacheRead + Output;

    /// <summary>
    /// Adds two splits bucket by bucket, so a window is summed once rather than field by field
    /// at every call site. The rollup store, the panel's composition and the model breakdown
    /// all aggregate the same six numbers.
    /// </summary>
    public static TokenSplit operator +(TokenSplit a, TokenSplit b) => new(
        a.Input + b.Input,
        a.CacheWrite5m + b.CacheWrite5m,
        a.CacheWrite1h + b.CacheWrite1h,
        a.CacheWriteTtlUnrecorded + b.CacheWriteTtlUnrecorded,
        a.CacheRead + b.CacheRead,
        a.Output + b.Output);
}
