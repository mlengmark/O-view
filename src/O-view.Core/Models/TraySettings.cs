using System.Text.Json;

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
public sealed record TraySettings(bool NotifyOnThreshold = true, int ThresholdPercent = UsageLevels.CriticalPercent)
{
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
