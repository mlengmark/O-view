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
    /// <summary>Steady-state poll interval. 60 s by default (build-plan Phase 3).</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Fast retry used while warming up before Claude Desktop has produced data. Never
    /// allowed to exceed <see cref="PollInterval"/> — a sub-3 s diagnostic
    /// <c>--interval-ms</c> would otherwise make "warming up" slower than steady state.
    /// </summary>
    public TimeSpan WarmupInterval { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>First update check, deliberately after launch so it neither slows startup nor races the first refresh (ADR-0009).</summary>
    public TimeSpan FirstUpdateCheckDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Update re-check cadence after the first.</summary>
    public TimeSpan UpdateCheckInterval { get; init; } = TimeSpan.FromHours(24);

    public IClock Clock { get; init; } = SystemClock.Instance;

    public IAppLog? Log { get; init; }

    /// <summary>Storage locations. Null means the real one.</summary>
    public string? RollupDbPath { get; init; }

    public string? WeeklyResetLogPath { get; init; }

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
}
