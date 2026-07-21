using OView.Core.Models;
using OView.Core.Providers;
using OView.Core.Storage;

namespace OView.Core.Providers.Jsonl;

/// <summary>
/// Fallback provider (ADR-0002): scans local transcripts, feeds the rollup store, and
/// reports what it honestly can. Session and weekly percentages are null — the plan's
/// token allowance is unpublished, so no true percentage-of-limit can be derived from
/// token counts, and fabricating a denominator would violate CLAUDE.md rule 6. Token
/// figures for the stats tiles come from the <see cref="RollupStore"/> directly.
/// </summary>
public sealed class JsonlUsageProvider : IUsageProvider
{
    private readonly RollupStore _store;
    private readonly string _projectsRoot;

    public JsonlUsageProvider(RollupStore store, string? projectsRoot = null)
    {
        _store = store;
        _projectsRoot = projectsRoot ?? ClaudeProjectsLocator.DefaultRoot;
    }

    public UsageSnapshot GetSnapshot(DateTimeOffset utcNow)
    {
        // Sync-then-report: each poll re-feeds the store. The store's upsert-by-
        // request_id makes re-ingestion idempotent, so a full rescan is safe.
        foreach (var file in ClaudeProjectsLocator.FindTranscripts(_projectsRoot))
        {
            _store.Ingest(TranscriptReader.ReadFile(file));
        }

        return _store.LatestActivityUtc() is { } latest
            ? new UsageSnapshot(DataSource.Estimate, null, null, null, latest)
            : UsageSnapshot.None;
    }
}
