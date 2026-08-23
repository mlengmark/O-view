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
    /// Maximum age at which a sample is still taken to describe <i>now</i>.
    ///
    /// <para><b>Measured, not assumed.</b> Across 1,828 consecutive gaps in a 30-day real
    /// file: median 5.00 min, and 1,686 of them (92%) within a hair of 5 min. Desktop samples
    /// every ~300 s, so 11 minutes tolerates two missed samples and a minute of slack.</para>
    ///
    /// <para>This was 15 minutes, on the reasoning that three missed samples means Desktop is
    /// not running. That is true, but it is the wrong question: the cost is not "is Desktop
    /// alive", it is "does this number still describe the window". Fifteen minutes let a
    /// 14-minute-old sample render as a confident, unqualified reading (issue #161).</para>
    /// </summary>
    public static readonly TimeSpan DefaultFreshness = TimeSpan.FromMinutes(11);

    /// <summary>
    /// How long a five-hour reading of <b>zero</b> may be trusted — much shorter, because zero
    /// is the one value whose staleness is both most likely and most costly.
    ///
    /// <para><b>Why zero is different.</b> Within a window, utilisation only ever rises. So an
    /// aged sample is a <i>lower bound</i>, never a measurement, and the older it is the weaker
    /// the bound. For a non-zero reading that degrades gracefully — 72% drifting to 75% is
    /// still broadly the truth. For zero it degrades to nothing: "at least 0%" says precisely
    /// nothing about the window, while <i>looking</i> like a precise finding that the window is
    /// empty. It is the reading a user is most likely to act on and most likely to be wrong
    /// about.</para>
    ///
    /// <para>And it is common, not exotic: 587 of 1,829 samples in that same real file read
    /// zero — every window reset produces a run of them. The reported case was exactly this:
    /// the window reset 72 → 0, Desktop sampled at that instant and then went quiet, and 14
    /// minutes later O-view still showed an empty gauge while ~6% had been consumed
    /// (issue #161).</para>
    ///
    /// <para>One sampling interval plus slack. In normal operation a fresh zero arrives every
    /// ~5 minutes and nothing changes; this only bites once Desktop has actually stopped
    /// reporting — which is exactly when O-view genuinely does not know.</para>
    /// </summary>
    public static readonly TimeSpan ZeroReadingFreshness = TimeSpan.FromMinutes(6);

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
    /// <summary>
    /// Earliest local request in an interval, used to tighten the five-hour window's start
    /// bracket (GitHub issue #185). A delegate rather than the store itself: this provider
    /// reads one file and knows nothing about SQLite, and the rule stays unit-testable
    /// without a database. Null disables the narrowing entirely, which is the fallback for
    /// a user whose first use leaves no local transcript.
    /// </summary>
    private readonly Func<DateTimeOffset, DateTimeOffset, DateTimeOffset?>? _earliestActivity;

    public PlanHistoryProvider(string? path = null, string? orgUuid = null, TimeSpan? freshness = null,
        IWeeklyResetLog? weeklyResetLog = null,
        Func<DateTimeOffset, DateTimeOffset, DateTimeOffset?>? earliestActivity = null)
    {
        _path = path ?? PlanHistoryFile.DefaultPath;
        _orgUuid = orgUuid;
        _freshness = freshness ?? DefaultFreshness;
        _weeklyResetLog = weeklyResetLog;
        _earliestActivity = earliestActivity;
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

        // Anchor on the current window's start so the span never crosses a boundary — a
        // reset would show as a large negative rise and mask real divergence.
        //
        // The current window, not merely the last drop: after an idle gap the last drop can
        // be days old, and summing output tokens since then would compare a two-day total
        // against a five-hour meter (issue #180). Falls back to the last drop, and then to
        // the series start, because a divergence window that is too wide is a missed signal
        // where no window at all is a crash.
        var windowStart =
            ResetDetector.FindCurrentWindowStart(samples)?.LatestUtc
            ?? ResetDetector.FindLastDrop(samples)
            ?? samples[0].AtUtc;
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

    /// <summary>
    /// The five-hour reading, or <c>null</c> when it can no longer be trusted to describe now.
    ///
    /// <para>Only zero is discarded, and only once it has aged past
    /// <see cref="ZeroReadingFreshness"/> — see there for why zero is the special case. Unknown
    /// renders as the neutral icon and "usage % unknown", which is the honest statement: O-view
    /// has lost contact and does not know. An empty gauge would be a claim it cannot support,
    /// and is the one a user acts on (CLAUDE.md rule 6).</para>
    ///
    /// <para>The <b>weekly</b> figure is deliberately left alone. The same monotonic argument
    /// applies to it, but its window is 7 days rather than 5 hours, so a sample minutes old
    /// cannot have drifted meaningfully — discarding it would cost information and buy
    /// nothing.</para>
    /// </summary>
    /// <summary>
    /// Pulls a window's upper bound down to the first local request inside it, when there is
    /// one (issue #185).
    ///
    /// <para>Every failure here is a silent no-op, and deliberately: no lookup configured, no
    /// activity in the bracket, or a store that throws mid-query all leave the plan-history
    /// bracket exactly as it was. This only ever <i>improves</i> a figure that is already
    /// correct-but-imprecise, so it must never be able to make one worse — and a user whose
    /// first use was chat or the Desktop app has no transcript to find, which is normal
    /// rather than exceptional.</para>
    /// </summary>
    private SessionWindowStart? NarrowWithLocalActivity(SessionWindowStart? window)
    {
        if (window is not { } bracket || _earliestActivity is null)
        {
            return window;
        }

        try
        {
            return bracket.NarrowedTo(_earliestActivity(bracket.EarliestUtc, bracket.LatestUtc));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // A precision refinement must never take down the reading it refines.
            return bracket;
        }
    }

    private static int? TrustedFiveHourPercent(int? fiveHourPercent, TimeSpan age) =>
        fiveHourPercent is 0 && age > ZeroReadingFreshness ? null : fiveHourPercent;

    public UsageSnapshot GetSnapshot(DateTimeOffset utcNow)
    {
        var samples = ReadSamples();
        if (samples.Count == 0) return UsageSnapshot.None;

        var latest = samples[^1];
        var age = utcNow - latest.AtUtc;
        var source = age <= _freshness ? DataSource.Live : DataSource.Stale;

        // The window that is running now, bracketed by the samples straddling its start —
        // not a grid stepped forward from the last drop, which after an idle gap describes
        // a window that never existed (issue #180).
        //
        // Then tightened by local activity: a request inside the bracket proves the window
        // was already running then, so it is a better-evidenced upper bound than the sample
        // that first noticed the new window (issue #185). Desktop samples every ~15 minutes,
        // which was leaving the forecast systematically about half an interval late.
        var windowStart = NarrowWithLocalActivity(ResetDetector.FindCurrentWindowStart(samples));
        var nextReset = ResetDetector.PredictNextReset(windowStart, utcNow);

        // Weekly reset (issue #6, ADR-0011). This IS the discovery loop: every poll
        // re-scans the whole retained series for `sd` drops and folds them into the
        // persisted log, so a reset is picked up on the first poll after it appears in the
        // file and re-recording an already-known one is a no-op. Prediction then runs off
        // the full history, not just what this file still holds.
        var weeklyReset = ForecastWeeklyReset(samples, utcNow);

        return new UsageSnapshot(
            source,
            TrustedFiveHourPercent(latest.FiveHourPercent, age),
            latest.SevenDayPercent,
            nextReset,
            latest.AtUtc,
            weeklyReset?.AtUtc,
            weeklyReset?.Uncertainty,
            weeklyReset?.Period,
            // How wide the bracket is, so a start inferred across a sampling gap is marked
            // approximate rather than printed to the minute (rule 6).
            windowStart?.Uncertainty);
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
