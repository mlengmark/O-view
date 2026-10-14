using OView.Core.Models;
using OView.Core.Providers;
using OView.Core.Providers.Jsonl;
using OView.Core.Providers.PlanHistory;
using OView.Core.Storage;

namespace OView.App;

/// <summary>
/// The provider chain and the Core-seam wiring behind it: the account read, the plan-history
/// and cached-utilization sources, the JSONL fallback, and the composite that picks between
/// them.
///
/// <para>Split out of <see cref="UsageEngine"/> purely to shorten that file — same wiring,
/// same behaviour, same fallback rules. See the constructor's comments for why each source
/// is built the way it is; nothing here changes what was decided, only where it lives.</para>
/// </summary>
public sealed class UsageProviders
{
    /// <summary>The chosen provider: injected, or the real composite chain (ADR-0002/0007).</summary>
    public IUsageProvider Provider { get; }

    /// <summary>
    /// The plan-history provider, held directly as well as through <see cref="Provider"/> —
    /// the engine needs it outside the chain for the current-window arithmetic that
    /// divergence detection depends on.
    /// </summary>
    public PlanHistoryProvider? PlanHistory { get; }

    /// <summary>Claude Code's own cached figures, held directly for the same reason.</summary>
    public IUsageProvider CachedUtilization { get; }

    /// <summary>
    /// Raw reader for Claude Code's cached figures, used only to harvest the weekly anchor.
    /// Deliberately not <see cref="CachedUtilization"/>: that provider drops a
    /// <c>resets_at</c> once it has passed, and a passed instant is precisely what the
    /// anchor is made of.
    /// </summary>
    public Func<OView.Core.Providers.CachedUsage.CachedUtilization?> CachedUtilizationSource { get; }

    /// <summary>
    /// Whether the plan-history provider built here is one the caller actually asked for,
    /// and so may be published from directly — see <see cref="PlanHistoryPoll"/>'s own doc
    /// for why this matters. False in exactly one case: a caller injected a whole
    /// <see cref="IUsageProvider"/> and said nothing about plan history.
    /// </summary>
    public bool PlanHistoryIsAddressed { get; }

    /// <summary>The account read from <see cref="ClaudeDataLocations.AccountFile"/>, for the
    /// weekly-reset anchor's scoping — null when there is none to read.</summary>
    public string? OrganizationUuid { get; }

    public UsageProviders(UsageEngineOptions options, RollupStore store, IAppLog? log)
    {
        var data = options.ClaudeData;
        var account = ClaudeAccount.TryRead(data?.AccountFile);
        OrganizationUuid = account?.OrganizationUuid;

        // Every Core seam below takes the log as a delegate rather than an interface, so the
        // accounting layer keeps knowing nothing about how this app logs. What they all now
        // share is that their failures are recorded instead of merely survived.
        var logLine = log is null ? (Action<string>?)null : log.Write;

        // Deliberately not the CachedUtilization provider: that one drops a resets_at once it
        // has passed, and a passed instant is precisely what the anchor is made of.
        CachedUtilizationSource = options.CachedUtilizationSource
            ?? (options.Provider is null
                ? () => OView.Core.Providers.CachedUsage.CachedUtilization.TryRead(data?.CachedUsageFile)
                : () => null);

        // A caller that supplied its own Provider has described the whole world, so an
        // unnamed plan history reads NOTHING rather than the real machine's file (issue #212).
        //
        // The default used to be PlanHistoryFile.DefaultPath in that case, and the guard
        // against it — PlanHistoryPoll's own addressed flag — covered only *publishing*.
        // UsageAlerts.ReadOffPlan() →
        // BuildStatistics() went through this provider regardless, so the off-plan comparison
        // read the developer's own Claude Desktop usage on every poll in every test that did
        // not name a file. Replayed against that file afterwards: six instants where a test
        // with an empty rollup store would have seen the plan meter pinned at 99% and reported
        // usage billing beyond the plan, all within two hours on 2026-08-25 — which is when
        // eight of them did.
        //
        // Same fix, and the same reasoning, as CachedUtilization gets a few lines below.
        PlanHistory = options.PlanHistory ?? new PlanHistoryProvider(
            path: data?.PlanHistoryFile
                ?? options.PlanHistoryPath
                ?? (options.Provider is null ? null : PlanHistoryFile.NoFile),
            orgUuid: account?.OrganizationUuid,
            // Local request times tighten the five-hour window's start bracket (issue #185).
            // The engine owns both halves, so the wiring lives here rather than teaching the
            // plan-history provider about the rollup store.
            earliestActivity: store.EarliestRequestBetween)
        {
            Log = logLine,
        };

        // Claude Code's own cached figures — real percentages where the JSONL estimate has
        // none, and on a machine without Desktop the only source of the top two bars at all.
        //
        // Position here is a tie-break, not a precedence: the composite picks whichever
        // snapshot reports the most meters, most recently. Desktop samples every ~5 minutes
        // while this refreshes on use, so neither is reliably the fresher one and neither
        // deserves a standing preference.
        //
        // A caller that supplies its own Provider has described the whole world, so the real
        // file is left alone unless it also supplies this. Without that, WithReportedResets
        // would read the developer's own ~/.claude.json during tests and fold live reset times
        // onto fixtures — passing on a CI runner, which has no such file, and failing on the
        // machine of whoever last used Claude Code. Reaching past an injected provider to real
        // user data is the bug, not the test that caught it.
        CachedUtilization = options.CachedUtilization ?? (options.Provider is null
            ? new OView.Core.Providers.CachedUsage.CachedUtilizationProvider(data?.CachedUsageFile)
            : new OView.Core.Providers.CachedUsage.CachedUtilizationProvider(() => null));

        var transcripts = data is null
            ? new JsonlUsageProvider(store) { Log = logLine }
            : new JsonlUsageProvider(store, data.ProjectsRoot, data.CoworkRoots, data.SessionRegistryRoots) { Log = logLine };

        Provider = options.Provider ?? new CompositeUsageProvider(
            PlanHistory,
            CachedUtilization,
            transcripts)
        {
            Log = logLine,
        };

        PlanHistoryIsAddressed = options.Provider is null
            || options.PlanHistory is not null
            || options.PlanHistoryPath is not null;
    }
}
