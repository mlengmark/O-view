using OView.Core.Models;

namespace OView.Core.Providers;

/// <summary>
/// Resolves the provider chain (ADR-0002 precedence, amended by ADR-0007):
/// PlanHistoryProvider → (OAuth, deferred) → JsonlUsageProvider. Selection is by
/// information value, not list position: any Live snapshot beats any Stale one, and
/// stale authoritative percentages beat an estimate that has no percentages at all.
/// The winning snapshot keeps its own Source so the UI labels it honestly.
/// </summary>
public sealed class CompositeUsageProvider : IUsageProvider
{
    private readonly IReadOnlyList<IUsageProvider> _providers;

    /// <param name="providers">In precedence order; earlier wins within the same tier.</param>
    public CompositeUsageProvider(params IUsageProvider[] providers)
    {
        _providers = providers;
    }

    public UsageSnapshot GetSnapshot(DateTimeOffset utcNow)
    {
        var snapshots = _providers.Select(p => p.GetSnapshot(utcNow)).ToList();

        foreach (var tier in new[] { DataSource.Live, DataSource.Stale, DataSource.Estimate })
        {
            if (snapshots.FirstOrDefault(s => s.Source == tier) is { } snapshot)
            {
                return snapshot;
            }
        }

        return UsageSnapshot.None;
    }
}
