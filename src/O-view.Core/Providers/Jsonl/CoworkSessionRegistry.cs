using System.Globalization;
using System.Text;
using System.Text.Json;

namespace OView.Core.Providers.Jsonl;

/// <summary>
/// One Cowork session as Cowork itself recorded it.
/// </summary>
/// <param name="SessionId">
/// <c>cliSessionId</c> — and, crucially, the <b>file name</b> its transcript is written under.
/// </param>
/// <param name="WorkingDirectory">The session's <c>cwd</c>. Diagnostic only; the lookup is by id.</param>
/// <param name="LastActivityUtc">When Cowork last touched it. Null when the field was absent.</param>
/// <param name="TranscriptPath">Where the transcript was found, or null when there is none.</param>
public sealed record CoworkSession(
    string SessionId,
    string WorkingDirectory,
    DateTimeOffset? LastActivityUtc,
    string? TranscriptPath);

/// <summary>
/// Cowork's own register of its sessions, checked against the transcripts O-view can find
/// (GitHub issue #218).
///
/// <para><b>Why this is the diagnostic that was missing.</b> Every other transcript field in the
/// bundle answers "what did O-view find", and none of them can answer "what should have been
/// there". A machine actively using Cowork whose newest local transcript is 52 hours old is
/// indistinguishable, in that report, from a machine that simply stopped working — the scan
/// found what it found, reported it honestly, and had no way to know anything was absent. This
/// is the independent expectation to measure against: Cowork writes one
/// <c>local_&lt;id&gt;.json</c> per session under <c>claude-code-sessions</c>, carrying the
/// session's own <c>cliSessionId</c>, its <c>cwd</c> and when it was last active. Every one of
/// those ids should name a transcript.</para>
///
/// <para><b>The lookup is by file name, not by rebuilding the directory.</b> Claude Code encodes
/// a <c>cwd</c> into a directory name (<c>C:\Users\x</c> → <c>C--Users-x</c>) by a rule this
/// project has never had written down and does not control. Reconstructing it would make the
/// check wrong the first time that rule changed — and wrong in the direction that invents a
/// fault, reporting every session as missing on a machine where nothing is. The session id is
/// unique and is the file's own name, so the transcript is looked up by name wherever it
/// sits.</para>
///
/// <para><b>Recency comes from Cowork, not from the filesystem.</b> <c>lastActivityAt</c> is
/// what Cowork wrote; a transcript's mtime is not updated while its handle is held open on
/// Windows, so a live session's file can look hours stale. The finding this reports is
/// therefore "a recent session has no transcript at all", which no timestamp can fake, and the
/// mtime comparison is offered beside it as context rather than as the alarm.</para>
///
/// <para>Ids, paths and timestamps only — no titles and no conversation content, which these
/// files do carry. <see cref="Providers.PlanHistory.PlanHistoryReport"/> and the transcript
/// scope report hold the same line.</para>
/// </summary>
public sealed record CoworkSessionReport(
    IReadOnlyList<string> Roots,
    int Registered,
    IReadOnlyList<CoworkSession> Sessions,
    string? Failure = null)
{
    /// <summary>Nothing was inspected — the roots do not exist, or none were given.</summary>
    public static CoworkSessionReport None { get; } = new([], 0, []);

    /// <summary>
    /// How many session registrations are read in full.
    ///
    /// <para>These files run to hundreds of kilobytes each and this report is built on the UI
    /// thread by Copy diagnostics, so the newest few are read rather than all of them. The
    /// total is still counted and printed, so a bound is never mistaken for the whole
    /// picture.</para>
    /// </summary>
    public const int InspectLimit = 25;

    /// <summary>How recent a session must be for a missing transcript to be a live fault.</summary>
    public static readonly TimeSpan RecentActivity = TimeSpan.FromHours(6);

    public int Resolved => Sessions.Count(s => s.TranscriptPath is not null);

    public int Unresolved => Sessions.Count(s => s.TranscriptPath is null);

    public DateTimeOffset? NewestActivityUtc =>
        Sessions.Select(s => s.LastActivityUtc).OfType<DateTimeOffset>() is { } times && times.Any()
            ? times.Max()
            : null;

    /// <summary>
    /// Sessions Cowork touched recently that have no transcript on disk.
    ///
    /// <para><b>This is the finding.</b> An old session without one is ordinary — Claude Code
    /// deletes transcripts after ~30 days while the registration survives — but a session
    /// active in the last few hours with nothing written for it means the machine is producing
    /// usage that nothing local is recording, which is precisely the state that reads as "the
    /// token tiles have stopped".</para>
    /// </summary>
    public IReadOnlyList<CoworkSession> RecentWithoutTranscript(DateTimeOffset utcNow) =>
        Sessions
            .Where(s => s.TranscriptPath is null
                        && s.LastActivityUtc is { } at
                        && utcNow - at <= RecentActivity)
            .OrderByDescending(s => s.LastActivityUtc)
            .ToList();

    public string ToClipboardText(DateTimeOffset utcNow)
    {
        var text = new StringBuilder();

        if (Failure is { Length: > 0 } failure)
        {
            text.AppendLine($"  Cowork sessions: unreadable ({failure})");
            return text.ToString();
        }

        if (Registered == 0)
        {
            // Printed rather than omitted. A machine that has never opened Cowork is the normal
            // case, and saying so distinguishes it from a section that failed to render.
            text.AppendLine("  Cowork sessions: none registered");
            foreach (var root in Roots)
            {
                text.AppendLine($"    root        : {root}{(Directory.Exists(root) ? "  <-- exists" : "")}");
            }

            return text.ToString();
        }

        var bound = Registered > Sessions.Count ? $" ({Sessions.Count} newest read)" : "";
        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"  Cowork sessions: {Registered} registered{bound}, newest activity {Age(NewestActivityUtc, utcNow)}"));

        foreach (var root in Roots)
        {
            text.AppendLine($"    root        : {root}{(Directory.Exists(root) ? "  <-- exists" : "")}");
        }

        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"    transcripts : {Resolved} resolved, {Unresolved} with no transcript file"));

        var recent = RecentWithoutTranscript(utcNow);
        if (recent.Count == 0)
        {
            return text.ToString();
        }

        // The alarm, and the one line in this bundle that says something is wrong rather than
        // reporting a measurement. An old registration without a transcript is ordinary; a
        // session active within hours without one is not.
        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"    !! {recent.Count} session(s) active in the last {RecentActivity.TotalHours:0} h have NO transcript"));

        foreach (var session in recent.Take(5))
        {
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"       {session.SessionId} · {Age(session.LastActivityUtc, utcNow)} · {session.WorkingDirectory}"));
        }

        return text.ToString();
    }

    private static string Age(DateTimeOffset? at, DateTimeOffset utcNow) => at is { } when
        ? string.Create(CultureInfo.InvariantCulture, $"{(utcNow - when).TotalHours:0.0} h old")
        : "unknown";

    /// <summary>Inspects the real machine layout.</summary>
    public static CoworkSessionReport Inspect() =>
        Inspect(DefaultRoots, ClaudeProjectsLocator.DefaultRoot, DateTimeOffset.UtcNow);

    /// <summary>Directory Cowork registers sessions in, relative to a Claude data root.</summary>
    public const string SessionsDirectoryName = "claude-code-sessions";

    /// <summary>One per Claude data root, so a packaged (MSIX) Desktop install is covered too.</summary>
    public static IReadOnlyList<string> DefaultRoots =>
        ClaudeDataRoots.All().Select(r => Path.Combine(r, SessionsDirectoryName)).ToList();

    /// <summary>
    /// Overload taking explicit roots so the report can be tested against a synthetic layout.
    /// A null projects root resolves nothing and reports every session as unresolved, which is
    /// why it is stated rather than defaulted — the same rule
    /// <see cref="TranscriptScopeReport.Inspect(string?, IReadOnlyList{string})"/> follows.
    /// </summary>
    public static CoworkSessionReport Inspect(
        IReadOnlyList<string> roots, string? projectsRoot, DateTimeOffset utcNow)
    {
        try
        {
            var files = roots
                .SelectMany(r => TranscriptFileScan.Find(r, "local_*.json"))
                .Distinct(PathIdentity.Comparer)
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();

            if (files.Count == 0)
            {
                return new CoworkSessionReport(roots, 0, []);
            }

            // Every transcript on disk, keyed by the session id that names it. One walk,
            // then a lookup per session — the alternative re-walks the whole projects tree
            // for each registration.
            var transcripts = (projectsRoot is null
                    ? []
                    : ClaudeProjectsLocator.FindTranscripts(projectsRoot))
                .GroupBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key!, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var sessions = files
                .Take(InspectLimit)
                .Select(f => Read(f.FullName, transcripts))
                .OfType<CoworkSession>()
                .OrderByDescending(s => s.LastActivityUtc)
                .ToList();

            return new CoworkSessionReport(roots, files.Count, sessions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new CoworkSessionReport(roots, 0, [], $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Pulls the three fields that matter out of one registration.
    ///
    /// <para>Read with <see cref="Utf8JsonReader"/> and abandoned as soon as they are all in
    /// hand: these files are hundreds of kilobytes of session state, almost none of it wanted,
    /// and building a document for three properties would be the largest allocation in the
    /// bundle by a wide margin. Reading only what is needed also means the conversation content
    /// in the rest of the file is never materialised at all.</para>
    /// </summary>
    private static CoworkSession? Read(string path, IReadOnlyDictionary<string, string> transcripts)
    {
        try
        {
            var reader = new Utf8JsonReader(File.ReadAllBytes(path));

            string? sessionId = null;
            string? cwd = null;
            DateTimeOffset? lastActivity = null;

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString();
                reader.Read();

                switch (name)
                {
                    case "cliSessionId" when reader.TokenType == JsonTokenType.String:
                        sessionId = reader.GetString();
                        break;
                    case "cwd" when reader.TokenType == JsonTokenType.String:
                        cwd = reader.GetString();
                        break;
                    case "lastActivityAt" when reader.TokenType == JsonTokenType.Number
                                               && reader.TryGetInt64(out var ms):
                        lastActivity = DateTimeOffset.FromUnixTimeMilliseconds(ms);
                        break;
                    default:
                        reader.Skip();
                        break;
                }

                if (sessionId is not null && cwd is not null && lastActivity is not null)
                {
                    break;
                }
            }

            return sessionId is { Length: > 0 } id
                ? new CoworkSession(id, cwd ?? "unknown", lastActivity, transcripts.GetValueOrDefault(id))
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // One unreadable registration is not a failed report.
            return null;
        }
    }
}
