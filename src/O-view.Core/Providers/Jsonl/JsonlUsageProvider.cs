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
///
/// Scans **both** local transcript sources — Claude Code and Cowork. Chat remains out of
/// reach at any price: claude.ai persists conversation content locally but no usage
/// accounting at all, so there is nothing to read (issue #44).
/// </summary>
public sealed class JsonlUsageProvider : IUsageProvider
{
    private readonly RollupStore _store;
    private readonly string? _projectsRoot;
    private readonly string? _coworkRoot;

    /// <summary>The real machine layout: both transcript roots.</summary>
    public JsonlUsageProvider(RollupStore store)
        : this(store, ClaudeProjectsLocator.DefaultRoot, CoworkAuditLocator.DefaultRoot)
    {
    }

    /// <summary>
    /// Explicit roots. A null root skips that source outright and never falls back to the
    /// machine default — naming one root while the other silently resolved to a real
    /// directory made a test ingest this developer's actual Cowork history (it expected
    /// 60 tokens and got 3,807). Both roots are stated or neither is.
    /// </summary>
    public JsonlUsageProvider(RollupStore store, string? projectsRoot, string? coworkRoot)
    {
        _store = store;
        _projectsRoot = projectsRoot;
        _coworkRoot = coworkRoot;
    }

    /// <summary>
    /// Every local transcript, across both places Claude writes them: Claude Code under
    /// %USERPROFILE%\.claude\projects, and Cowork under its sandboxed session root. The
    /// downstream pipeline is keyed by file path and request id, so a second source needs
    /// no other change — offsets, de-duplication and idempotent upserts all still hold,
    /// including for a request id that appears in both (issue #44).
    /// </summary>
    private IEnumerable<string> FindAllTranscripts()
    {
        IEnumerable<string> transcripts = _projectsRoot is null
            ? []
            : ClaudeProjectsLocator.FindTranscripts(_projectsRoot);

        IEnumerable<string> audits = _coworkRoot is null
            ? []
            : CoworkAuditLocator.FindAuditLogs(_coworkRoot);

        return transcripts.Concat(audits);
    }

    public UsageSnapshot GetSnapshot(DateTimeOffset utcNow)
    {
        Sync();

        return _store.LatestActivityUtc() is { } latest
            ? new UsageSnapshot(DataSource.Estimate, null, null, null, latest)
            : UsageSnapshot.None;
    }

    /// <summary>
    /// Feeds new transcript content into the store, reading only what each file has
    /// gained since the last poll. Idempotent upserts still make a full re-read safe,
    /// so offsets are purely an optimisation — but a necessary one: transcripts grew
    /// 0.2 MB to 6.7 MB in a single day of use, and a full rescan every 60 s scales
    /// with total history rather than with new activity.
    /// </summary>
    private void Sync()
    {
        foreach (var file in FindAllTranscripts())
        {
            long length;
            try
            {
                length = new FileInfo(file).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var (offset, knownLength) = _store.GetFileOffset(file);

            // Unchanged since the last poll: nothing to parse. This is the common case
            // and the reason the optimisation pays.
            if (length == knownLength && offset > 0)
            {
                continue;
            }

            var (records, nextOffset) = TranscriptReader.ReadFrom(file, offset);
            if (records.Count > 0)
            {
                _store.Ingest(records);
            }

            _store.SetFileOffset(file, nextOffset, length);
        }
    }
}
