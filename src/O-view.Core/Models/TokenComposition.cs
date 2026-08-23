using OView.Core.Storage;

namespace OView.Core.Models;

/// <summary>
/// The four token kinds behind a single tile figure, kept separate so the total can say
/// what it is made of.
///
/// <para><b>Why this exists.</b> A user reported the token tiles as inflated: the panel
/// read <c>235.6M</c> where Claude's own UI showed thousands, and they concluded O-view was
/// over-counting (GitHub issue #169). It is not — the ingestion path de-duplicates on
/// request id and upserts by replacement, and the sum is arithmetically right. The two
/// figures simply answer different questions, and the panel never said which one it was
/// answering.</para>
///
/// <para>Measured on real transcripts: <b>cache reads were 89.2% of a day's total</b> —
/// 398,121 of 446,148 tokens across 7 requests. Every turn re-sends the whole conversation
/// and prompt caching bills that re-send as <c>cache_read_input_tokens</c>, so the
/// cumulative figure outruns any single conversation's size by orders of magnitude. The
/// number a user compares it against is context-window occupancy: one conversation, at one
/// moment, bounded by the model's context limit and therefore always in the thousands.</para>
///
/// <para><b>Cache reads stay in the total.</b> They are billed, and <see cref="Pricing"/>
/// already prices them at the 0.10× multiplier, so excluding them would understate real
/// consumption and break the Est. value tiles. The defect was the label, not the sum —
/// which is rule 6 in its less obvious form: a number can be correct and still mislead if
/// nothing says what it counts.</para>
/// </summary>
public sealed record TokenComposition(long Input, long CacheCreation, long CacheRead, long Output)
{
    public static readonly TokenComposition Empty = new(0, 0, 0, 0);

    /// <summary>
    /// The figure the tiles show. Identical by construction to
    /// <see cref="DailyRollup.TotalTokens"/> — the point of this type is to explain that
    /// total, never to compute a different one.
    /// </summary>
    public long Total => Input + CacheCreation + CacheRead + Output;

    /// <summary>
    /// The total without cached prompt re-reads — the figure closest to a user's intuition
    /// of "what I actually used". Shown alongside the total, never instead of it: it is the
    /// smaller and more flattering number, and presenting it as the headline would be the
    /// same error in the opposite direction.
    /// </summary>
    public long ExcludingCacheReads => Input + CacheCreation + Output;

    /// <summary>Cache reads as a fraction of the total; zero when there is nothing to divide.</summary>
    public double CacheReadShare => Total == 0 ? 0 : (double)CacheRead / Total;

    /// <summary>Whether there is anything to break down. A composition of nothing explains nothing.</summary>
    public bool HasTokens => Total > 0;

    public static TokenComposition From(IEnumerable<DailyRollup> rollups)
    {
        long input = 0, cacheCreation = 0, cacheRead = 0, output = 0;

        foreach (var r in rollups)
        {
            input += r.InputTokens;
            cacheCreation += r.CacheCreationTokens;
            cacheRead += r.CacheReadTokens;
            output += r.OutputTokens;
        }

        return new TokenComposition(input, cacheCreation, cacheRead, output);
    }
}
