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

    /// <param name="path">File to read; defaults to the real Claude Desktop location.</param>
    /// <param name="orgUuid">
    /// Organization to track, from `~/.claude.json` → oauthAccount.organizationUuid.
    /// Multi-org accounts interleave samples, so filtering matters; null uses all
    /// samples (single-org machines).
    /// </param>
    /// <param name="freshness">Maximum sample age still labelled <see cref="DataSource.Live"/>.</param>
    public PlanHistoryProvider(string? path = null, string? orgUuid = null, TimeSpan? freshness = null)
    {
        _path = path ?? PlanHistoryFile.DefaultPath;
        _orgUuid = orgUuid;
        _freshness = freshness ?? DefaultFreshness;
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

    private IReadOnlyList<PlanHistorySample> ReadSamples()
    {
        var samples = PlanHistoryFile.Read(_path);
        return _orgUuid is not null
            ? samples.Where(s => s.OrgUuid == _orgUuid).ToList()
            : samples;
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

        return new UsageSnapshot(
            source,
            latest.FiveHourPercent,
            latest.SevenDayPercent,
            nextReset,
            latest.AtUtc);
    }
}
