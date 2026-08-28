using OView.Core.Providers.Jsonl;

namespace OView.Core.Providers.CachedUsage;

/// <summary>
/// Detects whether an invocation of Claude Code reached the model and was therefore billed.
///
/// <para>Two calls, in order: <see cref="Snapshot"/> before the process starts, then
/// <see cref="FindBilled"/> after it ends. Identity, not timestamps, is what separates this
/// invocation's transcript from everything else on the machine — see
/// <see cref="TranscriptCostGuard"/> for why that distinction is the whole point.</para>
/// </summary>
public interface IBilledTranscriptGuard
{
    /// <summary>Transcripts that already existed. Must be taken before the process starts.</summary>
    IReadOnlySet<string> Snapshot();

    /// <summary>
    /// The first transcript absent from <paramref name="before"/> that carries a request id, or
    /// null when none does.
    /// </summary>
    string? FindBilled(IReadOnlySet<string> before);
}

/// <summary>
/// The real guard: a transcript that did not exist before the invocation and carries a request id.
///
/// <para><b>Why identity rather than a timestamp.</b> The first version of this filtered on "created
/// or written since a watermark", which looked obviously correct and was not. A Claude Code session
/// that is <i>already running</i> writes its transcript continuously, so its file matches that
/// filter and carries request ids on every line — measured on the development machine, an active
/// session's transcript created at 12:23 and still being written at 15:41. The guard would have
/// reported a charge on the first refresh and permanently disabled the feature, on precisely the
/// machines it exists for: people who run Claude Code and have no Claude Desktop. No unit test
/// caught it, because every one of them injected a fake guard.</para>
///
/// <para><b>Every <c>claude -p</c> invocation writes a transcript, including the free one</b>, so
/// a new file proves nothing on its own. What separates them is the content: a locally handled
/// slash command produces <c>queue-operation</c>, <c>user</c>, <c>system</c> and <c>last-prompt</c>
/// records only, while anything that reached the model carries a request id and a usage record.
/// Measured both ways on 2026-08-28 —
/// [findings/cli-usage-refresh.md](../../../../docs/findings/cli-usage-refresh.md).</para>
///
/// <para><b>One race is accepted and not papered over.</b> A user who starts an unrelated Claude
/// Code session during the seconds this runs produces a second new transcript, which carries
/// request ids and is indistinguishable from a billed refresh. That reports a charge when none
/// was made. The direction is deliberate: a false charge stops a feature that can be turned back
/// on, and a missed charge leaks roughly 50K tokens per poll silently. The latch this feeds must
/// therefore be resettable — a permanent, unexplained disable would make this trade the wrong
/// way round.</para>
/// </summary>
public sealed class TranscriptCostGuard : IBilledTranscriptGuard
{
    private readonly Func<IReadOnlyList<FileInfo>> _list;
    private readonly Func<string, bool> _carriesRequestId;

    /// <summary>Production wiring: Claude Code's real transcript tree.</summary>
    public TranscriptCostGuard()
        : this(ClaudeProjectsLocator.DefaultRoot)
    {
    }

    /// <param name="root">Transcript tree to watch. Testable without a real profile.</param>
    public TranscriptCostGuard(string root)
        // TranscriptFileScan rather than a hand-rolled walk: this tree has contained a broken
        // junction that aborts a recursive enumeration, turning one bad folder into "no files"
        // (CLAUDE.md rule 9). Here that would read as "nothing was billed" — the one answer this
        // must never give wrongly.
        : this(() => TranscriptFileScan.FindInfos(root, "*.jsonl", int.MaxValue, out _),
               CarriesRequestId)
    {
    }

    /// <param name="list">Lists candidate transcripts.</param>
    /// <param name="carriesRequestId">Decides whether one was billed.</param>
    public TranscriptCostGuard(
        Func<IReadOnlyList<FileInfo>> list, Func<string, bool> carriesRequestId)
    {
        _list = list;
        _carriesRequestId = carriesRequestId;
    }

    /// <summary>
    /// Full paths of the transcripts present now.
    ///
    /// <para>Paths rather than names: two projects can hold transcripts with the same file name,
    /// and a name-keyed set would treat a genuinely new file as pre-existing — a missed charge,
    /// which is the direction that costs money.</para>
    /// </summary>
    public IReadOnlySet<string> Snapshot() =>
        _list().Select(f => f.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public string? FindBilled(IReadOnlySet<string> before)
    {
        foreach (var file in _list())
        {
            if (before.Contains(file.FullName))
            {
                continue;
            }

            if (_carriesRequestId(file.FullName))
            {
                return file.Name;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a transcript contains a request id, read line by line so a large transcript from
    /// an unrelated session cannot pull megabytes into memory behind a poll.
    ///
    /// <para>Both spellings, for the reason CLAUDE.md rule 4 gives: Claude Code writes
    /// <c>requestId</c> and Cowork writes <c>request_id</c> on an otherwise identical record, and
    /// checking one is how a whole source goes unseen.</para>
    ///
    /// <para>An unreadable file is <b>not</b> silently treated as unbilled — it throws, and
    /// <c>ClaudeCliRefresher</c> reports a throwing guard as a charge. A single unreadable
    /// transcript is weak evidence of a charge, but "assume it was free" is the failure mode this
    /// whole class exists to prevent, and it is not worth being clever about.</para>
    /// </summary>
    public static bool CarriesRequestId(string path)
    {
        using var reader = new StreamReader(path);
        while (reader.ReadLine() is { } line)
        {
            if (line.Contains("\"requestId\"", StringComparison.Ordinal) ||
                line.Contains("\"request_id\"", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
