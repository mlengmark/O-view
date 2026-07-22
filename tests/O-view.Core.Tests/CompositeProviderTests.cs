using OView.Core.Models;
using OView.Core.Providers;

namespace OView.Core.Tests;

public class CompositeProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);

    private sealed class Fixed(UsageSnapshot snapshot) : IUsageProvider
    {
        public UsageSnapshot GetSnapshot(DateTimeOffset utcNow) => snapshot;
    }

    private sealed class Throws : IUsageProvider
    {
        public UsageSnapshot GetSnapshot(DateTimeOffset utcNow) =>
            throw new InvalidOperationException("provider blew up (e.g. corrupt store)");
    }

    private static UsageSnapshot Snap(DataSource source, int? session = null) =>
        new(source, session, null, null, Now);

    [Fact]
    public void LiveBeatsEverything()
    {
        var composite = new CompositeUsageProvider(
            new Fixed(Snap(DataSource.Estimate)),
            new Fixed(Snap(DataSource.Live, 47)),
            new Fixed(Snap(DataSource.Stale, 90)));

        var result = composite.GetSnapshot(Now);

        Assert.Equal(DataSource.Live, result.Source);
        Assert.Equal(47, result.SessionPercent);
    }

    [Fact]
    public void StaleAuthoritativeBeatsEstimate()
    {
        // ADR-0002: stale real percentages carry more information than an estimate
        // with no percentages at all. The staleness label makes it honest.
        var composite = new CompositeUsageProvider(
            new Fixed(Snap(DataSource.Estimate)),
            new Fixed(Snap(DataSource.Stale, 31)));

        var result = composite.GetSnapshot(Now);

        Assert.Equal(DataSource.Stale, result.Source);
        Assert.Equal(31, result.SessionPercent);
    }

    [Fact]
    public void EstimateWhenNothingBetter()
    {
        var composite = new CompositeUsageProvider(
            new Fixed(UsageSnapshot.None),
            new Fixed(Snap(DataSource.Estimate)));

        Assert.Equal(DataSource.Estimate, composite.GetSnapshot(Now).Source);
    }

    [Fact]
    public void AllNone_YieldsNone()
    {
        var composite = new CompositeUsageProvider(
            new Fixed(UsageSnapshot.None),
            new Fixed(UsageSnapshot.None));

        Assert.Equal(UsageSnapshot.None, composite.GetSnapshot(Now));
    }

    [Fact]
    public void NoProviders_YieldsNone()
    {
        Assert.Equal(UsageSnapshot.None, new CompositeUsageProvider().GetSnapshot(Now));
    }

    [Fact]
    public void ThrowingProvider_DoesNotBlankTheChain()
    {
        // issue #16: a provider backed by a corrupt store throws; the composite must
        // fall through to the next source rather than propagating and showing "no data".
        var composite = new CompositeUsageProvider(
            new Throws(),
            new Fixed(Snap(DataSource.Live, 47)));

        var result = composite.GetSnapshot(Now);

        Assert.Equal(DataSource.Live, result.Source);
        Assert.Equal(47, result.SessionPercent);
    }

    [Fact]
    public void AllProvidersThrow_YieldsNone()
    {
        var composite = new CompositeUsageProvider(new Throws(), new Throws());

        Assert.Equal(UsageSnapshot.None, composite.GetSnapshot(Now));
    }
}
