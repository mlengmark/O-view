namespace OView.Core.Providers.Jsonl;

/// <summary>
/// One validated assistant record from a Claude Code transcript. The same
/// <see cref="RequestId"/> appears multiple times as responses stream (28 records /
/// 12 ids observed) — consumers must keep only the last occurrence per id, or totals
/// overcount ~2.3× (CLAUDE.md rule 4, docs/findings/jsonl-schema.md).
/// </summary>
public sealed record TranscriptRecord(
    string RequestId,
    DateTimeOffset TimestampUtc,
    string Model,
    long InputTokens,
    long CacheCreationTokens,
    long CacheReadTokens,
    long OutputTokens);
