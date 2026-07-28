namespace OView.Core.Providers.PlanHistory;

/// <summary>
/// One observed weekly reset. The reset itself is an instant, but O-view only ever sees
/// it between two samples, so what is actually known is the INTERVAL
/// <c>(EarliestUtc, LatestUtc]</c> that contains it. Recording that interval rather than
/// a single instant is the whole reason the weekly reset can be derived at all: weekly
/// resets land in the middle of the night for many users, Claude Desktop is closed then,
/// and the sample either side of the drop can be ten hours apart.
/// </summary>
/// <param name="EarliestUtc">Last sample still showing the old (higher) value.</param>
/// <param name="LatestUtc">First sample showing the reset value — the reset had happened by here.</param>
/// <param name="OrgUuid">Organization the samples belonged to. Windows are per-org.</param>
public sealed record WeeklyResetObservation(
    DateTimeOffset EarliestUtc,
    DateTimeOffset LatestUtc,
    string OrgUuid)
{
    /// <summary>How wide the bracket is — 0 would mean the exact instant was captured.</summary>
    public TimeSpan Uncertainty => LatestUtc - EarliestUtc;

    /// <summary>
    /// True when the reset was caught while Desktop was sampling normally, so the bracket
    /// is one sampling interval wide. Drives whether the UI qualifies the time it shows.
    /// </summary>
    public bool IsPrecise => Uncertainty <= WeeklyResetDetector.PreciseBracket;
}

/// <summary>
/// A predicted weekly reset, carrying the precision of the observation it was derived
/// from. <see cref="AtUtc"/> is an upper bound — the reset happens at or before it —
/// which is the safe direction for a quota display: it never promises fresh quota
/// earlier than it actually arrives.
/// </summary>
public sealed record WeeklyResetForecast(DateTimeOffset AtUtc, TimeSpan Uncertainty)
{
    public bool IsPrecise => Uncertainty <= WeeklyResetDetector.PreciseBracket;
}

/// <summary>
/// Derives the weekly (7-day) reset from `sd` drops — the same idea as
/// <see cref="ResetDetector"/> for the 5-hour window, and now the same shape: a measured
/// window length, anchored on an observed drop and stepped forward.
///
/// <para><b>Superseded design (see ADR-0011).</b> The first implementation refused to
/// predict until it had measured the period from two resets, and rejected any drop seen
/// across a gap in sampling as a suspected "restart snap". On eight days of real
/// plan-history that combination detected <em>nothing</em>: both weekly resets on the dev
/// machine landed at ~06:20 UTC while Desktop was closed overnight, so both were
/// discarded, and the panel therefore never showed a weekly reset at all. Two things
/// changed:</para>
///
/// <list type="bullet">
/// <item><b>The period is 7 days, measured.</b> Resets observed 2026-07-21 06:14:55Z and
/// 2026-07-28 06:28:57Z — 7 d 0 h 14 m apart, and the 14 m sits entirely inside the
/// sampling gaps that bracket them. The rival 72-hour hypothesis is disproved by the same
/// file: `sd` climbed 2 → 70 monotonically across those seven days, including continuous
/// sampling through 2026-07-24 06:00–12:00Z where a 72-hour window would have reset. One
/// observation is therefore enough to predict, exactly as one `fh` drop is.</item>
/// <item><b>A drop across a gap is a real reset, just an imprecise one.</b> While Desktop
/// is closed it writes nothing; the first sample after it reopens is a fresh fetch, so a
/// LOWER value there means quota was genuinely restored. What the gap costs is precision,
/// not trust — so the observation is bracketed rather than discarded.</item>
/// </list>
///
/// <para>Two things are still true from the original design and must stay: observations
/// are <b>persisted</b> (a reset is unrecoverable once it scrolls out of the file), and
/// before any reset has been observed the next one is genuinely <b>unknown</b> and is
/// reported as null — never guessed (CLAUDE.md rule 6).</para>
/// </summary>
public static class WeeklyResetDetector
{
    /// <summary>
    /// The window length. Measured, not assumed — see the class remarks for the two
    /// observations and for how the 72-hour alternative was ruled out.
    /// </summary>
    public static readonly TimeSpan WindowLength = TimeSpan.FromDays(7);

    /// <summary>
    /// Minimum decrease in `sd` that counts as a reset. Guards against noise; a real reset
    /// need not land on 0, because usage can resume in the fresh window immediately.
    /// </summary>
    public const int DropThreshold = 2;

    /// <summary>
    /// Bracket width at or below which an observation counts as precise — ~3× Desktop's
    /// 300 s sampling cadence, so an ordinary in-cadence catch qualifies and a
    /// Desktop-was-closed catch does not.
    /// </summary>
    public static readonly TimeSpan PreciseBracket = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Slack allowed when checking a measured interval against <see cref="WindowLength"/>,
    /// on top of the two observations' own uncertainty. Covers sampling jitter and the
    /// integer-percent quantisation of `sd`.
    /// </summary>
    public static readonly TimeSpan PeriodTolerance = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Every `sd` drop in one sample series, each bracketed by the samples either side of
    /// it. Samples must be ordered by time and belong to a single org.
    /// </summary>
    public static IReadOnlyList<WeeklyResetObservation> FindResets(IReadOnlyList<PlanHistorySample> samples)
    {
        var resets = new List<WeeklyResetObservation>();
        for (var i = 1; i < samples.Count; i++)
        {
            if (samples[i - 1].SevenDayPercent - samples[i].SevenDayPercent >= DropThreshold)
            {
                resets.Add(new WeeklyResetObservation(
                    samples[i - 1].AtUtc, samples[i].AtUtc, samples[i].OrgUuid));
            }
        }
        return resets;
    }

    /// <summary>
    /// Predict the next weekly reset strictly after <paramref name="utcNow"/>, or null when
    /// no reset has ever been observed — the reset time is then genuinely unknown and the
    /// UI says so rather than guessing.
    ///
    /// <para>The anchor is the <b>most precise</b> observation, not the most recent. Because
    /// the period is exact, a tightly bracketed reset from three weeks ago projects forward
    /// more accurately than a ten-hour bracket from yesterday, and stepping it forward by
    /// whole periods costs nothing.</para>
    /// </summary>
    public static WeeklyResetForecast? PredictNextReset(
        IReadOnlyList<WeeklyResetObservation> observations, DateTimeOffset utcNow)
    {
        if (observations.Count == 0)
        {
            return null;
        }

        var anchor = observations
            .OrderBy(o => o.Uncertainty)
            .ThenByDescending(o => o.LatestUtc)
            .First();

        var period = MeasurePeriod(observations);
        var next = anchor.LatestUtc + period;
        if (next <= utcNow)
        {
            // Idle stretches can span several windows; roll forward without looping.
            var missed = (long)Math.Floor((utcNow - next) / period) + 1;
            next += period * missed;
        }

        return new WeeklyResetForecast(next, anchor.Uncertainty);
    }

    /// <summary>
    /// The period to step by: <see cref="WindowLength"/> unless two precise observations
    /// say otherwise.
    ///
    /// <para>The constant is measured on one machine and one plan, so it is checked rather
    /// than trusted blindly: if the two most recent <em>precise</em> observations are not a
    /// whole number of 7-day windows apart (within their own uncertainty), the measured
    /// interval wins. Imprecise observations are never used to measure — a ten-hour bracket
    /// cannot distinguish 7 d from 7 d 10 h, and letting it try would replace a correct
    /// constant with noise.</para>
    /// </summary>
    private static TimeSpan MeasurePeriod(IReadOnlyList<WeeklyResetObservation> observations)
    {
        var precise = observations.Where(o => o.IsPrecise).OrderBy(o => o.LatestUtc).ToList();
        if (precise.Count < 2)
        {
            return WindowLength;
        }

        var interval = precise[^1].LatestUtc - precise[^2].LatestUtc;
        if (interval <= TimeSpan.Zero)
        {
            return WindowLength;
        }

        // A missed reset makes the interval a multiple of the period, which is consistent,
        // not contradictory — so compare against the nearest whole number of windows.
        var windows = Math.Max(1, Math.Round(interval / WindowLength));
        var slack = precise[^1].Uncertainty + precise[^2].Uncertainty + PeriodTolerance;
        var drift = (interval - WindowLength * windows).Duration();

        return drift <= slack ? WindowLength : interval;
    }
}

/// <summary>
/// Persists observed weekly resets across runs. Retention of the source file is finite and
/// a reset that scrolls out of it is gone for good, so this is the one piece of O-view
/// state that cannot be rebuilt from anything else — which is why it lives in its own file
/// rather than in the rebuildable rollup store (ADR-0011). Recording is idempotent:
/// re-observing the same reset merges into the existing record and can only tighten it.
/// </summary>
public interface IWeeklyResetLog
{
    void Record(IEnumerable<WeeklyResetObservation> observations);

    /// <param name="orgUuid">Restrict to one organization; null returns everything.</param>
    IReadOnlyList<WeeklyResetObservation> GetObservations(string? orgUuid = null);
}
