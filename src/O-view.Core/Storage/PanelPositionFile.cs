using System.Text.Json;
using System.Text.Json.Serialization;

namespace OView.Core.Storage;

/// <summary>
/// Where the user last left the panel on each display, kept so the next open can put it back
/// there (ADR-0017) — the storage half of the shared placement rule in
/// <c>OView.App.Rendering.PanelPlacementMemory</c>, which this cannot reference: Core may use
/// only the BCL, and <c>PanelPlacementMemory</c>'s own corner type lives above it.
///
/// <para>Written atomically (temp file then replace) and read defensively, exactly like
/// <see cref="WeeklyResetAnchor"/>: anything unparseable degrades to "nothing remembered",
/// which the caller already has to handle for a display it has never seen before.</para>
///
/// <para><b>Per display, not one slot.</b> A laptop's external monitor is not always attached,
/// and overwriting its remembered spot with the laptop panel's own would be wrong the next
/// time both are back. Each display keeps its own entry, keyed by whatever id the caller
/// supplies — this file knows nothing about what makes two displays the same one.</para>
/// </summary>
public sealed class PanelPositionFile
{
    /// <summary>Default location: <c>%LOCALAPPDATA%\O-view\panel-position.json</c>.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "O-view",
        "panel-position.json");

    /// <summary>
    /// A remembered place, in device-independent units: the name of the work-area corner it
    /// was left nearest to, and the offset inward from that corner. The corner is a bare
    /// string here, not an enum — Core may not name the type <c>PanelPlacementMemory</c>
    /// defines, so the translation is the caller's job.
    /// </summary>
    public readonly record struct Place(string Corner, double OffsetXDip, double OffsetYDip);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;

    /// <summary>
    /// Where a failed write is recorded. A delegate rather than a log interface, so Core keeps
    /// knowing nothing about how the app logs — the same seam <see cref="WeeklyResetAnchor.Log"/> uses.
    /// </summary>
    public Action<string>? Log { get; init; }

    public PanelPositionFile(string? path = null)
    {
        _path = path ?? DefaultPath;
    }

    /// <summary>
    /// The stored place for <paramref name="displayId"/>, or null when none is stored, the
    /// file is absent or unreadable, or that display has never been saved to.
    /// </summary>
    public Place? Read(string displayId) =>
        ReadAll().TryGetValue(displayId, out var entry) && entry.Corner is { Length: > 0 } corner
            ? new Place(corner, entry.OffsetXDip, entry.OffsetYDip)
            : null;

    /// <summary>
    /// Stores <paramref name="place"/> for <paramref name="displayId"/>, leaving every other
    /// display's entry untouched.
    /// </summary>
    /// <returns>Whether the write succeeded.</returns>
    public bool Save(string displayId, Place place)
    {
        var displays = ReadAll();
        displays[displayId] = new Entry
        {
            Corner = place.Corner,
            OffsetXDip = place.OffsetXDip,
            OffsetYDip = place.OffsetYDip,
        };

        var file = new PositionFile { Version = 1, Displays = displays };
        var temp = _path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(temp, JsonSerializer.Serialize(file, SerializerOptions));
            File.Move(temp, _path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log?.Invoke($"panel position write FAILED {ex.GetType().Name}: {ex.Message}");
            try { File.Delete(temp); } catch (IOException) { }
            return false;
        }
    }

    private Dictionary<string, Entry> ReadAll()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var file = JsonSerializer.Deserialize<PositionFile>(stream, SerializerOptions);
            return file?.Displays ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private sealed class PositionFile
    {
        [JsonPropertyName("version")] public int Version { get; set; }
        [JsonPropertyName("displays")] public Dictionary<string, Entry> Displays { get; set; } = [];
    }

    private sealed class Entry
    {
        [JsonPropertyName("corner")] public string? Corner { get; set; }
        [JsonPropertyName("offsetXDip")] public double OffsetXDip { get; set; }
        [JsonPropertyName("offsetYDip")] public double OffsetYDip { get; set; }
    }
}
