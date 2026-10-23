using OView.Core.Models;
using OView.Core.Providers;

namespace OView.Core.Tests;

public class CompositeProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A provider backed by a corrupt store: it throws, and the composite must carry on.</summary>
    private static ThrowingProvider Throws() =>
        new(new InvalidOperationException("provider blew up (e.g. corrupt store)"));

    private static UsageSnapshot Snap(DataSource source, int? session = null) =>
        new(
            source,
            UsageSnapshot.Percent(session),
            UsageSnapshot.Percent(null),
            UsageSnapshot.Instant(null),
            UsageSnapshot.Instant(Now),
            UsageSnapshot.Instant(null));

    [Fact]
    public void LiveBeatsEverything()
    {
        var composite = new CompositeUsageProvider(
            new FixedProvider(Snap(DataSource.Estimate)),
            new FixedProvider(Snap(DataSource.Live, 47)),
            new FixedProvider(Snap(DataSource.Stale, 90)));

        var result = composite.GetSnapshot(Now);

        Assert.Equal(DataSource.Live, result.Source);
        Assert.Equal(47, result.SessionPercentValue.Value);
    }

    [Fact]
    public void StaleAuthoritativeBeatsEstimate()
    {
        // ADR-0002: stale real percentages carry more information than an estimate
        // with no percentages at all. The staleness label makes it honest.
        var composite = new CompositeUsageProvider(
            new FixedProvider(Snap(DataSource.Estimate)),
            new FixedProvider(Snap(DataSource.Stale, 31)));

        var result = composite.GetSnapshot(Now);

        Assert.Equal(DataSource.Stale, result.Source);
        Assert.Equal(31, result.SessionPercentValue.Value);
    }

    [Fact]
    public void EstimateWhenNothingBetter()
    {
        var composite = new CompositeUsageProvider(
            new FixedProvider(UsageSnapshot.None),
            new FixedProvider(Snap(DataSource.Estimate)));

        Assert.Equal(DataSource.Estimate, composite.GetSnapshot(Now).Source);
    }

    [Fact]
    public void AllNone_YieldsNone()
    {
        var composite = new CompositeUsageProvider(
            new FixedProvider(UsageSnapshot.None),
            new FixedProvider(UsageSnapshot.None));

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
            Throws(),
            new FixedProvider(Snap(DataSource.Live, 47)));

        var result = composite.GetSnapshot(Now);

        Assert.Equal(DataSource.Live, result.Source);
        Assert.Equal(47, result.SessionPercentValue.Value);
    }

    [Fact]
    public void AllProvidersThrow_YieldsNone()
    {
        var composite = new CompositeUsageProvider(Throws(), Throws());

        Assert.Equal(UsageSnapshot.None, composite.GetSnapshot(Now));
    }

    /// <summary>
    /// The composite picks a snapshot whole and returns it unchanged (see the class summary
    /// on <see cref="CompositeUsageProvider"/>) — so the status beside each figure survives
    /// selection rather than being re-derived or dropped on the way through.
    /// </summary>
    [Fact]
    public void TypedValues_SurviveSelectionUnchanged()
    {
        var withStatus = Snap(DataSource.Live) with
        {
            SessionPercentValue = new(47, UsageValueStatus.Estimated),
        };
        var composite = new CompositeUsageProvider(new FixedProvider(withStatus));

        var result = composite.GetSnapshot(Now);

        Assert.Equal(UsageValueStatus.Estimated, result.SessionPercentValue.Status);
        Assert.Equal(47, result.SessionPercentValue.Value);
    }

    /// <summary>
    /// A snapshot built the ordinary, positional way carries both the figure and the reason it
    /// is there on every reading, and a figure that was never read says so. This used to be a
    /// test that the typed members agreed with their plain twins — the twins defaulted
    /// independently, so a positionally built snapshot claimed Real with no value on every one
    /// of them. There are no twins left to disagree; what is left to pin is that the pairing
    /// the positional arguments asked for is the pairing the snapshot holds.
    /// </summary>
    [Fact]
    public void PositionallyBuiltSnapshot_PairsEveryFigureWithItsReason()
    {
        var snapshot = new UsageSnapshot(
            DataSource.Live,
            UsageSnapshot.Percent(25),
            UsageSnapshot.Percent(3),
            UsageSnapshot.Instant(Now.AddHours(3)),
            UsageSnapshot.Instant(Now),
            UsageSnapshot.Instant(null));

        Assert.Equal(new UsagePercent(25, UsageValueStatus.Real), snapshot.SessionPercentValue);
        Assert.Equal(new UsagePercent(3, UsageValueStatus.Real), snapshot.WeeklyPercentValue);
        Assert.Equal(
            new UsageInstant(Now.AddHours(3), UsageValueStatus.Real), snapshot.SessionResetAtUtcValue);
        Assert.Equal(new UsageInstant(Now, UsageValueStatus.Real), snapshot.CapturedAtUtcValue);

        // The reading nothing produced: unavailable, and carrying no figure to read as one.
        Assert.Equal(UsageValueStatus.Unavailable, snapshot.WeeklyResetAtUtcValue.Status);
        Assert.Null(snapshot.WeeklyResetAtUtcValue.Value);

        // A `with` replaces the pair, so a changed figure cannot be left beside the status of
        // the one it replaced.
        var changed = snapshot with
        {
            SessionPercentValue = UsageSnapshot.Percent(91),
            WeeklyPercentValue = UsageSnapshot.Percent(80),
        };

        Assert.Equal(new UsagePercent(91, UsageValueStatus.Real), changed.SessionPercentValue);
        Assert.Equal(new UsagePercent(80, UsageValueStatus.Real), changed.WeeklyPercentValue);
    }

    /// <summary>
    /// Choosing between two sources that report the same meters — Claude Desktop's sampled
    /// series and Claude Code's cached figures. Before these, the tier was the whole rule and
    /// list position quietly decided the rest; a monitoring tool must prefer the most accurate
    /// reading it has, and fall back to the less reliable one only when it must.
    /// </summary>
    public class WithinATier
    {

        private static UsageSnapshot Snap(
            int? session, int? weekly, TimeSpan age, DataSource source = DataSource.Live) =>
            new(
                source,
                UsageSnapshot.Percent(session),
                UsageSnapshot.Percent(weekly),
                UsageSnapshot.Instant(null),
                UsageSnapshot.Instant(Now - age),
                UsageSnapshot.Instant(null));

        /// <summary>
        /// Both sources cache the same upstream meter, so between equally complete readings the
        /// later one is closer to the truth. Desktop samples every ~5 minutes while Claude Code
        /// refreshes on use, so the fresher one is routinely the second in the list.
        /// </summary>
        [Fact]
        public void TheMoreRecentlyCapturedReadingWins()
        {
            var composite = new CompositeUsageProvider(
                new FixedProvider(Snap(81, 78, TimeSpan.FromMinutes(11))),
                new FixedProvider(Snap(91, 79, TimeSpan.FromMinutes(1))));

            var result = composite.GetSnapshot(Now);

            Assert.Equal(91, result.SessionPercentValue.Value);
            Assert.Equal(79, result.WeeklyPercentValue.Value);
        }

        /// <summary>...and it wins from either position, or it is list order wearing a disguise.</summary>
        [Fact]
        public void TheMoreRecentReadingWinsFromEitherPosition()
        {
            var composite = new CompositeUsageProvider(
                new FixedProvider(Snap(91, 79, TimeSpan.FromMinutes(1))),
                new FixedProvider(Snap(81, 78, TimeSpan.FromMinutes(11))));

            Assert.Equal(91, composite.GetSnapshot(Now).SessionPercentValue.Value);
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
                new FixedProvider(Snap(null, 78, TimeSpan.FromMinutes(1))),
                new FixedProvider(Snap(40, 77, TimeSpan.FromMinutes(9))));

            var result = composite.GetSnapshot(Now);

            Assert.Equal(40, result.SessionPercentValue.Value);
            Assert.Equal(77, result.WeeklyPercentValue.Value);
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
                new FixedProvider(Snap(91, 79, TimeSpan.Zero, DataSource.Stale)),
                new FixedProvider(Snap(40, 60, TimeSpan.FromMinutes(9))));

            var result = composite.GetSnapshot(Now);

            Assert.Equal(DataSource.Live, result.Source);
            Assert.Equal(40, result.SessionPercentValue.Value);
        }

        /// <summary>Equally complete and equally recent: deterministic, and the order given decides.</summary>
        [Fact]
        public void AnUndecidablePairKeepsTheOrderTheProvidersWerePassedIn()
        {
            var composite = new CompositeUsageProvider(
                new FixedProvider(Snap(40, 60, TimeSpan.FromMinutes(3))),
                new FixedProvider(Snap(91, 79, TimeSpan.FromMinutes(3))));

            Assert.Equal(40, composite.GetSnapshot(Now).SessionPercentValue.Value);
        }

        /// <summary>
        /// An undated snapshot cannot be shown to be newer, so it does not displace one that is
        /// dated. It can still win on carrying more meters, which is decided first.
        /// </summary>
        [Fact]
        public void AnUndatedSnapshotDoesNotDisplaceADatedOne()
        {
            var composite = new CompositeUsageProvider(
                new FixedProvider(Snap(40, 60, TimeSpan.FromMinutes(9))),
                new FixedProvider(new UsageSnapshot(
                    DataSource.Live,
                    UsageSnapshot.Percent(91),
                    UsageSnapshot.Percent(79),
                    UsageSnapshot.Instant(null),
                    UsageSnapshot.Instant(null),
                    UsageSnapshot.Instant(null))));

            Assert.Equal(40, composite.GetSnapshot(Now).SessionPercentValue.Value);
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
                new FixedProvider(Snap(40, null, TimeSpan.FromMinutes(9))),
                new FixedProvider(Snap(91, 79, TimeSpan.FromMinutes(1))));

            var result = composite.GetSnapshot(Now);

            Assert.Equal(91, result.SessionPercentValue.Value);
            Assert.Equal(79, result.WeeklyPercentValue.Value);
            Assert.Equal(Now.AddMinutes(-1), result.CapturedAtUtcValue.Value);
        }
    }
}
