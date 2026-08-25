using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OView.Core.Storage;

/// <summary>
/// The one weekly reset instant O-view has ever been told, kept so it does not have to be
/// told again (ADR-0014).
///
/// <para><b>Why this is persisted at all.</b> The instant comes from Claude Code's
/// <c>cachedUsageUtilization</c>, which refreshes only when Claude Code fetches usage —
/// measured at <b>43 hours stale</b> on the development machine while the file holding it was
/// being rewritten every few minutes. A value read only while fresh is a value that is
/// usually absent. Read once and stored, it is correct forever after, because the reset is a
/// fixed weekly grid tied to the account.</para>
///
/// <para><b>This replaces <c>WeeklyResetLog</c>, and the justification for its predecessor's
/// existence does not carry over.</b> ADR-0011 kept observed resets outside the rebuildable
/// rollup store because an observation was unrepeatable — a reset seen and then lost cost a
/// full week before another could be caught. An anchor is not like that: it can be re-read
/// from <c>~/.claude.json</c> the next time Claude Code refreshes. Losing this file costs a
/// wait, not a week.</para>
///
/// <para>Written atomically (temp file then replace) and read defensively, like everything
/// else here: anything unparseable degrades to "not known yet", which the next refresh
/// refills.</para>
/// </summary>
public sealed class WeeklyResetAnchor
{
    /// <summary>Default location: <c>%LOCALAPPDATA%\O-view\weekly-reset.json</c>.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "O-view",
        "weekly-reset.json");

    /// <summary>
    /// The observation log this supersedes. Deleted on first successful save rather than left
    /// behind: it is a file full of inferences the app no longer makes, and a stale one in the
    /// same directory invites exactly the confusion of reading it as current state.
    /// </summary>
    public const string LegacyLogFileName = "weekly-resets.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;

    /// <summary>
    /// Where a stored anchor, or a failed write, is recorded. A delegate rather than a log
    /// interface, so <c>Core</c> keeps knowing nothing about how the app logs — the same seam
    /// the providers use.
    /// </summary>
    public Action<string>? Log { get; init; }

    public WeeklyResetAnchor(string? path = null)
    {
        _path = path ?? DefaultPath;
    }

    /// <summary>
    /// The stored anchor for <paramref name="orgUuid"/>, or null when none has been stored or
    /// the stored one belongs to a different organization.
    ///
    /// <para>Scoped by org because the window is per-account: someone who switches
    /// organizations must not be shown the previous account's schedule as though it were
    /// theirs. A stored anchor with no org matches anything, so a file written before an org
    /// was known is not stranded.</para>
    /// </summary>
    public DateTimeOffset? Read(string? orgUuid = null)
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var file = JsonSerializer.Deserialize<AnchorFile>(stream, SerializerOptions);

            if (file?.AnchorUtc is not { Length: > 0 } text ||
                !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var anchor))
            {
                return null;
            }

            var belongsToAnother =
                file.Org is { Length: > 0 } stored &&
                orgUuid is { Length: > 0 } wanted &&
                !string.Equals(stored, wanted, StringComparison.OrdinalIgnoreCase);

            return belongsToAnother ? null : anchor;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Stores <paramref name="anchorUtc"/>, unless an equivalent anchor is already stored.
    ///
    /// <para><b>Equivalent, not identical.</b> Claude reports the same weekly boundary with a
    /// different date every week, so comparing instants would rewrite the file on every
    /// refresh forever. Two instants that sit on the same weekly grid describe the same
    /// schedule, so only a genuine schedule change causes a write — which also makes the
    /// file's timestamp meaningful.</para>
    /// </summary>
    /// <returns>Whether anything was written.</returns>
    public bool Save(DateTimeOffset anchorUtc, string? orgUuid = null)
    {
        if (Read(orgUuid) is { } existing && OnSameGrid(existing, anchorUtc))
        {
            return false;
        }

        var file = new AnchorFile
        {
            Version = 2,
            AnchorUtc = anchorUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
            Org = orgUuid is { Length: > 0 } ? orgUuid : null,
        };

        var temp = _path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(temp, JsonSerializer.Serialize(file, SerializerOptions));
            File.Move(temp, _path, overwrite: true);
            Log?.Invoke($"weekly reset anchor stored: {anchorUtc:u} ({anchorUtc.UtcDateTime.DayOfWeek})");
            RemoveLegacyLog();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log?.Invoke($"weekly reset anchor write FAILED {ex.GetType().Name}: {ex.Message}");
            try { File.Delete(temp); } catch (IOException) { }
            return false;
        }
    }

    /// <summary>
    /// Whether two instants describe the same weekly schedule — the same weekday and time of
    /// day, to within a minute of slack for the sub-second jitter Claude reports.
    /// </summary>
    public static bool OnSameGrid(DateTimeOffset a, DateTimeOffset b)
    {
        var offset = (b - a).Ticks % Providers.PlanHistory.WeeklyWindow.Length.Ticks;
        if (offset < 0)
        {
            offset += Providers.PlanHistory.WeeklyWindow.Length.Ticks;
        }

        var drift = TimeSpan.FromTicks(Math.Min(offset, Providers.PlanHistory.WeeklyWindow.Length.Ticks - offset));
        return drift <= TimeSpan.FromMinutes(1);
    }

    /// <summary>Best-effort: a leftover observation log is litter, never an error.</summary>
    private void RemoveLegacyLog()
    {
        try
        {
            var legacy = Path.Combine(Path.GetDirectoryName(_path)!, LegacyLogFileName);
            if (File.Exists(legacy))
            {
                File.Delete(legacy);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class AnchorFile
    {
        [JsonPropertyName("version")] public int Version { get; set; }
        [JsonPropertyName("anchorUtc")] public string? AnchorUtc { get; set; }
        [JsonPropertyName("org")] public string? Org { get; set; }
    }
}
