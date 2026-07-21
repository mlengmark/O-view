namespace OView.Core.Models;

/// <summary>Severity band for a usage percentage. The one place the thresholds live.</summary>
public enum UsageLevel
{
    /// <summary>0–49% — green.</summary>
    Normal,

    /// <summary>50–69% — amber.</summary>
    Warning,

    /// <summary>70–100% — red.</summary>
    Critical,
}

/// <summary>
/// Classifies usage into colour bands. Shared by the tray icon, the popup bars, and
/// the notification default so they can never disagree — each surface maps the level
/// to its own colour type (System.Drawing vs System.Windows.Media), but the boundaries
/// are defined once here. Bands set by GitHub issue #2 (2026-07-21).
/// </summary>
public static class UsageLevels
{
    public const int WarningPercent = 50;
    public const int CriticalPercent = 70;

    public static UsageLevel Classify(int percent) => percent switch
    {
        >= CriticalPercent => UsageLevel.Critical,
        >= WarningPercent => UsageLevel.Warning,
        _ => UsageLevel.Normal,
    };
}
