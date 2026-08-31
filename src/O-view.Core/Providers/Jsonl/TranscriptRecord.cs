using OView.Core.Pricing;

namespace OView.Core.Providers.Jsonl;

/// <summary>
/// One validated assistant record from a Claude Code transcript. The same
/// <see cref="RequestId"/> appears multiple times as responses stream (28 records /
/// 12 ids observed) — consumers must keep only the last occurrence per id, or totals
/// overcount ~2.3× (CLAUDE.md rule 4, docs/findings/jsonl-schema.md).
/// </summary>
/// <param name="Tokens">
/// The token counts, split the way the rate card charges them rather than the way
/// <c>usage</c> leads with them — cache writes carry their TTL, because the two TTLs bill at
/// different published prices (GitHub issue #255).
/// </param>
/// <param name="Modifiers">
/// <c>usage.speed</c> and <c>usage.inference_geo</c>, the two published pricing modifiers.
/// Both are inactive on every record measured here; they are read anyway, because a field
/// that appears in a pricing formula is load-bearing whether or not today's data exercises it
/// (issue #257).
/// </param>
public sealed record TranscriptRecord(
    string RequestId,
    DateTimeOffset TimestampUtc,
    string Model,
    TokenSplit Tokens,
    UsageModifiers Modifiers);
