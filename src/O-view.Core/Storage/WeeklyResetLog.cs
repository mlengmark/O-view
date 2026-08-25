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

    // There was a 12-hour MergeWindow here, and removing it is the fix for issue #136: it
    // merged two brackets that sat close together, which is only the same question as "the
    // same reset" while brackets stay narrow. See Absorb and CouldBeSeparateResets.

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

    /// <summary>
    /// Where a failed write is recorded. A delegate, so <c>Core</c> stays free of the app's
    /// logging — the same seam the providers use.
    ///
    /// <para><b>This file is the one piece of O-view state that cannot be rebuilt</b>
    /// (ADR-0011): a reset that is never written costs a week before another can be observed.
    /// <see cref="Write"/> nevertheless swallowed every failure, so the most expensive loss in
    /// the app was also its quietest. Measured in the field: a bundle reported six observations
    /// while the file on disk held five and had not been written for five days.</para>
    /// </summary>
    public Action<string>? Log { get; init; }

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
    /// Folds one observation into the set. Returns whether anything changed.
    ///
    /// <para>Three outcomes, and which one applies is decided by the brackets themselves
    /// rather than by how close together they happen to sit:</para>
    ///
    /// <list type="number">
    ///   <item><description><b>Overlapping — one reset.</b> Some instant satisfies both, so
    ///   they are the same reset. Keep the INTERSECTION: both brackets contain it, and
    ///   re-seeing a reset with better sampling on either side sharpens the answer instead
    ///   of duplicating it.</description></item>
    ///   <item><description><b>Disjoint, and far enough apart to be a period apart — two
    ///   resets.</b> Keep both. This is the case issue #136 was losing.</description></item>
    ///   <item><description><b>Disjoint, but too close together for two resets —
    ///   contradictory.</b> They cannot both be right and cannot be two weekly resets, so
    ///   keep the tighter rather than inventing an inverted interval.</description></item>
    /// </list>
    /// </summary>
    private static bool Absorb(List<WeeklyResetObservation> known, WeeklyResetObservation observation)
    {
        for (var i = 0; i < known.Count; i++)
        {
            if (!IsSameOrg(known[i], observation))
            {
                continue;
            }

            var earliest = Max(known[i].EarliestUtc, observation.EarliestUtc);
            var latest = Min(known[i].LatestUtc, observation.LatestUtc);

            if (earliest <= latest)
            {
                var merged = known[i] with { EarliestUtc = earliest, LatestUtc = latest };
                if (merged == known[i])
                {
                    return false;
                }

                known[i] = merged;
                return true;
            }

            if (CouldBeSeparateResets(known[i], observation))
            {
                continue;   // a different reset — keep looking, and add it if nothing claims it
            }

            if (known[i].Uncertainty <= observation.Uncertainty)
            {
                return false;
            }

            known[i] = observation;
            return true;
        }

        known.Add(observation);
        return true;
    }

    private static bool IsSameOrg(WeeklyResetObservation a, WeeklyResetObservation b) =>
        string.Equals(a.OrgUuid, b.OrgUuid, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether two non-overlapping brackets could hold resets a full period apart.
    ///
    /// <para><b>Width, not proximity, is what separates the two cases</b> — which is the
    /// whole lesson of issue #136. The two brackets that triggered it were disjoint by
    /// <i>47 minutes</i>, so every proximity test in the world calls them the same reset;
    /// what makes them different resets is that each is ~6.7 days <i>wide</i>, so the
    /// instants inside them can be a week apart and routinely are.</para>
    ///
    /// <para>The test is therefore the widest separation the two brackets admit. Two narrow
    /// brackets an hour apart can only ever place their resets about a day apart, which no
    /// weekly cadence explains, so they stay contradictory and the old behaviour is kept for
    /// them. Two week-wide brackets can place theirs a week apart, so they are allowed to be
    /// two resets.</para>
    /// </summary>
    private static bool CouldBeSeparateResets(WeeklyResetObservation a, WeeklyResetObservation b)
    {
        var widest = a.LatestUtc >= b.LatestUtc
            ? a.LatestUtc - b.EarliestUtc
            : b.LatestUtc - a.EarliestUtc;

        return widest >= WeeklyResetDetector.WindowLength - WeeklyResetDetector.PeriodTolerance;
    }

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
            Log?.Invoke($"weekly-reset log written ({observations.Count} observation(s))");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still swallowed — a failed write here must not take down the poll that
            // discovered the reset — but no longer silent. See the Log remarks: this is the
            // only unrebuildable state in the app, so a lost write is the one worth naming.
            Log?.Invoke($"weekly-reset log write FAILED {ex.GetType().Name}: {ex.Message}");
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
