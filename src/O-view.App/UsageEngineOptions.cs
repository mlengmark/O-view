using OView.Core.Providers;
using OView.Core.Providers.PlanHistory;
using OView.Core.Storage;

namespace OView.App;

/// <summary>
/// Everything <see cref="UsageEngine"/> needs that it does not own outright.
///
/// <para>Every path is nullable and defaults to the real location, which is the same shape
/// <see cref="RollupStore"/>, <see cref="WeeklyResetLog"/> and <see cref="Core.Models.TraySettings"/>
/// already use — so a test can point the whole engine at a temp directory without a
/// filesystem mock, and production passes nothing.</para>
/// </summary>
public sealed record UsageEngineOptions
{
    /// <summary>
    /// Steady-state interval for the <b>full</b> poll — the one that walks the transcripts and
    /// rebuilds the 31-day figures. 60 s by default (build-plan Phase 3).
    /// </summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Interval for the plan-history read alone — the session and weekly percentages the icon,
    /// the tooltip and the bars are drawn from.
    ///
    /// <para><b>Why this is not <see cref="PollInterval"/>.</b> The two reads a poll performs
    /// cost three orders of magnitude apart: measured on a real machine, the plan-history read,
    /// parse and reset-scan is <b>3.3 ms</b>, while the transcript walk behind the token tiles
    /// covers <b>32 files and 92 MB</b>. Sharing one timer meant the numbers on the icon waited
    /// behind the ingest that froze the app on a large history (issue #125).</para>
    ///
    /// <para><b>Why 20 s and not less.</b> Claude Desktop writes that file every ~5 minutes —
    /// measured median 5.00 min across 1,828 consecutive gaps in a 30-day file — and no poll
    /// rate can beat its source. Twenty seconds removes O-view's own contribution to staleness
    /// without re-reading an unchanged file for nothing; going lower buys accuracy that does
    /// not exist.</para>
    /// </summary>
    public TimeSpan PlanPollInterval { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Fast retry used while warming up before Claude Desktop has produced data. Never
    /// allowed to exceed <see cref="PollInterval"/> — a sub-3 s diagnostic
    /// <c>--interval-ms</c> would otherwise make "warming up" slower than steady state.
    /// </summary>
    public TimeSpan WarmupInterval { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>First update check, deliberately after launch so it neither slows startup nor races the first refresh (ADR-0009).</summary>
    public TimeSpan FirstUpdateCheckDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Update re-check cadence after the first, before jitter (<see cref="UpdateSchedule"/>).
    ///
    /// <para><b>Six hours, not twenty-four and not minutes</b> (ADR-0009 as amended
    /// 2026-08-23). Twenty-four left an app designed to run for days waiting up to a day to
    /// notice a release, which is the gap that prompted the change. Minutes would spend a
    /// resource that is not this instance's to spend: GitHub allows an unauthenticated caller
    /// 60 requests per hour <b>per IP</b>, so a ten-minute cadence has ten copies behind one
    /// NAT consuming the whole budget for that address — and conditional requests buy no
    /// exemption without an <c>Authorization</c> header, which rule 3 forbids this app from
    /// holding.</para>
    ///
    /// <para>Six hours is four requests a day per instance, and caps the worst case at six
    /// hours instead of twenty-four.</para>
    /// </summary>
    public TimeSpan UpdateCheckInterval { get; init; } = TimeSpan.FromHours(6);

    /// <summary>
    /// Randomness source for the cadence jitter. Injectable so a test can pin the interval
    /// rather than assert against a range that a bad implementation would also satisfy.
    /// </summary>
    public Random UpdateJitter { get; init; } = Random.Shared;

    public IClock Clock { get; init; } = SystemClock.Instance;

    /// <summary>
    /// The timezone the panel's daily figures are measured in — the reader's own, so "today"
    /// means their today (issue #211).
    ///
    /// <para>Injectable for the same reason <see cref="Clock"/> is: a day-boundary assertion
    /// that reads the machine's zone passes or fails by where CI happens to be, and passes
    /// vacuously on a runner sitting in UTC. The pair is what pins a case — a fixed clock in a
    /// zone that moves under it says nothing.</para>
    /// </summary>
    public TimeZoneInfo DisplayZone { get; init; } = TimeZoneInfo.Local;

    public IAppLog? Log { get; init; }

    /// <summary>Storage locations. Null means the real one.</summary>
    public string? RollupDbPath { get; init; }

    /// <summary>Where the discovered weekly-reset anchor is stored (ADR-0014).</summary>
    public string? WeeklyResetAnchorPath { get; init; }

    /// <summary>
    /// Raw access to Claude Code's cached figures, for harvesting the weekly anchor.
    ///
    /// <para>Separate from <see cref="CachedUtilization"/> — that provider deliberately drops a
    /// <c>resets_at</c> once it has passed, which is exactly the value the anchor is made of.
    /// Null defaults to reading the real file, and only when no <see cref="Provider"/> was
    /// injected: a test that describes its own world must not have this reach past it into the
    /// developer's own <c>~/.claude.json</c>.</para>
    /// </summary>
    public Func<OView.Core.Providers.CachedUsage.CachedUtilization?>? CachedUtilizationSource { get; init; }

    public string? SettingsPath { get; init; }

    public string? PlanHistoryPath { get; init; }

    /// <summary>
    /// Verification hook (<c>--simulate-divergence</c>): feeds the real
    /// <see cref="Core.Models.DivergenceDetector"/> synthetic inputs rather than faking its
    /// output, so the simulation exercises the same code path the real case would.
    /// Off-plan rendering cannot be produced on demand from real data, and this whole
    /// feature exists because a UI failed to communicate something expensive — so the UI
    /// itself needs verifying.
    /// </summary>
    public string? SimulateDivergence { get; init; }

    /// <summary>
    /// Pre-composed provider, for tests. Production leaves this null and the engine builds
    /// the real chain — plan history first, JSONL as a labelled estimate (ADR-0002/0007).
    /// </summary>
    public IUsageProvider? Provider { get; init; }

    /// <summary>
    /// Pre-built plan-history provider, for tests. Only used when <see cref="Provider"/> is
    /// also supplied; the engine needs it directly for the current-window arithmetic that
    /// divergence detection depends on.
    /// </summary>
    public PlanHistoryProvider? PlanHistory { get; init; }

    /// <summary>
    /// Claude Code's cached usage figures. Supplied directly in tests; production leaves this
    /// null and the engine reads the real <c>.claude.json</c>.
    ///
    /// <para>Held separately from <see cref="Provider"/> even though it is part of the chain,
    /// because the engine also needs it <em>outside</em> the chain: its reset timestamps are
    /// reported rather than derived, so they are folded onto whichever snapshot wins rather
    /// than only counting when this provider is the winner.</para>
    /// </summary>
    public IUsageProvider? CachedUtilization { get; init; }
}
