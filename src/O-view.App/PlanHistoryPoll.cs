using OView.Core.Models;
using OView.Core.Providers;
using OView.Core.Providers.PlanHistory;
using OView.Core.Storage;

namespace OView.App;

/// <summary>
/// The cheap plan-history cadence and the panel's statistics (issue #163): reading the
/// plan-history file alone, publishing it without disturbing the full poll's own result, and
/// building the 31-day rollup plus off-plan divergence both cadences' callers read from.
///
/// <para>Split out of <see cref="UsageEngine"/> purely to shorten that file — same members,
/// same behaviour, same logging. <see cref="UsageEngine.Latest"/> stays with the engine, since
/// both cadences publish to it; this reads and writes it through the <paramref
/// name="getLatest"/>/<paramref name="publish"/> delegates the constructor takes, the same seam
/// <see cref="UsageCacheRefresh"/> and <see cref="WeeklyResetCoordinator"/> use for their own
/// engine-owned state.</para>
/// </summary>
public sealed class PlanHistoryPoll
{
    private readonly PlanHistoryProvider? _planHistory;
    private readonly RollupStore _store;
    private readonly IUsageProvider _cachedUtilization;
    private readonly WeeklyResetCoordinator _weeklyReset;
    private readonly IClock _clock;
    private readonly IAppLog? _log;
    private readonly TimeZoneInfo _displayZone;
    private readonly string? _simulateDivergence;
    private readonly Func<UsageSnapshot> _getLatest;
    private readonly Action<UsageSnapshot> _publish;
    private readonly Action<Func<UsageSnapshot>, Action<UsageSnapshot>> _runOffThread;

    /// <summary>
    /// Whether the plan-history provider this poll holds is one the caller actually asked for,
    /// and so may be published from directly.
    ///
    /// <para>False in exactly one case: a caller injected a whole
    /// <see cref="UsageEngineOptions.Provider"/> and said nothing about plan history. The
    /// engine still builds a provider then, because the off-plan arithmetic needs one — but it
    /// defaults to the <b>real machine's</b> <c>plan-usage-history.json</c>, and it is not part
    /// of the injected chain. Publishing from it would put the developer's own Claude Desktop
    /// usage over the caller's resolution, which in the test suite means results that depend
    /// on whose machine is running them.</para>
    ///
    /// <para>Naming either <see cref="UsageEngineOptions.PlanHistory"/> or
    /// <see cref="UsageEngineOptions.PlanHistoryPath"/> is enough — both say which file is
    /// meant, which is the whole question.</para>
    ///
    /// <para><b>Kept after issue #212, and it is no longer the only defence.</b> The provider
    /// built in that case now reads <see cref="PlanHistoryFile.NoFile"/> rather than the real
    /// machine's file, which is what fixes the off-plan path this flag never covered. This
    /// still guards <i>publishing</i>, which is a different question: an inert provider would
    /// publish empty snapshots over a caller's own chain rather than the developer's usage,
    /// which is better and still wrong.</para>
    /// </summary>
    private readonly bool _planHistoryIsAddressed;

    public PlanHistoryPoll(
        PlanHistoryProvider? planHistory,
        bool planHistoryIsAddressed,
        RollupStore store,
        IUsageProvider cachedUtilization,
        WeeklyResetCoordinator weeklyReset,
        IClock clock,
        IAppLog? log,
        TimeZoneInfo displayZone,
        string? simulateDivergence,
        Func<UsageSnapshot> getLatest,
        Action<UsageSnapshot> publish,
        Action<Func<UsageSnapshot>, Action<UsageSnapshot>> runOffThread)
    {
        _planHistory = planHistory;
        _planHistoryIsAddressed = planHistoryIsAddressed;
        _store = store;
        _cachedUtilization = cachedUtilization;
        _weeklyReset = weeklyReset;
        _clock = clock;
        _log = log;
        _displayZone = displayZone;
        _simulateDivergence = simulateDivergence;
        _getLatest = getLatest;
        _publish = publish;
        _runOffThread = runOffThread;
    }

    /// <summary>
    /// How many <b>plan-history</b> samples the current window holds. Zero when there is no
    /// plan history to read — the state every test with an injected provider chain must be in
    /// (issue #212).
    ///
    /// <para>Deliberately not the size of the series the detector evaluates, which may carry
    /// one more (<see cref="MeterSeries.WithReportedReading"/>). This exists to assert what
    /// was read from <i>this file</i>, so folding the other source in would blunt the
    /// isolation check it is here for.</para>
    /// </summary>
    public int WindowSampleCount =>
        _planHistory?.GetCurrentWindow(_clock.UtcNow).Percents.Count ?? 0;

    /// <summary>
    /// The cheap half, on its own faster cadence: read the plan-history file and nothing else.
    ///
    /// <para><b>Why this is separate.</b> The two things a poll reads have costs three orders
    /// of magnitude apart. Measured on a real machine: the plan-history read, parse and
    /// reset-scan is <b>3.3 ms</b>; the transcript walk behind the token tiles is <b>32 files
    /// and 92 MB</b>. They were on one 60-second timer, so the percentages — the thing the
    /// icon and the bars actually show — waited on the ingest that froze the app on a large
    /// history (issue #125).</para>
    ///
    /// <para>What this does <i>not</i> do is make the data fresher than its source. Claude
    /// Desktop writes that file every ~5 minutes (measured median 5.00 min over 1,828 gaps),
    /// and nothing here can beat it. What it removes is O-view's own contribution: up to a
    /// full 60 s of extra lag on top, now at most <see cref="UsageEngineOptions.PlanPollInterval"/>.
    /// Polling faster than Desktop writes would re-read an unchanged file for nothing.</para>
    /// </summary>
    public void PollPlanHistory()
    {
        // Nothing to publish from when the caller injected a provider chain and never said
        // which plan-history file it stands for — see _planHistoryIsAddressed. Production
        // always passes this; it is only reachable from a test.
        if (!_planHistoryIsAddressed)
        {
            return;
        }

        _runOffThread(ReadPlanHistory, PublishPlanHistory);
    }

    /// <summary>Never throws: a bad read must not kill the cadence.</summary>
    private UsageSnapshot ReadPlanHistory()
    {
        try
        {
            var utcNow = _clock.UtcNow;
            var snapshot = _planHistory?.GetSnapshot(utcNow) ?? UsageSnapshot.None;

            // The same two folds the full poll applies, in the same order, because both
            // cadences publish to the same display.
            //
            // Plan history stopped producing a weekly reset of its own under ADR-0014, so
            // without this the 20-second cadence publishes a snapshot with no weekly time and
            // the 60-second one puts it back: the reset blinks out and returns, twice a
            // minute. Measured in the field on v0.6.24 — "7d: 13%" alternating with
            // "7d: 13% · resets Mon 22:59" in consecutive tooltips.
            //
            // This is exactly the flicker PublishPlanHistory's own remarks warn about for the
            // percentages, arriving through the reset instead. Two cadences that decorate a
            // snapshot differently will always show the difference as a blink.
            return _weeklyReset.WithReportedResets(_weeklyReset.WithWeeklyReset(snapshot, utcNow), utcNow);
        }
        catch (Exception ex)
        {
            _log?.Write($"plan read FAILED {ex.GetType().Name}: {ex.Message}");
            return UsageSnapshot.None;
        }
    }

    /// <summary>
    /// Publishes a plan-history-only snapshot, and declines to in the two cases where it would
    /// make the display worse than leaving it alone.
    ///
    /// <para><b>Non-authoritative results are dropped, not published.</b> This path skips the
    /// composite provider, so it cannot see the JSONL estimate that would otherwise win when
    /// plan history has nothing. Publishing its <c>None</c> would blank a panel that the full
    /// poll had correctly filled with an estimate, and the display would flicker between the
    /// two cadences.</para>
    ///
    /// <para><b>An older sample never replaces a newer one.</b> Two cadences now publish, and
    /// a first-run full poll can spend seconds ingesting before posting a snapshot it read
    /// before the plan tick did. Without this the bars would visibly jump backwards.</para>
    ///
    /// <para>Threshold notification runs here, which is a genuine gain rather than a side
    /// effect: crossing 70% is now noticed within <see cref="UsageEngineOptions.PlanPollInterval"/>
    /// rather than up to a minute later. Off-plan detection deliberately does not — it needs
    /// the 31-day SQLite aggregate, which is the expensive half this path exists to avoid.</para>
    /// </summary>
    private void PublishPlanHistory(UsageSnapshot snapshot)
    {
        if (!PollingCadence.IsAuthoritative(snapshot.Source) || IsOlderThanLatest(snapshot))
        {
            return;
        }

        _publish(snapshot);
    }

    /// <summary>
    /// Whether this snapshot describes an older sample than the one already published.
    ///
    /// <para><b>Only ever compares two authoritative readings.</b> Both halves of that are
    /// load-bearing. The incoming side has to be checked as well as the current one, because a
    /// JSONL estimate <i>does</i> carry a capture time — the newest transcript activity — so a
    /// live plan reading would otherwise block the fallback the composite resolved to, and
    /// block it permanently: <c>Latest</c> stays Live, so every later estimate is compared
    /// against the same frozen sample and dropped in turn. The panel would sit on a reading
    /// that had stopped being true and never fall back.</para>
    ///
    /// <para>Comparing capture times rather than read times is deliberate. These stamps come
    /// from Claude Desktop's own samples, so the ordering is the data's, not this process's —
    /// which keeps it right across a wall-clock correction that would reorder read times.</para>
    /// </summary>
    public bool IsOlderThanLatest(UsageSnapshot snapshot)
    {
        var latest = _getLatest();
        return PollingCadence.IsAuthoritative(snapshot.Source)
            && PollingCadence.IsAuthoritative(latest.Source)
            && snapshot.CapturedAtUtc is { } incoming
            && latest.CapturedAtUtc is { } current
            && incoming < current;
    }

    /// <summary>
    /// The panel's figures: 31-day rollups plus off-plan divergence for the current window.
    /// </summary>
    public PanelStatistics BuildStatistics()
    {
        var utcNow = _clock.UtcNow;
        var stats = PanelStatistics.Build(_store, utcNow, _displayZone);
        if (_planHistory is null)
        {
            return stats;
        }

        var (windowStart, percents, meterAge) = _planHistory.GetCurrentWindow(utcNow);

        if (_simulateDivergence is { } mode)
        {
            // Feed the real detector synthetic inputs rather than faking its output,
            // so the simulation exercises the same code path the real case would. The meter is
            // stated as current, because a simulation of divergence against a stopped meter
            // would now correctly render nothing at all.
            var fake = mode == "limit" ? new[] { 99, 100 } : [6, 6, 6];
            return stats.WithDivergence(_store, windowStart, fake, TimeSpan.Zero) with
            {
                EstOffPlanUsd = 92.75m,
                Divergence = DivergenceDetector.Evaluate(fake, 69_091, TimeSpan.Zero),
            };
        }

        // The meter as O-view actually knows it, not merely as Desktop last wrote it. Plan
        // history alone leaves the comparison up to 15 minutes behind a reading already on
        // screen — the gap issue #268 was reported through.
        var reported = ReadReportedMeter(utcNow);
        var (series, seriesAge) = MeterSeries.WithReportedReading(
            percents, meterAge, reported.SessionPercent, reported.Age);

        return stats.WithDivergence(_store, windowStart, series, seriesAge);
    }

    /// <summary>
    /// Claude Code's cached five-hour reading and how old it is, or nulls when there is none.
    ///
    /// <para>Read through <see cref="_cachedUtilization"/> rather than the raw file so it
    /// arrives with that provider's two rules already applied — a percentage whose window has
    /// rolled over, and an aged zero, both come back null. It is also the seam a caller who
    /// supplied its own <c>Provider</c> has already blanked, which is what keeps the off-plan
    /// comparison from reaching into the developer's own <c>~/.claude.json</c> during tests
    /// (issue #212, and the reasoning at <see cref="UsageEngine"/>'s plan-history field).</para>
    ///
    /// <para>A reader that throws yields nulls, not an exception: this refines a comparison
    /// that was already correct without it.</para>
    /// </summary>
    private (int? SessionPercent, TimeSpan? Age) ReadReportedMeter(DateTimeOffset utcNow)
    {
        try
        {
            var reported = _cachedUtilization.GetSnapshot(utcNow);
            return reported.SessionPercent is { } percent
                ? (percent, utcNow - reported.CapturedAtUtc)
                : (null, null);
        }
        catch (Exception ex)
        {
            _log?.Write($"reported meter skipped: {ex.GetType().Name}: {ex.Message}");
            return (null, null);
        }
    }
}
