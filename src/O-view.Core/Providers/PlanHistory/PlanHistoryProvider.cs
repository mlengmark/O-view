using OView.Core.Models;

namespace OView.Core.Providers.PlanHistory;

/// <summary>
/// Primary usage provider (ADR-0007). Reads the utilisation time series Claude Desktop
/// maintains locally — no token, no network, no rate limits. Degrades to
/// <see cref="UsageSnapshot.None"/> when the file is missing or empty, and to
/// <see cref="DataSource.Stale"/> when Desktop has not sampled recently (e.g. it is
/// closed); the composite provider then falls back (ADR-0002 precedence).
/// </summary>
public sealed class PlanHistoryProvider : IUsageProvider
{
    /// <summary>
    /// Desktop samples every ~300 s; three missed samples means it is very likely not
    /// running, at which point the data stops tracking reality.
    /// </summary>
    public static readonly TimeSpan DefaultFreshness = TimeSpan.FromMinutes(15);

    private readonly string _path;
    private readonly string? _orgUuid;
    private readonly TimeSpan _freshness;
    private readonly IWeeklyResetLog? _weeklyResetLog;

    /// <param name="path">File to read; defaults to the real Claude Desktop location.</param>
    /// <param name="orgUuid">
    /// Preferred organization to track, from `~/.claude.json` → oauthAccount.organizationUuid.
    /// Only used to <em>disambiguate</em> a file that interleaves several orgs; it is never
    /// allowed to blank a file whose samples it doesn't match. That distinction matters:
    /// `~/.claude.json` is written by Claude Code and `plan-usage-history.json` by Claude
    /// Desktop, so if the two apps are signed into different accounts the keys differ — and
    /// a single-account dev machine never reveals it. null tracks whatever the file holds.
    /// See <see cref="ReadSamples"/>.
    /// </param>
    /// <param name="freshness">Maximum sample age still labelled <see cref="DataSource.Live"/>.</param>
    /// <param name="weeklyResetLog">
    /// Persists observed weekly resets so the 7-day reset survives the source file's finite
    /// retention (issue #6, ADR-0011). Null disables weekly-reset prediction — fine for
    /// tests and for the JSONL-only fallback.
    /// </param>
    public PlanHistoryProvider(string? path = null, string? orgUuid = null, TimeSpan? freshness = null,
        IWeeklyResetLog? weeklyResetLog = null)
    {
        _path = path ?? PlanHistoryFile.DefaultPath;
        _orgUuid = orgUuid;
        _freshness = freshness ?? DefaultFreshness;
        _weeklyResetLog = weeklyResetLog;
    }

    /// <summary>
    /// Meter values for the current session window (since the last observed reset),
    /// plus that window's start — the inputs the divergence detector needs. Empty
    /// when no data is available.
    /// </summary>
    public (DateTimeOffset WindowStartUtc, IReadOnlyList<int> Percents) GetCurrentWindow(DateTimeOffset utcNow)
    {
        var samples = ReadSamples();
        if (samples.Count == 0)
        {
            return (utcNow, []);
        }

        // Anchor on the last reset so the window never spans one — a reset would show
        // as a large negative rise and mask real divergence.
        var windowStart = ResetDetector.FindLastDrop(samples) ?? samples[0].AtUtc;
        var inWindow = samples.Where(s => s.AtUtc >= windowStart).ToList();

        return (windowStart, inWindow.Select(s => s.FiveHourPercent).ToList());
    }

    /// <summary>
    /// Reads samples, resolving to a single organization when the file interleaves several.
    /// The org filter's only legitimate job is de-interleaving a multi-org file; it must
    /// never blank a file it simply doesn't match. So:
    /// <list type="bullet">
    /// <item>prefer <see cref="_orgUuid"/> when it matches at least one sample;</item>
    /// <item>otherwise fall back to the file's most-recently-active org — one org's data,
    /// still de-interleaved, but the machine's real usage instead of nothing.</item>
    /// </list>
    /// The fallback is what makes O-view work when Claude Code and Claude Desktop are signed
    /// into different accounts: the old code returned an empty set and every panel read
    /// "unknown", even though the Desktop file held perfectly good usage.
    /// </summary>
    private IReadOnlyList<PlanHistorySample> ReadSamples()
    {
        var samples = PlanHistoryFile.Read(_path);
        if (_orgUuid is null || samples.Count == 0)
        {
            return samples;
        }

        var forOrg = samples.Where(s => s.OrgUuid == _orgUuid).ToList();
        if (forOrg.Count > 0)
        {
            return forOrg;
        }

        // Preferred org matched nothing — use the most recent org rather than blanking.
        // Samples are time-sorted (PlanHistoryFile), so the last one is the active org.
        var latestOrg = samples[^1].OrgUuid;
        return samples.Where(s => s.OrgUuid == latestOrg).ToList();
    }

    public UsageSnapshot GetSnapshot(DateTimeOffset utcNow)
    {
        var samples = ReadSamples();
        if (samples.Count == 0) return UsageSnapshot.None;

        var latest = samples[^1];
        var age = utcNow - latest.AtUtc;
        var source = age <= _freshness ? DataSource.Live : DataSource.Stale;

        var lastDrop = ResetDetector.FindLastDrop(samples);
        var nextReset = ResetDetector.PredictNextReset(lastDrop, utcNow);

        // Weekly reset (issue #6, ADR-0011). This IS the discovery loop: every poll
        // re-scans the whole retained series for `sd` drops and folds them into the
        // persisted log, so a reset is picked up on the first poll after it appears in the
        // file and re-recording an already-known one is a no-op. Prediction then runs off
        // the full history, not just what this file still holds.
        var weeklyReset = ForecastWeeklyReset(samples, utcNow);

        return new UsageSnapshot(
            source,
            latest.FiveHourPercent,
            latest.SevenDayPercent,
            nextReset,
            latest.AtUtc,
            weeklyReset?.AtUtc,
            weeklyReset?.Uncertainty,
            weeklyReset?.Period);
    }

    /// <summary>
    /// Records this poll's observations and predicts from everything recorded so far.
    /// Scoped to the org the samples belong to: windows are per-organization, and an
    /// account that switches org must not have the two sets of resets averaged together.
    /// </summary>
    private WeeklyResetForecast? ForecastWeeklyReset(
        IReadOnlyList<PlanHistorySample> samples, DateTimeOffset utcNow)
    {
        if (_weeklyResetLog is null)
        {
            return null;
        }

        try
        {
            _weeklyResetLog.Record(WeeklyResetDetector.FindResets(samples));
            return WeeklyResetDetector.PredictNextReset(
                _weeklyResetLog.GetObservations(samples[^1].OrgUuid), utcNow);
        }
        catch (Exception)
        {
            // The weekly-reset log is one part of the panel; a failure inside it — an
            // unreadable file, a locked directory — must never take down the plan-history
            // percentages, which do not depend on it. Degrade to unknown and carry on.
            return null;
        }
    }
}
