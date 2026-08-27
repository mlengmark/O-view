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
    private readonly CoworkSessionIndex _sessions;

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
        : this(store, ClaudeProjectsLocator.DefaultRoot, CoworkAuditLocator.DefaultRoots,
            CoworkSessionReport.DefaultRoots)
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
    /// <param name="sessionRegistryRoots">
    /// Cowork's <c>claude-code-sessions</c> directories, used to tell which transcripts under
    /// the Claude Code root Cowork actually wrote (<see cref="CoworkSessionIndex"/>).
    ///
    /// <para>Empty — the default — reclassifies nothing, so every existing caller keeps the
    /// behaviour it had. Stated rather than defaulted to a machine path for the same reason as
    /// the roots above: a test that silently picked up this developer's own registry would be
    /// measuring his sessions, not its fixture.</para>
    /// </param>
    public JsonlUsageProvider(
        RollupStore store,
        string? projectsRoot,
        IReadOnlyList<string> coworkRoots,
        IReadOnlyList<string>? sessionRegistryRoots = null)
    {
        _store = store;
        _projectsRoot = projectsRoot;
        _coworkRoots = coworkRoots;
        _sessions = new CoworkSessionIndex(sessionRegistryRoots ?? []);
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

        // The surface is decided by Cowork's own register, not by which locator found the file.
        //
        // Labelling by location was true when it was written and is not true now: Cowork runs
        // its sessions through Claude Code, so its transcripts land under the Claude Code root.
        // Measured on the development machine, 28 of 30 files there — 107.7 MB of 107.9 MB —
        // belong to registered Cowork sessions, while the bundle reported "Cowork: 0 rows"
        // (issue #218). A registration names the id its transcript is written under, which is
        // exact and survives Claude Code moving the file again.
        //
        // An audit log is still Cowork by construction: nothing else writes one.
        return transcripts
            .Select(p => new Transcript(
                _sessions.Wrote(p) ? TranscriptSources.Cowork : TranscriptSources.ClaudeCode, p))
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
        var staleStat = 0;

        // Per-source tallies for the log line. A single total cannot answer the question the
        // log is read for: 98.5% of one machine's transcript bytes were Cowork, and "9 records
        // ingested" was equally consistent with that working and with only the Claude Code
        // slice being counted (issue #218).
        var perSource = TranscriptSources.All.ToDictionary(s => s, _ => new SourceTally(),
            StringComparer.Ordinal);

        // Before the walk, because the walk asks it which surface wrote each transcript. Only
        // registrations that have changed are re-read, so the steady-state cost is one small
        // file rather than the whole register.
        _sessions.Refresh();

        foreach (var (source, file) in FindAllTranscripts())
        {
            seen++;
            var tally = perSource[source];
            tally.Files++;

            // From an open handle, never from the cached directory entry. Windows documents
            // GetFileAttributesEx — which FileInfo.Length uses — as not necessarily current for
            // a file that is open and being written, and every transcript here is exactly that.
            // A stale entry makes a growing file look untouched, so the "unchanged" test below
            // skips it on every poll for as long as the session holds it open: no error, no
            // records, and a token tile frozen while the user is working (issue #218).
            if (TranscriptReader.CurrentLength(file) is not { } length)
            {
                unreadable++;
                tally.Unreadable++;
                continue;
            }

            // What the directory entry claimed, kept only to count how often it was wrong. This
            // is the measurement that turns "the tiles stopped" into a named cause on a machine
            // nobody can attach a debugger to.
            if (StatLength(file) is { } stat && stat != length)
            {
                staleStat++;
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
            + $"{ingested} record(s) ingested{(unreadable > 0 ? $", {unreadable} unreadable" : "")}"
            // Printed only when it happens, because on a healthy machine it never does — and a
            // zero every minute would train the reader to skip the line that matters.
            + $"{(staleStat > 0 ? $", {staleStat} stale stat(s)" : "")} "
            + $"in {Environment.TickCount64 - startedAt} ms"
            + $" [{string.Join(", ", TranscriptSources.All.Select(s => perSource[s].Describe(s)))}]");
    }

    /// <summary>
    /// The length the cached directory entry claims. Diagnostic only — it is the value that
    /// cannot be trusted, kept solely so disagreements with the handle can be counted.
    /// </summary>
    private static long? StatLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
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
