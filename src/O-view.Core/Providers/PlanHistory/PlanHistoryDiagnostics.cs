using System.Text;
using System.Text.Json;

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
        PlanDataStatus.FileMissing =>
            $"O-view could not find Claude Desktop's usage file at {Path} — it is the source of "
            + "session and weekly %. If Claude Desktop is not running, start it and reopen this "
            + "panel. If it IS running, exit O-view and start it again from the Start Menu: an "
            + "instance that cannot see this file does not recover on its own.",
        PlanDataStatus.Unreadable =>
            $"O-view could not read {Path}. Right-click the tray icon → Copy diagnostics to report this.",
        PlanDataStatus.NoValidSamples =>
            $"O-view read {Path} but found no usage entries in the expected format. "
            + "Right-click the tray icon → Copy diagnostics to report this.",
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

        if (!File.Exists(target))
        {
            return new PlanHistoryReport(PlanDataStatus.FileMissing, target, false, 0, 0, 0, [], null, null);
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
        return new PlanHistoryReport(status, target, true, bytes, raw, valid.Count, orgs, age, detail);
    }
}
