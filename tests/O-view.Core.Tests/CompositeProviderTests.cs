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

    /// <summary>
    /// Choosing between two sources that report the same meters — Claude Desktop's sampled
    /// series and Claude Code's cached figures. Before these, the tier was the whole rule and
    /// list position quietly decided the rest; a monitoring tool must prefer the most accurate
    /// reading it has, and fall back to the less reliable one only when it must.
    /// </summary>
    public class WithinATier
    {
        private static readonly DateTimeOffset Now = new(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);

        private sealed class Fixed(UsageSnapshot snapshot) : IUsageProvider
        {
            public UsageSnapshot GetSnapshot(DateTimeOffset utcNow) => snapshot;
        }

        private static UsageSnapshot Snap(
            int? session, int? weekly, TimeSpan age, DataSource source = DataSource.Live) =>
            new(source, session, weekly, null, Now - age);

        /// <summary>
        /// Both sources cache the same upstream meter, so between equally complete readings the
        /// later one is closer to the truth. Desktop samples every ~5 minutes while Claude Code
        /// refreshes on use, so the fresher one is routinely the second in the list.
        /// </summary>
        [Fact]
        public void TheMoreRecentlyCapturedReadingWins()
        {
            var composite = new CompositeUsageProvider(
                new Fixed(Snap(81, 78, TimeSpan.FromMinutes(11))),
                new Fixed(Snap(91, 79, TimeSpan.FromMinutes(1))));

            var result = composite.GetSnapshot(Now);

            Assert.Equal(91, result.SessionPercent);
            Assert.Equal(79, result.WeeklyPercent);
        }

        /// <summary>...and it wins from either position, or it is list order wearing a disguise.</summary>
        [Fact]
        public void TheMoreRecentReadingWinsFromEitherPosition()
        {
            var composite = new CompositeUsageProvider(
                new Fixed(Snap(91, 79, TimeSpan.FromMinutes(1))),
                new Fixed(Snap(81, 78, TimeSpan.FromMinutes(11))));

            Assert.Equal(91, composite.GetSnapshot(Now).SessionPercent);
        }

        /// <summary>
        /// A fuller reading beats a fresher one. Blanking a bar to make the other a few minutes
        /// fresher costs the user a whole figure to gain precision they cannot see — and
        /// percentages do go missing for real reasons, an aged zero being discarded rather than
        /// shown in both sources.
        /// </summary>
        [Fact]
        public void ASnapshotCarryingBothMetersBeatsAFresherOneCarryingOne()
        {
            var composite = new CompositeUsageProvider(
                new Fixed(Snap(null, 78, TimeSpan.FromMinutes(1))),
                new Fixed(Snap(40, 77, TimeSpan.FromMinutes(9))));

            var result = composite.GetSnapshot(Now);

            Assert.Equal(40, result.SessionPercent);
            Assert.Equal(77, result.WeeklyPercent);
        }

        /// <summary>
        /// Freshness is compared within a tier, never across one. A Live reading is authoritative
        /// and current; a Stale one only claims to have been true at capture, however recently
        /// that was relative to some other source's sample.
        /// </summary>
        [Fact]
        public void ATierIsStillDecidedBeforeAccuracy()
        {
            var composite = new CompositeUsageProvider(
                new Fixed(Snap(91, 79, TimeSpan.Zero, DataSource.Stale)),
                new Fixed(Snap(40, 60, TimeSpan.FromMinutes(9))));

            var result = composite.GetSnapshot(Now);

            Assert.Equal(DataSource.Live, result.Source);
            Assert.Equal(40, result.SessionPercent);
        }

        /// <summary>Equally complete and equally recent: deterministic, and the order given decides.</summary>
        [Fact]
        public void AnUndecidablePairKeepsTheOrderTheProvidersWerePassedIn()
        {
            var composite = new CompositeUsageProvider(
                new Fixed(Snap(40, 60, TimeSpan.FromMinutes(3))),
                new Fixed(Snap(91, 79, TimeSpan.FromMinutes(3))));

            Assert.Equal(40, composite.GetSnapshot(Now).SessionPercent);
        }

        /// <summary>
        /// An undated snapshot cannot be shown to be newer, so it does not displace one that is
        /// dated. It can still win on carrying more meters, which is decided first.
        /// </summary>
        [Fact]
        public void AnUndatedSnapshotDoesNotDisplaceADatedOne()
        {
            var composite = new CompositeUsageProvider(
                new Fixed(Snap(40, 60, TimeSpan.FromMinutes(9))),
                new Fixed(new UsageSnapshot(DataSource.Live, 91, 79, null, null)));

            Assert.Equal(40, composite.GetSnapshot(Now).SessionPercent);
        }

        /// <summary>
        /// Fields are never mixed across sources. A session figure from one and a weekly from
        /// another would describe an account state that existed at no instant, under a single
        /// source label that could only be true of one of them (rule 6).
        /// </summary>
        [Fact]
        public void TheWinningSnapshotIsReturnedWholeRatherThanMerged()
        {
            var composite = new CompositeUsageProvider(
                new Fixed(Snap(40, null, TimeSpan.FromMinutes(9))),
                new Fixed(Snap(91, 79, TimeSpan.FromMinutes(1))));

            var result = composite.GetSnapshot(Now);

            Assert.Equal(91, result.SessionPercent);
            Assert.Equal(79, result.WeeklyPercent);
            Assert.Equal(Now.AddMinutes(-1), result.CapturedAtUtc);
        }
    }
}
