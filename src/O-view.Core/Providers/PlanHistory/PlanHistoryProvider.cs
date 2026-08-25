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
    /// <para><b>Measured, not assumed — and re-measured, because Claude Desktop changed it.</b>
    /// The original calibration read: across 1,828 consecutive gaps in a 30-day real file,
    /// median 5.00 min, 92% within a hair of 5 min; 11 minutes then tolerated two missed
    /// samples and a minute of slack. That was true of every Desktop build up to
    /// <b>2026-08-10</b>.</para>
    ///
    /// <para>On that date the cadence changed to <b>15 minutes</b> and has held there since.
    /// Measured on the same machine's file, 1,443 gaps: 1,234 at 5 min (all before 08-10),
    /// then 15 min throughout — 31 of them on 08-23 alone, with nothing at 5 min. Desktop
    /// samples on launch and every 15 minutes it stays active.</para>
    ///
    /// <para><b>Eleven minutes was therefore shorter than the interval itself</b>, which made
    /// "Live" unreachable for four minutes of every fifteen: a reading taken as recently as
    /// Desktop can take one was labelled stale while it was still the newest that could
    /// possibly exist. One interval plus a minute of slack restores the meaning the bound is
    /// supposed to carry — stale means <i>the next sample is overdue</i>, not merely that
    /// Desktop paused between two of them.</para>
    ///
    /// <para>The question this answers is unchanged (issue #161): not "is Desktop alive" but
    /// "does this number still describe the window". Two missed samples at the new cadence
    /// would be 31 minutes, which is far too long to render as a confident reading — so the
    /// tolerance shrinks from two intervals to one as the interval grows.</para>
    /// </summary>
    public static readonly TimeSpan DefaultFreshness = TimeSpan.FromMinutes(16);

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
    /// <para>Six minutes, and <b>deliberately not raised alongside
    /// <see cref="DefaultFreshness"/></b> when Desktop's cadence went from 5 minutes to 15 on
    /// 2026-08-10. Raising it was tried and reverted: at 16 minutes a 14-minute-old zero
    /// renders as a confident empty window again, which is issue #161 verbatim.</para>
    ///
    /// <para>The two bounds answer different questions, and only one of them is about
    /// Desktop's cadence. "Is this snapshot current?" is — it can only ever be as current as
    /// the sampler allows. "Does <i>at least 0%</i> tell the user anything?" is not: fourteen
    /// minutes of Claude Code can move an empty window well into double digits whether or not
    /// Desktop was due to sample, so the reading is worthless at the same age regardless of
    /// why it is that age.</para>
    ///
    /// <para>The cost is real and accepted: after a reset the gauge now reads <i>unknown</i>
    /// for nine minutes out of every fifteen rather than six out of every ten. Unknown is not
    /// a wrong answer — it is the absence of one, which is what rule 6 asks for when the
    /// alternative is a precise-looking figure O-view cannot stand behind.</para>
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

    /// <summary>
    /// Where a swallowed failure inside the weekly-reset forecast is recorded. A delegate for
    /// the same reason <see cref="_earliestActivity"/> is one — this class knows nothing about
    /// the app's logging and stays testable without it.
    /// </summary>
    public Action<string>? Log { get; init; }

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
    /// <returns>
    /// The window's start, its meter samples, and <b>how old the newest of them is</b>. The age
    /// travels with the series because the two are only meaningful together: Claude Desktop
    /// samples while it runs and not at all otherwise, so a flat series says nothing until you
    /// know whether it is still being written (<see cref="DivergenceDetector.MaxMeterAge"/>).
    /// </returns>
    public (DateTimeOffset WindowStartUtc, IReadOnlyList<int> Percents, TimeSpan MeterAge)
        GetCurrentWindow(DateTimeOffset utcNow)
    {
        var samples = ReadSamples();
        if (samples.Count == 0)
        {
            return (utcNow, [], TimeSpan.MaxValue);
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

        // Measured against the newest sample in the FILE, not in the window: a window whose
        // samples all predate a long silence is exactly the case being guarded against, and
        // taking the age from inside it would report the silence as freshness.
        var age = utcNow - samples[^1].AtUtc;

        return (windowStart, inWindow.Select(s => s.FiveHourPercent).ToList(),
            age > TimeSpan.Zero ? age : TimeSpan.Zero);
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
        catch (Exception)
        {
            // A precision refinement must never take down the reading it refines — and the
            // filter that used to sit here, `when (ex is IOException or
            // InvalidOperationException)`, did not deliver that. In production the delegate
            // is RollupStore.EarliestRequestBetween, so what it actually throws is
            // SqliteException, which is neither. That escaped GetSnapshot and blanked the
            // plan-history percentages — figures that do not depend on the store at all —
            // turning a rebuildable cache's failure into a missing session gauge.
            //
            // Do not narrow this again. The delegate is injected, so this provider cannot
            // know what it throws, and the only correct answer to any of it is the bracket
            // that was already correct before the refinement was attempted.
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
    /// <summary>
    /// The weekly reset the user entered, when they have entered one. Set by the head from
    /// settings; null means derive it (GitHub issue #186).
    /// </summary>
    public ManualWeeklyReset? ManualWeeklyReset { get; set; }

    /// <summary>
    /// The observation that disproved the entered reset, if one has. Read by the head so it
    /// can tell the user once — a wrong entry that is silently overridden is worse than no
    /// entry, because they go on believing the number they typed.
    /// </summary>
    public WeeklyResetObservation? ManualWeeklyResetConflict { get; private set; }

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
            var observations = _weeklyResetLog.GetObservations(samples[^1].OrgUuid);
            var derived = WeeklyResetDetector.PredictNextReset(observations, utcNow);

            return ResolveWeeklyReset(derived, observations, utcNow);
        }
        catch (Exception ex)
        {
            // The weekly-reset log is one part of the panel; a failure inside it — an
            // unreadable file, a locked directory — must never take down the plan-history
            // percentages, which do not depend on it. Degrade to unknown and carry on.
            //
            // Named rather than swallowed outright, for the reason CompositeUsageProvider's
            // own catch now is: "the weekly reset is unknown" and "the weekly reset threw on
            // every poll for a week" are the same blank on screen and want different fixes.
            Log?.Invoke($"weekly reset forecast FAILED {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Chooses between what the user entered and what O-view derived (GitHub issue #186).
    ///
    /// <para><b>The entry wins over inference, and loses to evidence.</b> Anthropic assigns
    /// the weekly reset as a fixed time the user can read directly, so an entered value comes
    /// from the authoritative source while the derived one is inferred from a ~10-hour
    /// sampling gap. That justifies precedence — but not immunity.</para>
    ///
    /// <para>An observed bracket is proof of <i>within what</i> the reset fell. If the entered
    /// boundary lies outside every such bracket, the two cannot both be true, and continuing
    /// to show the entry would be displaying a number O-view has evidence against — the exact
    /// thing rule 6 forbids. So a contradiction hands the answer back to the derived value
    /// <b>and</b> is recorded for the head to surface. Flagging while still showing the
    /// disproven number would be the worst of both.</para>
    ///
    /// <para>The most recent observation decides. An older one can legitimately disagree
    /// because the account's schedule changed — which is the case where the newest evidence is
    /// the only relevant evidence.</para>
    /// </summary>
    private WeeklyResetForecast? ResolveWeeklyReset(
        WeeklyResetForecast? derived,
        IReadOnlyList<WeeklyResetObservation> observations,
        DateTimeOffset utcNow)
    {
        ManualWeeklyResetConflict = null;

        if (ManualWeeklyReset is not { } manual)
        {
            return derived;
        }

        var local = TimeZoneInfo.Local;
        var newest = observations.Count > 0
            ? observations.OrderByDescending(o => o.LatestUtc).First()
            : null;

        if (newest is not null && manual.IsContradictedBy(newest, local))
        {
            ManualWeeklyResetConflict = newest;
            return derived;
        }

        // Zero uncertainty: the user read this off Claude, so it is not an approximation and
        // must not wear the "~" that marks one.
        return new WeeklyResetForecast(
            manual.NextAfter(utcNow, local), TimeSpan.Zero, WeeklyResetDetector.WindowLength);
    }
}
