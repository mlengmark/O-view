using System.Globalization;
using System.Text;
using OView.Core.Models;

namespace OView.Core.Providers.Jsonl;

/// <summary>What the transcript scan found, at the level the UI has to distinguish.</summary>
public enum TranscriptScopeStatus
{
    /// <summary>
    /// No transcript files in any location checked. A user whose Claude usage is all chat
    /// is the normal case here, not an error — chat keeps no local usage record at all.
    /// </summary>
    NoTranscripts,

    /// <summary>
    /// Transcript files exist. If the tiles still read zero, the suspect is ingestion, not
    /// the source — which is a different message and a different action for the user.
    /// </summary>
    TranscriptsPresent,
}

/// <summary>
/// One transcript source as it resolved on this machine.
/// </summary>
/// <param name="Label">Which surface writes here — "Claude Code" or "Cowork".</param>
/// <param name="Roots">The directories actually searched, after root resolution.</param>
/// <param name="FileCount">Transcript files found across those roots.</param>
/// <param name="TotalBytes">Their combined size. Fat files beside a zero total mean ingestion, not absence.</param>
/// <param name="NewestWriteUtc">Last write across them, or null when there are none.</param>
public sealed record TranscriptSource(
    string Label,
    IReadOnlyList<string> Roots,
    int FileCount,
    long TotalBytes,
    DateTimeOffset? NewestWriteUtc);

/// <summary>
/// Where O-view's local token counts actually came from on this machine, and what it found
/// there — the JSONL counterpart to <see cref="PlanHistory.PlanHistoryReport"/>, and built
/// for the same reason: so the panel can state an observation instead of an assumption.
///
/// <para>The note it replaces was a string literal naming one hard-coded path
/// (<c>%USERPROFILE%\.claude\projects</c>) and one source (Claude Code). Both had been
/// wrong since issue #44: ingestion also reads Cowork audit logs, across every root
/// <see cref="ClaudeDataRoots"/> resolves, which on an MSIX-packaged Claude Desktop is not
/// the canonical path at all. A Cowork user was therefore told the source of their usage
/// was not being read while it was being read and counted (GitHub issue #58).</para>
///
/// <para>Hard-coding that path in user-facing text is the same mistake
/// <see cref="ClaudeDataRoots"/> exists to prevent, and worse there than in code: a user
/// shown a path that is not the one being searched has been actively pointed somewhere
/// unhelpful. Everything here is resolved, never assumed.</para>
///
/// <para>Contains paths and file sizes only — no conversation content, no token counts,
/// nothing identifying beyond the profile directory names already in any Windows path.</para>
/// </summary>
public sealed record TranscriptScopeReport(IReadOnlyList<TranscriptSource> Sources)
{
    public int TotalFiles => Sources.Sum(s => s.FileCount);

    public long TotalBytes => Sources.Sum(s => s.TotalBytes);

    /// <summary>Every directory searched, across all sources — the evidence behind a "found none".</summary>
    public IReadOnlyList<string> SearchedRoots => Sources.SelectMany(s => s.Roots).ToList();

    /// <summary>
    /// Labels of the surfaces that actually have transcripts here, for copy that names the
    /// user's own sources rather than the full list O-view knows how to read. Naming a
    /// surface a user does not use is the mistake issue #58 was about, pointed the other way.
    /// </summary>
    public IReadOnlyList<string> PresentSources =>
        Sources.Where(s => s.FileCount > 0).Select(s => s.Label).ToList();

    public DateTimeOffset? NewestWriteUtc =>
        Sources.Select(s => s.NewestWriteUtc).OfType<DateTimeOffset>() is { } times && times.Any()
            ? times.Max()
            : null;

    public TranscriptScopeStatus Status =>
        TotalFiles == 0 ? TranscriptScopeStatus.NoTranscripts : TranscriptScopeStatus.TranscriptsPresent;

    /// <summary>
    /// The panel's note when the token tiles read zero while the plan meters show real
    /// usage. States what was checked and what was found, and names the surfaces rather
    /// than a path — the exact locations belong in Copy diagnostics, which this points at.
    ///
    /// <para>It deliberately does not blame "the Claude Desktop app". Claude Code sessions
    /// hosted inside Desktop write to the ordinary user-profile location and ARE counted;
    /// chat is the surface that genuinely cannot be measured, because it keeps no local
    /// usage record (CLAUDE.md rule 9).</para>
    /// </summary>
    public string Explain() => Status switch
    {
        TranscriptScopeStatus.NoTranscripts =>
            $"No local session transcripts found. Token counts and Est. value are read from "
            + $"Claude Code and Cowork sessions on this machine, and O-view checked "
            + $"{SearchedRoots.Count} location(s) without finding any. Chat conversations keep "
            + "no local usage record, so they cannot be counted here. The session and weekly "
            + "bars above come from Claude Desktop's own usage file and do cover all of your "
            + $"usage. {DiagnosticsHint.Instruction} to see the exact locations.",

        _ =>
            $"O-view found {TotalFiles} local transcript file(s) ({FormatBytes(TotalBytes)}) but "
            + "recorded no usage from them. That is unexpected — the session and weekly bars "
            + "above are unaffected, but the token figures may be incomplete. "
            + $"{DiagnosticsHint.Instruction} and report this.",
    };

    /// <summary>
    /// The transcript section of the support bundle. Size and recency matter as much as the
    /// count: several empty or long-stale files produce a legitimate zero, whereas fat,
    /// freshly written files beside a zero total mean ingestion is broken. A bare count
    /// cannot tell those apart.
    /// </summary>
    public string ToClipboardText()
    {
        var text = new StringBuilder();
        text.AppendLine($"  transcripts   : {TotalFiles} file(s), {TotalBytes:N0} bytes total");
        text.AppendLine($"  newest        : {(NewestWriteUtc is { } at ? $"{(DateTimeOffset.UtcNow - at).TotalHours:0.0} h old" : "n/a")}");

        foreach (var source in Sources)
        {
            text.AppendLine($"  {source.Label,-13}: {source.FileCount} file(s), {source.TotalBytes:N0} bytes");
            foreach (var root in source.Roots)
            {
                text.AppendLine($"    root        : {root}{(Directory.Exists(root) ? "  <-- exists" : "")}");
            }
        }

        return text.ToString();
    }

    /// <summary>Inspects the real machine layout — every root ingestion actually reads.</summary>
    public static TranscriptScopeReport Inspect() =>
        Inspect(ClaudeProjectsLocator.DefaultRoot, CoworkAuditLocator.DefaultRoots);

    /// <summary>
    /// Overload taking explicit roots so the report can be tested against a synthetic
    /// layout. Mirrors <see cref="JsonlUsageProvider"/>'s own constructor: a null projects
    /// root and an empty Cowork list each skip that source outright, and neither falls back
    /// to a machine default.
    /// </summary>
    public static TranscriptScopeReport Inspect(string? projectsRoot, IReadOnlyList<string> coworkRoots)
    {
        var projectFiles = projectsRoot is null
            ? []
            : ClaudeProjectsLocator.FindTranscripts(projectsRoot);

        // Distinct by path, exactly as ingestion does: MSIX redirection can expose the same
        // session through two roots, and counting it twice would overstate the evidence.
        // Must use the same rule ingestion uses, or the banner and the scan disagree about
        // how many files there are — which is the drift issue #58 was about.
        var coworkFiles = CoworkAuditLocator
            .FindAuditLogs(coworkRoots)
            .Distinct(PathIdentity.Comparer)
            .ToList();

        return new TranscriptScopeReport(
        [
            Measure("Claude Code", projectsRoot is null ? [] : [projectsRoot], projectFiles),
            Measure("Cowork", coworkRoots, coworkFiles),
        ]);
    }

    private static TranscriptSource Measure(
        string label, IReadOnlyList<string> roots, IReadOnlyList<string> files)
    {
        long bytes = 0;
        DateTimeOffset? newest = null;

        foreach (var file in files)
        {
            try
            {
                var info = new FileInfo(file);
                bytes += info.Length;
                var written = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
                if (newest is null || written > newest)
                {
                    newest = written;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Per-file stat is diagnostic only; the file still counts as present.
            }
        }

        return new TranscriptSource(label, roots, files.Count, bytes, newest);
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):0.0} MB"),
        >= 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.0} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes} bytes"),
    };
}
