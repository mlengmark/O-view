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
    private readonly IReadOnlyList<string> _coworkRoots;

    /// <summary>
    /// Where each poll's ingestion result is recorded. A delegate, so <c>Core</c> stays free
    /// of the app's logging — the same seam <see cref="CompositeUsageProvider.Log"/> uses.
    ///
    /// <para><b>The one line this writes is the one that was missing.</b> Ingestion has three
    /// ways to produce nothing and they need different fixes: no transcript files were found
    /// at all, files were found but none had grown since the last poll, or reading them threw.
    /// From outside, all three are a token tile that does not move. Measured in the field: a
    /// poll completing in 15 ms while 409,501 bytes sat unread, with no way to tell which of
    /// the three it was without attaching a debugger to a user's machine.</para>
    /// </summary>
    public Action<string>? Log { get; init; }

    /// <summary>The real machine layout: every transcript root on this machine.</summary>
    public JsonlUsageProvider(RollupStore store)
        : this(store, ClaudeProjectsLocator.DefaultRoot, CoworkAuditLocator.DefaultRoots)
    {
    }

    /// <summary>
    /// Explicit roots: a null projects root and an empty Cowork list each skip that
    /// source outright, and neither falls back to a machine default. Naming one root
    /// while the other silently resolved to a real directory made a test ingest this
    /// developer's actual Cowork history (it expected 60 tokens and got 3,807), so every
    /// source is stated or absent by choice.
    ///
    /// Cowork takes a list rather than an optional single root because a machine can
    /// have more than one Claude data root (canonical plus MSIX package stores). An
    /// overload pair would have made a bare <c>null</c> argument ambiguous at every call
    /// site, so there is deliberately one constructor here, not two.
    /// </summary>
    public JsonlUsageProvider(RollupStore store, string? projectsRoot, IReadOnlyList<string> coworkRoots)
    {
        _store = store;
        _projectsRoot = projectsRoot;
        _coworkRoots = coworkRoots;
    }

    /// <summary>
    /// Every local transcript, across both places Claude writes them: Claude Code under
    /// %USERPROFILE%\.claude\projects, and Cowork under its sandboxed session root. The
    /// downstream pipeline is keyed by file path and request id, so a second source needs
    /// no other change — offsets, de-duplication and idempotent upserts all still hold,
    /// including for a request id that appears in both (issue #44).
    /// </summary>
    /// <param name="Source">Which surface writes it — a <see cref="TranscriptSources"/> label.</param>
    /// <param name="Path">The file itself.</param>
    private readonly record struct Transcript(string Source, string Path);

    private IEnumerable<Transcript> FindAllTranscripts()
    {
        IEnumerable<string> transcripts = _projectsRoot is null
            ? []
            : ClaudeProjectsLocator.FindTranscripts(_projectsRoot);

        // Distinct by path: the same root can appear twice through MSIX redirection, and
        // re-reading one file per poll is pointless work even though ingestion would
        // de-duplicate its records anyway. Platform path identity, because on a
        // case-sensitive filesystem two names differing only in case are two files.
        IEnumerable<string> audits = CoworkAuditLocator
            .FindAuditLogs(_coworkRoots)
            .Distinct(PathIdentity.Comparer);

        // The label is attached here, where the locator that produced the path is still
        // known, and carried all the way into the store. Deriving it downstream from the
        // path — "does it end in audit.jsonl" — would be a second, guessable answer to a
        // question this loop already knows for certain, and it would go quietly wrong the
        // first time a root moved (issue #218).
        return transcripts.Select(p => new Transcript(TranscriptSources.ClaudeCode, p))
            .Concat(audits.Select(p => new Transcript(TranscriptSources.Cowork, p)));
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
        var startedAt = Environment.TickCount64;
        var seen = 0;
        var changed = 0;
        var unreadable = 0;
        long bytesRead = 0;
        var ingested = 0;

        // Per-source tallies for the log line. A single total cannot answer the question the
        // log is read for: 98.5% of one machine's transcript bytes were Cowork, and "9 records
        // ingested" was equally consistent with that working and with only the Claude Code
        // slice being counted (issue #218).
        var perSource = TranscriptSources.All.ToDictionary(s => s, _ => new SourceTally(),
            StringComparer.Ordinal);

        foreach (var (source, file) in FindAllTranscripts())
        {
            seen++;
            var tally = perSource[source];
            tally.Files++;

            long length;
            try
            {
                length = new FileInfo(file).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unreadable++;
                tally.Unreadable++;
                continue;
            }

            var (offset, knownLength) = _store.GetFileOffset(file);

            // Unchanged since the last poll: nothing to parse. This is the common case
            // and the reason the optimisation pays.
            if (length == knownLength && offset > 0)
            {
                continue;
            }

            changed++;
            tally.Changed++;
            var (records, nextOffset) = TranscriptReader.ReadFrom(file, offset);
            var read = Math.Max(0, nextOffset - offset);
            bytesRead += read;
            tally.BytesRead += read;

            if (records.Count > 0)
            {
                _store.Ingest(records, source);
                ingested += records.Count;
                tally.Records += records.Count;
            }

            // Written whether or not anything was parsed, and that is the case it exists for:
            // a file that advances its watermark while yielding nothing is invisible in every
            // total above, and is the shape a stale watermark leaves behind.
            _store.SetFileOffset(file, nextOffset, length, source, records.Count, offset);
        }

        // One line per poll, always — including the all-quiet case, because "0 changed" is
        // the answer to "is it stuck or is there simply nothing new?" and only says so when
        // it is written down. A throw above never reaches here, which is itself the signal:
        // the composite's own catch names the provider, and the absence of this line dates
        // the failure.
        Log?.Invoke(
            $"jsonl sync: {seen} file(s), {changed} changed, {bytesRead:N0} bytes read, "
            + $"{ingested} record(s) ingested{(unreadable > 0 ? $", {unreadable} unreadable" : "")} "
            + $"in {Environment.TickCount64 - startedAt} ms"
            + $" [{string.Join(", ", TranscriptSources.All.Select(s => perSource[s].Describe(s)))}]");
    }

    /// <summary>One surface's share of a single poll. Mutable by design — it is a counter.</summary>
    private sealed class SourceTally
    {
        public int Files;
        public int Changed;
        public int Unreadable;
        public int Records;
        public long BytesRead;

        /// <summary>
        /// Compact enough that a whole poll still fits on one log line, and complete enough that
        /// "0 records" can be told apart from "0 files". A source with no files at all is the
        /// normal case for anyone who uses only one surface, and says so rather than printing
        /// three zeroes that read like a failure.
        /// </summary>
        public string Describe(string source) => Files == 0
            ? $"{source} none"
            : $"{source} {Files}f/{Changed}ch/{BytesRead:N0}b/{Records}r"
              + (Unreadable > 0 ? $"/{Unreadable}unreadable" : "");
    }
}
