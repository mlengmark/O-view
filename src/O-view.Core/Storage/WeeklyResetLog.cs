using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using OView.Core.Providers.PlanHistory;

namespace OView.Core.Storage;

/// <summary>
/// The persisted record of every weekly reset O-view has ever seen (ADR-0011).
///
/// <para><b>Why its own file rather than the rollup store.</b> The rollup store is a
/// derived cache: everything in it can be rebuilt by re-reading the JSONL transcripts, so
/// it is allowed to detect corruption and start over (issue #16) — and on the dev machine
/// it actually did, four times in six days. Weekly resets are the opposite: they are
/// observations of a moment that has passed, the source file retains only days of history,
/// and a reset thrown away costs a full week before another can be seen. Precious,
/// unrebuildable state must not live inside something designed to wipe itself.</para>
///
/// <para>Written atomically (temp file + replace) so an interrupted write cannot leave a
/// half-file behind, and read defensively: anything unparseable degrades to "nothing
/// observed yet", which the discovery loop simply starts filling again. A failure here
/// must never reach the tray.</para>
/// </summary>
public sealed class WeeklyResetLog : IWeeklyResetLog
{
    /// <summary>Default location: %LOCALAPPDATA%\O-view\weekly-resets.json</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "O-view",
        "weekly-resets.json");

    /// <summary>
    /// Two observations closer together than this describe the same reset and are merged.
    /// Real resets are a week apart, and the widest bracket a closed-overnight Desktop can
    /// produce is well under a day, so there is a large margin either side.
    /// </summary>
    public static readonly TimeSpan MergeWindow = TimeSpan.FromHours(12);

    /// <summary>
    /// Cap on retained observations — over a year of weekly resets, and prediction only
    /// ever consults the most precise and the two most recent. Bounds the file forever.
    /// </summary>
    public const int MaxObservations = 64;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;

    public WeeklyResetLog(string? path = null)
    {
        _path = path ?? DefaultPath;
    }

    public IReadOnlyList<WeeklyResetObservation> GetObservations(string? orgUuid = null)
    {
        var all = Read();
        return orgUuid is null
            ? all
            : all.Where(o => string.Equals(o.OrgUuid, orgUuid, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public void Record(IEnumerable<WeeklyResetObservation> observations)
    {
        var incoming = observations.ToList();
        if (incoming.Count == 0)
        {
            return;
        }

        var merged = Read().ToList();
        var changed = false;
        foreach (var observation in incoming)
        {
            changed |= Absorb(merged, observation);
        }

        if (!changed)
        {
            return;     // every reset already known — the steady state, so no write
        }

        merged.Sort((a, b) => a.LatestUtc.CompareTo(b.LatestUtc));
        if (merged.Count > MaxObservations)
        {
            merged.RemoveRange(0, merged.Count - MaxObservations);
        }

        Write(merged);
    }

    /// <summary>
    /// Imports resets recorded by the pre-ADR-0011 store, which kept a single instant per
    /// reset in the rollup DB's <c>weekly_resets</c> table. Those could only be written by
    /// the old in-cadence-only detector, so each one is known to have been caught while
    /// Desktop was sampling — hence a <see cref="WeeklyResetDetector.PreciseBracket"/>
    /// bracket ending at the recorded instant, which is what the old value meant.
    /// Idempotent, so running it on every launch is harmless.
    /// </summary>
    public void ImportLegacy(IEnumerable<DateTimeOffset> legacyResets, string orgUuid) =>
        Record(legacyResets.Select(at => new WeeklyResetObservation(
            at - WeeklyResetDetector.PreciseBracket, at, orgUuid)));

    /// <summary>
    /// Folds one observation into the set, merging it with an existing record of the same
    /// reset. Merging keeps the INTERSECTION of the two brackets: both contain the reset,
    /// so their overlap does too, and re-seeing a reset with better sampling on either side
    /// sharpens the answer instead of duplicating it. Returns whether anything changed.
    /// </summary>
    private static bool Absorb(List<WeeklyResetObservation> known, WeeklyResetObservation observation)
    {
        for (var i = 0; i < known.Count; i++)
        {
            if (!IsSameReset(known[i], observation))
            {
                continue;
            }

            var earliest = Max(known[i].EarliestUtc, observation.EarliestUtc);
            var latest = Min(known[i].LatestUtc, observation.LatestUtc);

            // Disjoint brackets cannot both be right about one reset — keep the tighter
            // one rather than inventing an empty or inverted interval.
            var merged = earliest <= latest
                ? known[i] with { EarliestUtc = earliest, LatestUtc = latest }
                : (known[i].Uncertainty <= observation.Uncertainty ? known[i] : observation);

            if (merged == known[i])
            {
                return false;
            }

            known[i] = merged;
            return true;
        }

        known.Add(observation);
        return true;
    }

    private static bool IsSameReset(WeeklyResetObservation a, WeeklyResetObservation b) =>
        string.Equals(a.OrgUuid, b.OrgUuid, StringComparison.OrdinalIgnoreCase) &&
        a.EarliestUtc - MergeWindow <= b.LatestUtc &&
        b.EarliestUtc - MergeWindow <= a.LatestUtc;

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a >= b ? a : b;
    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;

    // ── persistence ────────────────────────────────────────────────────────────

    private IReadOnlyList<WeeklyResetObservation> Read()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var file = JsonSerializer.Deserialize<LogFile>(stream, SerializerOptions);
            if (file?.Observations is null)
            {
                return [];
            }

            return file.Observations
                .Select(Validate)
                .OfType<WeeklyResetObservation>()
                .OrderBy(o => o.LatestUtc)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A log we cannot read is indistinguishable from one we have not written yet,
            // and the discovery loop refills it. Never let this reach the caller.
            return [];
        }
    }

    /// <summary>
    /// Rejects entries that cannot describe a reset — the file is ours, but it is still a
    /// file on disk that anything could have touched, and an inverted bracket would make
    /// every prediction downstream nonsense.
    /// </summary>
    private static WeeklyResetObservation? Validate(LogEntry entry)
    {
        if (!TryParse(entry.Earliest, out var earliest) || !TryParse(entry.Latest, out var latest))
        {
            return null;
        }
        return earliest <= latest
            ? new WeeklyResetObservation(earliest, latest, entry.Org ?? "")
            : null;
    }

    private static bool TryParse(string? value, out DateTimeOffset parsed) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed);

    private void Write(IReadOnlyList<WeeklyResetObservation> observations)
    {
        var file = new LogFile
        {
            Version = 1,
            Observations = observations
                .Select(o => new LogEntry
                {
                    Earliest = o.EarliestUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
                    Latest = o.LatestUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
                    Org = o.OrgUuid.Length == 0 ? null : o.OrgUuid,
                })
                .ToList(),
        };

        // Temp-then-replace: a crash mid-write leaves the previous good file intact rather
        // than a truncated one, which for unrebuildable state is worth the extra syscall.
        var temp = _path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(temp, JsonSerializer.Serialize(file, SerializerOptions));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temp); } catch (IOException) { }
        }
    }

    private sealed class LogFile
    {
        [JsonPropertyName("version")] public int Version { get; set; }
        [JsonPropertyName("observations")] public List<LogEntry>? Observations { get; set; }
    }

    private sealed class LogEntry
    {
        [JsonPropertyName("earliest")] public string? Earliest { get; set; }
        [JsonPropertyName("latest")] public string? Latest { get; set; }
        [JsonPropertyName("org")] public string? Org { get; set; }
    }
}
