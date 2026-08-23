using System.Text.Json;
using OView.Core.Providers.PlanHistory;

namespace OView.Core.Models;

/// <summary>
/// Persisted user settings, in %LOCALAPPDATA%\O-view\settings.json.
///
/// <para>The default threshold aligns with the "Critical" colour band
/// (<see cref="UsageLevels.CriticalPercent"/>), so out of the box O-view notifies exactly
/// when the gauge turns red — GitHub issue #2.</para>
///
/// <para><b>Run-at-startup is deliberately NOT here.</b> The registry Run key is its single
/// source of truth, so the two can never disagree about whether O-view starts with Windows,
/// and Task Manager's startup page — which edits that key directly — stays authoritative.</para>
/// </summary>
/// <param name="LastUpdateNoticeTag">
/// The release tag the user has already been told about, so a build that cannot update
/// itself says so <b>once per version</b> rather than on every check.
///
/// <para>Persisted rather than held in memory because the check runs every 24 h and the app
/// is designed to run for days: in-memory state would re-nag after every restart, and a
/// notice the user has already acted on is noise. Empty means "never told".</para>
/// </param>
/// <param name="UpdateAutomatically">
/// Whether the daily background check may install a newer release without asking each time
/// (ADR-0009, amended 2026-08-19 for GitHub issue #140).
///
/// <para><b>Default false, and a release must never turn it on.</b> ADR-0009 rejected silent
/// auto-install because it acts <i>without consent</i>; what makes this acceptable is that
/// the user chose it, once, knowingly. A default would remove precisely that. The default
/// here is also the fallback for a settings file that predates the field or fails to
/// load — so an upgrade never switches it on behind the user.</para>
///
/// <para>It is <b>not</b> sufficient on its own: <c>UpdatePolicy.MayDownloadAndRun</c> still
/// decides whether anything may be fetched or executed, and a portable, <c>.deb</c> or
/// tarball build ignores this setting entirely.</para>
/// </param>
/// <param name="WeeklyResetDay">
/// Weekday of the user's weekly reset, as read from Claude's Settings → Usage. Empty means
/// "not set", and O-view derives the reset instead (GitHub issue #186).
///
/// <para><b>Deliberately not org-scoped</b>, unlike the observation log. Windows are
/// per-organization, so an account that switches org would carry the wrong entry across —
/// but a settings file is the natural home for something the user typed, and the failure is
/// visible and one edit to fix. Revisit if multi-org turns out to be common.</para>
/// </param>
/// <param name="WeeklyResetTime">
/// Time of day of that reset, <c>HH:mm</c>, local. Stored as text so the file stays legible
/// and no enum ordinal can shift meaning underneath it.
/// </param>
public sealed record TraySettings(
    bool NotifyOnThreshold = true,
    int ThresholdPercent = UsageLevels.CriticalPercent,
    string LastUpdateNoticeTag = "",
    bool UpdateAutomatically = false,
    string WeeklyResetDay = "",
    string WeeklyResetTime = "",
    string WeeklyResetConflictNoticed = "")
{
    /// <summary>The user's entered reset, or null when unset or unreadable.</summary>
    public ManualWeeklyReset? WeeklyReset => ManualWeeklyReset.Parse(WeeklyResetDay, WeeklyResetTime);

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "O-view",
        "settings.json");

    /// <summary>Load settings; defaults on any failure. Never throws.</summary>
    public static TraySettings Load(string? path = null)
    {
        try
        {
            var loaded = JsonSerializer.Deserialize<TraySettings>(File.ReadAllText(path ?? DefaultPath));
            return loaded is null || loaded.ThresholdPercent is < 1 or > 100
                ? new TraySettings()
                : loaded;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new TraySettings();
        }
    }

    /// <summary>Persist settings; failures are swallowed — losing a preference must not crash the tray.</summary>
    public void Save(string? path = null)
    {
        try
        {
            var target = path ?? DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, JsonSerializer.Serialize(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
