using System.Globalization;

namespace OView.Core.Models;

/// <summary>
/// Builds the tray tooltip. NotifyIcon.Text caps at 127 characters (measured —
/// docs/findings/tray-icon-rendering.md), so output is always truncated defensively.
/// Times convert to local only here, at the display edge.
/// </summary>
public static class TooltipFormatter
{
    public const int MaxLength = 127;

    public static string Format(UsageSnapshot snapshot, TimeZoneInfo? displayZone = null)
    {
        var zone = displayZone ?? TimeZoneInfo.Local;
        var text = snapshot.Source switch
        {
            DataSource.Live => FormatAuthoritative(snapshot, zone, staleSuffix: null),
            DataSource.Stale => FormatAuthoritative(snapshot, zone,
                staleSuffix: snapshot.CapturedAtUtc is { } at ? $" (as of {ToLocal(at, zone):HH:mm})" : " (stale)"),
            DataSource.Estimate => "O-view · local estimate · usage % unknown",
            _ => "O-view · no usage data",
        };

        return text.Length <= MaxLength ? text : text[..MaxLength];
    }

    private static string FormatAuthoritative(UsageSnapshot s, TimeZoneInfo zone, string? staleSuffix)
    {
        var session = s.SessionPercent is { } fh
            ? string.Create(CultureInfo.InvariantCulture, $"5h: {fh}%")
            : "5h: ?";
        var reset = s.SessionResetAtUtc is { } r
            ? string.Create(CultureInfo.InvariantCulture, $" · resets {ToLocal(r, zone):HH:mm}")
            : "";
        var weekly = s.WeeklyPercent is { } sd
            ? string.Create(CultureInfo.InvariantCulture, $" · 7d: {sd}%")
            : "";
        return session + (staleSuffix ?? "") + reset + weekly;
    }

    private static DateTimeOffset ToLocal(DateTimeOffset utc, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(utc, zone);
}
