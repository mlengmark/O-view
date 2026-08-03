using System.Text;
using System.Text.Json;
using OView.Core.Models;

namespace OView.Core.Providers.PlanHistory;

/// <summary>Why the plan-history file is or isn't yielding usable samples.</summary>
public enum PlanDataStatus
{
    /// <summary>The file is not where we look — Claude Desktop absent, or writing elsewhere.</summary>
    FileMissing,

    /// <summary>Present but unreadable — locked, or not JSON at all.</summary>
    Unreadable,

    /// <summary>Parsed, but no sample matched the expected shape (t / org / u.fh / u.sd) — schema drift.</summary>
    NoValidSamples,

    /// <summary>Valid samples exist, but all are older than the freshness window (Desktop idle/closed).</summary>
    Stale,

    /// <summary>Valid, fresh samples are available.</summary>
    Ok,
}

/// <summary>
/// Inspects the plan-history file and explains, in one line, why usage is or isn't
/// available. <see cref="PlanHistoryFile"/> deliberately swallows every failure and
/// returns an empty list (it must never crash the tray) — which is right for the polling
/// path but leaves a blank panel with no way to tell "Claude Desktop isn't installed"
/// from "the file has an unexpected format". Support reports could not be resolved
/// without asking users to run PowerShell by hand; this turns that into a fact the app
/// can state and copy to the clipboard.
/// </summary>
public sealed record PlanHistoryReport(
    PlanDataStatus Status,
    string Path,
    bool FileExists,
    long FileBytes,
    int RawSampleCount,
    int ValidSampleCount,
    IReadOnlyList<string> Orgs,
    TimeSpan? LatestSampleAge,
    string? Detail)
{
    /// <summary>Every location searched, in priority order — the evidence behind a "not found".</summary>
    public IReadOnlyList<string> Searched { get; init; } = [];

    /// <summary>What the Claude data folder beside the canonical path holds, when it exists.</summary>
    public IReadOnlyList<string> CanonicalFolderFiles { get; init; } = [];

    public int SearchedCount => Searched.Count == 0 ? 1 : Searched.Count;


    /// <summary>
    /// Short user-facing explanation for the popup when figures read "unknown".
    /// States what O-view observed — never what it assumes about the user's setup. The
    /// first version of this text told users to "install and run the Claude Desktop app",
    /// which was flatly wrong for a user who had it open at the time: a file O-view cannot
    /// read is not evidence that Desktop is absent. Report the observation and the path,
    /// and let the reader draw the conclusion (CLAUDE.md rule 6).
    /// </summary>
    public string Explain() => Status switch
    {
        // Deliberately offers no single remedy. An earlier version told users to restart
        // O-view; a user whose file genuinely does not exist followed that and nothing
        // changed, because the advice fitted a different failure. Say what was searched
        // and hand over to diagnostics rather than guessing which case this is.
        PlanDataStatus.FileMissing =>
            $"O-view searched {SearchedCount} location(s) for Claude Desktop's usage file — the "
            + $"source of session and weekly % — and found none. Last checked: {Path}. "
            + "If Claude Desktop is not running, start it and reopen this panel. If it is "
            + $"running: {DiagnosticsHint.Instruction} — O-view may be looking in the "
            + "wrong place for how Claude Desktop is installed on this machine.",
        PlanDataStatus.Unreadable =>
            $"O-view could not read {Path}. {DiagnosticsHint.Instruction} to report this.",
        PlanDataStatus.NoValidSamples =>
            $"O-view read {Path} but found no usage entries in the expected format. "
            + $"{DiagnosticsHint.Instruction} to report this.",
        PlanDataStatus.Stale =>
            "Claude Desktop has not recorded usage recently — figures may lag until it records again.",
        _ => "",
    };

    /// <summary>Multi-line report for the clipboard — everything needed to diagnose a blank panel.</summary>
    public string ToClipboardText(string appVersion)
    {
        var sb = new StringBuilder();
        sb.AppendLine("O-view diagnostics");
        sb.AppendLine($"  app version   : {appVersion}");
        sb.AppendLine($"  status        : {Status}");
        sb.AppendLine($"  path          : {Path}");
        sb.AppendLine($"  file exists   : {FileExists}{(FileExists ? $" ({FileBytes} bytes)" : "")}");
        sb.AppendLine($"  samples (raw) : {RawSampleCount}");
        sb.AppendLine($"  samples (valid): {ValidSampleCount}");
        sb.AppendLine($"  orgs in file  : {(Orgs.Count == 0 ? "none" : string.Join(", ", Orgs))}");
        sb.AppendLine($"  latest sample : {(LatestSampleAge is { } a ? $"{a.TotalMinutes:0.0} min old" : "n/a")}");
        if (Detail is { Length: > 0 })
        {
            sb.AppendLine($"  detail        : {Detail}");
        }

        // The search trail is the whole point when the answer is "not found": it shows
        // whether O-view even considered the location this machine actually uses.
        foreach (var (candidate, i) in Searched.Select((c, i) => (c, i)))
        {
            sb.AppendLine($"  searched [{i}]  : {candidate}{(File.Exists(candidate) ? "  <-- exists" : "")}");
        }
        if (CanonicalFolderFiles.Count > 0)
        {
            sb.AppendLine($"  claude folder : {string.Join(", ", CanonicalFolderFiles)}");
        }
        return sb.ToString();
    }
}

/// <summary>Builds a <see cref="PlanHistoryReport"/> for the plan-history file.</summary>
public static class PlanHistoryDiagnostics
{
    /// <summary>
    /// Inspects the file without throwing. Counts raw array entries separately from
    /// entries that actually parse, because the gap between the two is precisely the
    /// signature of schema drift — the case a plain "no data" message cannot express.
    /// </summary>
    public static PlanHistoryReport Inspect(string? path = null, TimeSpan? freshness = null)
    {
        var target = path ?? PlanHistoryFile.DefaultPath;
        var window = freshness ?? PlanHistoryProvider.DefaultFreshness;

        // Only meaningful when resolving the real machine location, not a test path.
        var searched = path is null ? PlanHistoryLocator.Candidates() : [target];
        var folderFiles = path is null ? CanonicalFolderContents() : [];

        if (!File.Exists(target))
        {
            return new PlanHistoryReport(PlanDataStatus.FileMissing, target, false, 0, 0, 0, [], null, null)
            {
                Searched = searched,
                CanonicalFolderFiles = folderFiles,
            };
        }

        long bytes = 0;
        try
        {
            bytes = new FileInfo(target).Length;
        }
        catch (IOException)
        {
            // Size is a nicety; failing to read it must not abort the report.
        }

        var raw = 0;
        string? detail = null;
        try
        {
            using var stream = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("samples", out var samples) &&
                samples.ValueKind == JsonValueKind.Array)
            {
                raw = samples.GetArrayLength();
            }
            else
            {
                detail = "no 'samples' array at the top level";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new PlanHistoryReport(PlanDataStatus.Unreadable, target, true, bytes, 0, 0, [],
                null, $"{ex.GetType().Name}: {ex.Message}");
        }

        // The same parse the provider uses, so the report reflects what the app actually sees.
        var valid = PlanHistoryFile.Read(target);
        var orgs = valid.Select(s => s.OrgUuid).Distinct().ToList();

        if (valid.Count == 0)
        {
            return new PlanHistoryReport(PlanDataStatus.NoValidSamples, target, true, bytes, raw, 0, orgs, null,
                detail ?? (raw > 0 ? "entries present but none matched the expected t/org/u.fh/u.sd shape" : "samples array is empty"));
        }

        var age = DateTimeOffset.UtcNow - valid[^1].AtUtc;
        var status = age <= window ? PlanDataStatus.Ok : PlanDataStatus.Stale;
        return new PlanHistoryReport(status, target, true, bytes, raw, valid.Count, orgs, age, detail)
        {
            Searched = searched,
            CanonicalFolderFiles = folderFiles,
        };
    }

    /// <summary>
    /// Names of files in the canonical Claude folder. Distinguishes "Claude Desktop keeps
    /// data here but no usage file" from "there is no Claude folder here at all" — the two
    /// need different answers, and a bare missing-file result cannot tell them apart.
    /// </summary>
    private static IReadOnlyList<string> CanonicalFolderContents()
    {
        try
        {
            var dir = Path.GetDirectoryName(PlanHistoryLocator.CanonicalPath);
            return dir is not null && Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir).Select(Path.GetFileName).OfType<string>().Take(25).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
