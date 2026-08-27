namespace OView.Core.Providers.Jsonl;

/// <summary>
/// Which transcripts Cowork wrote, according to Cowork (GitHub issue #218).
///
/// <para><b>The problem this exists to fix.</b> Ingestion labelled a record by <i>which locator
/// found the file</i> — anything under <c>~/.claude/projects</c> was "Claude Code", anything
/// named <c>audit.jsonl</c> was "Cowork". That was true when it was written and is not true now:
/// Cowork runs its sessions through Claude Code and its transcripts land in the Claude Code
/// location. Measured on the development machine, <b>28 of 30 transcripts there — 107.7 MB of
/// 107.9 MB — belong to registered Cowork sessions</b>, and the support bundle reported
/// <c>Cowork: 0 rows</c> on a machine where Cowork was essentially the only source. A label
/// derived from a path is a statement about where a file is, dressed up as a statement about
/// what wrote it.</para>
///
/// <para><b>The authority is Cowork's own register.</b> Each <c>local_*.json</c> under
/// <c>claude-code-sessions</c> names the <c>cliSessionId</c> its session writes under, and that
/// id is the transcript's file name. Matching on it is exact, needs no knowledge of how a
/// working directory is encoded into a folder name, and keeps working wherever Claude Code
/// decides to put the file next.</para>
///
/// <para><b>Cached against file identity, because this runs on every poll.</b> Registrations are
/// hundreds of kilobytes each and a machine can hold dozens; re-parsing all of them once a
/// minute to learn a set of ids that rarely changes would be the largest recurring cost in the
/// poll. Each file is re-read only when its size or write time moves, which in practice is the
/// one session currently being used.</para>
///
/// <para><b>It relabels nothing already stored.</b> A row carries the source it was ingested
/// under, and transcripts already consumed are never re-read, so history keeps whatever label it
/// was given. The breakdown corrects itself as new records arrive rather than retroactively —
/// which is the honest behaviour, since rewriting the past would require re-reading every
/// transcript to establish what it should have said.</para>
/// </summary>
public sealed class CoworkSessionIndex
{
    /// <summary>Directory ceiling for the registry walk. The tree is small; this is a backstop.</summary>
    private const int MaxDirectories = 2_000;

    private readonly IReadOnlyList<string> _roots;

    /// <summary>
    /// One entry per registration file, keyed by path. The size and timestamp are what make a
    /// re-read unnecessary; the id is what the whole class is for. A null id is cached too, so a
    /// file that is not a registration is not re-parsed every minute.
    /// </summary>
    private readonly Dictionary<string, (long Length, DateTime WrittenUtc, string? SessionId)> _cache =
        new(PathIdentity.Comparer);

    private HashSet<string> _ids = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="roots">
    /// The <c>claude-code-sessions</c> directories to read. An empty list indexes nothing and
    /// reclassifies nothing — stated rather than defaulted, so a caller that does not name a
    /// registry cannot silently pick up this developer's own (the hazard of issue #212).
    /// </param>
    public CoworkSessionIndex(IReadOnlyList<string> roots) => _roots = roots;

    /// <summary>The real machine's registry roots, one per Claude data root.</summary>
    public static CoworkSessionIndex ForThisMachine() => new(CoworkSessionReport.DefaultRoots);

    /// <summary>
    /// Session ids Cowork has registered — that is, the file names its transcripts are written
    /// under. Case-insensitive, because it is compared against file names.
    /// </summary>
    public IReadOnlySet<string> SessionIds => _ids;

    /// <summary>
    /// Re-reads registrations that have changed since the last call. Never throws: an
    /// unreadable registry costs attribution, not ingestion.
    /// </summary>
    public void Refresh()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in _roots)
        {
            foreach (var file in TranscriptFileScan.FindInfos(root, "local_*.json", MaxDirectories, out _))
            {
                string? id;
                try
                {
                    if (_cache.TryGetValue(file.FullName, out var cached)
                        && cached.Length == file.Length
                        && cached.WrittenUtc == file.LastWriteTimeUtc)
                    {
                        id = cached.SessionId;
                    }
                    else
                    {
                        id = CoworkSessionReport.ReadFields(file.FullName)?.SessionId;
                        _cache[file.FullName] = (file.Length, file.LastWriteTimeUtc, id);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if (id is { Length: > 0 })
                {
                    ids.Add(id);
                }
            }
        }

        _ids = ids;
    }

    /// <summary>Whether this transcript was written by a registered Cowork session.</summary>
    public bool Wrote(string transcriptPath) =>
        _ids.Contains(Path.GetFileNameWithoutExtension(transcriptPath));
}
