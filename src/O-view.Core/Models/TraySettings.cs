using System.Text.Json;

namespace OView.Core.Models;

/// <summary>
/// User settings, persisted to %LOCALAPPDATA%\O-view\settings.json. Run-at-startup
/// is deliberately NOT here — the registry Run key is its single source of truth.
/// </summary>
public sealed record TraySettings(bool NotifyOnThreshold = true, int ThresholdPercent = 85)
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
