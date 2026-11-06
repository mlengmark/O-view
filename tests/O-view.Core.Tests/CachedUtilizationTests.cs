using OView.Core.Models;
using OView.Core.Providers.CachedUsage;

namespace OView.Core.Tests;

/// <summary>
/// Claude Code caches the figures behind <c>/status</c> → Usage in <c>~/.claude.json</c>, and
/// they are the only <b>reported</b> session and weekly numbers available locally, percentages
/// and, more valuably, exact reset instants.
///
/// <para>Fixtures follow the real shape, with identifying values scrubbed.</para>
/// </summary>
public class CachedUtilizationTests
{
    /// <summary>
    /// The repository's all-zero placeholder, not a real account uuid, fixtures are scrubbed
    /// (repo convention), and the CI identifier scan allows only this one.
    /// </summary>
    private const string Account = "00000000-0000-0000-0000-000000000000";

    /// <summary>2026-08-24T00:00:00Z, the fetch time every fixture below is stamped with.</summary>
    private static readonly DateTimeOffset Fetched = new(2026, 8, 24, 0, 0, 0, TimeSpan.Zero);

    private static long Ms(DateTimeOffset t) => t.ToUnixTimeMilliseconds();

    /// <summary>
    /// Reported 2026-08-24. Claude Code moved its state to <c>~/.claude/.claude.json</c> and
    /// stopped writing the old file, which kept a block that was hours stale and structurally
    /// indistinguishable from a live one. Taking the first candidate that HAD a block would
    /// have read the abandoned figures for as long as that file existed.
    /// </summary>
    [Fact]
    public void TheFreshestBlockWins_NotTheFirstCandidateThatHasOne()
    {
        var dir = Directory.CreateTempSubdirectory("oview-tests-").FullName;
        try
        {
            var abandoned = Path.Combine(dir, "abandoned.json");
            File.WriteAllText(abandoned, Document(fiveHour: 14, fetchedAt: Fetched.AddHours(-6)));

            var current = Path.Combine(dir, "current.json");
            File.WriteAllText(current, Document(fiveHour: 61, fetchedAt: Fetched));

            // Both orders, because "the freshest" must not be "whichever happens to be listed
            // in the position that used to win".
            Assert.Equal(61, CachedUtilization.TryReadAny([abandoned, current])?.FiveHour?.Percent);
            Assert.Equal(61, CachedUtilization.TryReadAny([current, abandoned])?.FiveHour?.Percent);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// The documented shape, including the sibling bars that read null on a plan with no
    /// separate meter for them, they are present in every real file and must not confuse the
    /// parse.
    /// </summary>
    private static string Document(
        int fiveHour = 91,
        string? fiveHourResets = "2026-08-24T05:00:00.046735+00:00",
        int sevenDay = 79,
        string? sevenDayResets = "2026-08-24T21:00:00.046756+00:00",
        DateTimeOffset? fetchedAt = null) =>
        $$"""
        {
          "numStartups": 12,
          "cachedUsageUtilization": {
            "fetchedAtMs": {{Ms(fetchedAt ?? Fetched)}},
            "accountUuid": "{{Account}}",
            "utilization": {
              "five_hour": {
                "utilization": {{fiveHour}},
                {{Resets(fiveHourResets)}}
                "limit_dollars": null, "used_dollars": null, "remaining_dollars": null
              },
              "seven_day": {
                "utilization": {{sevenDay}},
                {{Resets(sevenDayResets)}}
                "limit_dollars": null, "used_dollars": null, "remaining_dollars": null
              },
              "seven_day_opus": null,
              "seven_day_sonnet": null,
              "seven_day_cowork": null,
              "extra_usage": { "is_enabled": false, "utilization": null }
            }
          }
        }
        """;

    private static string Resets(string? value) =>
        value is null ? string.Empty : $"\"resets_at\": \"{value}\",";

    [Fact]
    public void ItReadsBothMetersAndTheirResetInstants()
    {
        var parsed = CachedUtilization.Parse(Document());

        Assert.NotNull(parsed);
        Assert.Equal(Fetched, parsed.FetchedAtUtc);
        Assert.Equal(Account, parsed.AccountUuid);
        Assert.Equal(91, parsed.FiveHour!.Percent);
        Assert.Equal(79, parsed.SevenDay!.Percent);
        Assert.Equal(new DateTimeOffset(2026, 8, 24, 5, 0, 0, TimeSpan.Zero),
            parsed.FiveHour.ResetsAtUtc!.Value.AddTicks(-parsed.FiveHour.ResetsAtUtc.Value.Ticks % TimeSpan.TicksPerSecond));
        Assert.Equal(TimeSpan.Zero, parsed.SevenDay.ResetsAtUtc!.Value.Offset);
    }

    /// <summary>
    /// The source writes an offset-qualified timestamp. Parsing it as a local
    /// <see cref="DateTime"/> would silently shift it by the machine's offset, the class of bug
    /// that puts a reset countdown hours out and looks like an arithmetic error somewhere else.
    /// </summary>
    [Fact]
    public void AnOffsetTimestampIsNormalisedToUtcRatherThanShifted()
    {
        var parsed = CachedUtilization.Parse(
            Document(sevenDayResets: "2026-08-24T23:00:00.000000+02:00"));

        Assert.Equal(new DateTimeOffset(2026, 8, 24, 21, 0, 0, TimeSpan.Zero), parsed!.SevenDay!.ResetsAtUtc);
    }

    /// <summary>A file from before the block existed, or one written by another tool.</summary>
    [Fact]
    public void ADocumentWithoutTheBlockYieldsNothing()
    {
        Assert.Null(CachedUtilization.Parse("""{"numStartups": 12}"""));
    }

    /// <summary>
    /// No fetch time means no way to say how old the figures are. An undated percentage is the
    /// confident-but-wrong reading rule 6 exists to prevent, so the whole block is refused
    /// rather than shown without an age.
    /// </summary>
    [Fact]
    public void AnUndatedBlockIsRefusedEntirely()
    {
        var undated = """
        {"cachedUsageUtilization": {"utilization": {"five_hour": {"utilization": 91}}}}
        """;

        Assert.Null(CachedUtilization.Parse(undated));
    }

    /// <summary>The sibling bars really are null on a normal plan; that is not a parse failure.</summary>
    [Fact]
    public void ABarThatIsNullIsSimplyAbsent()
    {
        var parsed = CachedUtilization.Parse(
            """{"cachedUsageUtilization": {"fetchedAtMs": """
            + Ms(Fetched)
            + """, "utilization": {"five_hour": null, "seven_day": null}}}""");

        Assert.NotNull(parsed);
        Assert.Null(parsed.FiveHour);
        Assert.Null(parsed.SevenDay);
    }

    /// <summary>A percentage without a reset instant is still a usable percentage.</summary>
    [Fact]
    public void AMeterWithNoResetInstantKeepsItsPercentage()
    {
        var parsed = CachedUtilization.Parse(Document(fiveHourResets: null));

        Assert.Equal(91, parsed!.FiveHour!.Percent);
        Assert.Null(parsed.FiveHour.ResetsAtUtc);
    }

    /// <summary>
    /// Another application's private cache, mid-write or hand-edited. Providers must never throw
    /// (<c>IUsageProvider</c>), and that contract starts at the file.
    /// </summary>
    [Fact]
    public void AMalformedFileIsReportedAsAbsentRatherThanThrowing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"oview-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ this is not json");
        try
        {
            Assert.Null(CachedUtilization.TryRead(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AMissingFileIsReportedAsAbsent()
    {
        Assert.Null(CachedUtilization.TryRead(
            Path.Combine(Path.GetTempPath(), $"oview-absent-{Guid.NewGuid():N}.json")));
    }

    /// <summary>
    /// A block with <c>limits</c> and <c>spend</c> in the shape read from a real cache on
    /// 2026-11-06 (dates scrubbed): a session row, an account-wide weekly row and one
    /// model-scoped weekly row, then a spend block at zero.
    /// </summary>
    private static string BlockWith(string limits = Limits, string spend = Spend) =>
        $$"""
        {
          "cachedUsageUtilization": {
            "fetchedAtMs": {{Ms(Fetched)}},
            "utilization": {
              "five_hour": { "utilization": 7, "resets_at": "2026-08-24T05:00:00+00:00" },
              "seven_day": { "utilization": 13, "resets_at": "2026-08-24T21:00:00+00:00" },
              "extra_usage": { "is_enabled": false },
              "limits": {{limits}},
              "spend": {{spend}}
            }
          }
        }
        """;

    private const string Limits = """
        [
          {"kind": "session", "group": "session", "percent": 7, "severity": "normal",
           "resets_at": "2026-08-24T05:00:00+00:00", "scope": null, "is_active": false},
          {"kind": "weekly_all", "group": "weekly", "percent": 13, "severity": "normal",
           "resets_at": "2026-08-24T21:00:00+00:00", "scope": null, "is_active": true},
          {"kind": "weekly_scoped", "group": "weekly", "percent": 10, "severity": "normal",
           "resets_at": "2026-08-24T21:00:00+00:00",
           "scope": {"model": {"id": null, "display_name": "Fable"}, "surface": null},
           "is_active": false}
        ]
        """;

    private const string Spend = """
        {
          "used": {"amount_minor": 0, "currency": "USD", "exponent": 2},
          "limit": null, "percent": 0, "severity": "normal", "enabled": false,
          "disabled_reason": null, "cap": null, "balance": null, "auto_reload": null,
          "can_purchase_credits": false, "can_toggle": false
        }
        """;

    [Fact]
    public void TheRealBlockYieldsTheModelLimitAndTheSpendBesideTheMeters()
    {
        var parsed = CachedUtilization.Parse(BlockWith());

        Assert.Equal(7, parsed!.FiveHour!.Percent);
        Assert.Equal(13, parsed.SevenDay!.Percent);
        var limit = Assert.Single(parsed.ModelLimits.Limits);
        Assert.Equal("Fable", limit.Model);
        Assert.Equal(10, limit.Bar.Percent);
        Assert.Equal(3, parsed.ModelLimits.RowsSeen);
        Assert.Equal(2, parsed.ModelLimits.RowsSkipped);
        Assert.Equal(SpendReadState.Read, parsed.Spend.State);
    }

    /// <summary>
    /// The two meters come from the same block as the new parts, and a lost meter is the
    /// expensive failure: every shape below must cost only its own part.
    /// </summary>
    [Theory]
    [InlineData("\"limits\"", "{}")]
    [InlineData("\"limits\"", "\"nope\"")]
    [InlineData("\"limits\"", "null")]
    [InlineData("\"limits\"", "[null, 7, \"x\", [], {\"group\": 5}]")]
    [InlineData("\"spend\"", "7")]
    [InlineData("\"spend\"", "[]")]
    [InlineData("\"spend\"", "{\"used\": \"lots\", \"enabled\": true}")]
    [InlineData("\"spend\"", "null")]
    public void ABrokenLimitsOrSpendLeavesBothMetersIntact(string property, string value)
    {
        var json = property == "\"limits\""
            ? BlockWith(limits: value)
            : BlockWith(spend: value);

        var parsed = CachedUtilization.Parse(json);

        Assert.Equal(7, parsed!.FiveHour!.Percent);
        Assert.Equal(13, parsed.SevenDay!.Percent);
        Assert.Equal(ExtraUsageState.Disabled, parsed.ExtraUsage.State);
    }

    [Fact]
    public void ABlockWithNeitherLimitsNorSpendReadsAsNothingForBoth()
    {
        var parsed = CachedUtilization.Parse(Document());

        Assert.Empty(parsed!.ModelLimits.Limits);
        Assert.Equal(0, parsed.ModelLimits.RowsSeen);
        Assert.Equal(SpendReadState.Absent, parsed.Spend.State);
    }

    /// <summary>
    /// A meter's rule and a model limit's rule are one rule: the same percentage, reset and
    /// age give the same answer through either, so a limit cannot outlive its window or keep
    /// an aged zero when the meter beside it has dropped them.
    /// </summary>
    [Theory]
    [InlineData(10, "2026-08-24T21:00:00+00:00", 3, 10)]   // live
    [InlineData(10, "2026-08-23T21:00:00+00:00", 3, null)] // window rolled over
    [InlineData(0, "2026-08-24T21:00:00+00:00", 3, 0)]     // fresh zero
    [InlineData(0, "2026-08-24T21:00:00+00:00", 60, null)] // aged zero
    public void AModelLimitIsJudgedByTheSameRuleAsAMeter(int percent, string resets, int ageMinutes, int? expected)
    {
        var block = BlockWith(limits: $$$"""
            [{"group": "weekly", "percent": {{{percent}}}, "resets_at": "{{{resets}}}",
              "scope": {"model": {"display_name": "Fable"}, "surface": null}}]
            """);
        var parsed = CachedUtilization.Parse(block)!;
        var limit = Assert.Single(parsed.ModelLimits.Limits);
        var age = TimeSpan.FromMinutes(ageMinutes);
        var now = Fetched + age;

        var viaLimit = CachedUtilizationProvider.CurrentWindowPercent(limit.Bar, now, age);
        var viaMeter = CachedUtilizationProvider.CurrentWindowPercent(
            new UtilizationBar(percent, limit.Bar.ResetsAtUtc), now, age);

        Assert.Equal(expected, viaLimit);
        Assert.Equal(viaMeter, viaLimit);
    }

    /// <summary>
    /// The provider's rules, which are where the honesty lives, parsing is the easy half.
    /// </summary>
    public class Provider
    {
        private static readonly DateTimeOffset Fetched = new(2026, 8, 24, 0, 0, 0, TimeSpan.Zero);

        private static CachedUtilizationProvider For(
            int fiveHour = 91,
            DateTimeOffset? fiveHourResets = null,
            int sevenDay = 79,
            DateTimeOffset? sevenDayResets = null,
            DateTimeOffset? fetchedAt = null) =>
            new(() => new CachedUtilization(
                fetchedAt ?? Fetched,
                "acct",
                new UtilizationBar(fiveHour, fiveHourResets ?? Fetched.AddHours(5)),
                new UtilizationBar(sevenDay, sevenDayResets ?? Fetched.AddHours(21))));

        [Fact]
        public void FreshFiguresArriveLiveWithBothMetersAndBothResets()
        {
            var snapshot = For().GetSnapshot(Fetched.AddMinutes(2));

            Assert.Equal(DataSource.Live, snapshot.Source);
            Assert.Equal(91, snapshot.SessionPercentValue.Value);
            Assert.Equal(79, snapshot.WeeklyPercentValue.Value);
            Assert.Equal(Fetched.AddHours(5), snapshot.SessionResetAtUtcValue.Value);
            Assert.Equal(Fetched.AddHours(21), snapshot.WeeklyResetAtUtcValue.Value);
        }

        /// <summary>
        /// Reported, not inferred, so neither reset carries a bracket, and neither may render
        /// with the "~" that marks one.
        /// </summary>
        [Fact]
        public void AReportedResetCarriesNoUncertainty()
        {
            var snapshot = For().GetSnapshot(Fetched.AddMinutes(2));

            Assert.Equal(TimeSpan.Zero, snapshot.SessionResetUncertainty);
            Assert.Equal(TimeSpan.FromDays(7), snapshot.WeeklyResetPeriod);
        }

        /// <summary>
        /// This is a cache refreshed when Claude Code runs, not a sampler. Past the threshold it
        /// still carries information, but it says so.
        /// </summary>
        [Fact]
        public void FiguresOlderThanTheThresholdAreLabelledStaleRatherThanDropped()
        {
            var snapshot = For().GetSnapshot(Fetched.AddHours(1));

            Assert.Equal(DataSource.Stale, snapshot.Source);
            Assert.Equal(91, snapshot.SessionPercentValue.Value);
            Assert.Equal(Fetched, snapshot.CapturedAtUtcValue.Value);
        }

        /// <summary>
        /// <b>The failure mode unique to this source.</b> Claude Code caches while it runs; leave
        /// it closed across a window boundary and the file still reads 91% for a window that
        /// reset to nothing hours ago. The bar carries the instant its own window ends, so this
        /// is checkable, and checked. Showing the cached figure here would be the worst number
        /// O-view could display: confidently near the limit when the truth is near zero.
        /// </summary>
        [Fact]
        public void APercentageIsDiscardedOnceItsOwnWindowHasRolledOver()
        {
            var snapshot = For().GetSnapshot(Fetched.AddHours(6));

            Assert.Null(snapshot.SessionPercentValue.Value);
            Assert.Null(snapshot.SessionResetAtUtcValue.Value);
        }

        /// <summary>The windows roll independently; one expiring says nothing about the other.</summary>
        [Fact]
        public void TheWeeklyMeterSurvivesTheSessionWindowRollingOver()
        {
            var snapshot = For().GetSnapshot(Fetched.AddHours(6));

            Assert.Equal(79, snapshot.WeeklyPercentValue.Value);
            Assert.Equal(Fetched.AddHours(21), snapshot.WeeklyResetAtUtcValue.Value);
        }

        /// <summary>
        /// A passed boundary is not stepped forward to the next one. For the five-hour window
        /// that would rebuild the bug issue #180 removed, the window starts on first use, so a
        /// grid stepped forward describes a window that never existed. For the weekly window the
        /// arithmetic would be sound but the result would be an inference wearing a reported
        /// value's zero uncertainty. Null hands the question back to the derivation that already
        /// exists, correctly labelled as derived.
        /// </summary>
        [Fact]
        public void APassedResetIsNotSteppedForwardToTheNextWindow()
        {
            var snapshot = For().GetSnapshot(Fetched.AddDays(8));

            Assert.Null(snapshot.WeeklyResetAtUtcValue.Value);
            Assert.Null(snapshot.SessionResetAtUtcValue.Value);
            Assert.Equal(DataSource.None, snapshot.Source);
        }

        /// <summary>
        /// Within a window utilisation only rises, so an aged reading is a lower bound. Zero is
        /// the one value whose bound says nothing at all while looking like a precise finding
        /// that the window is empty, the same argument, and the same threshold, as
        /// <c>PlanHistoryProvider.ZeroReadingFreshness</c> (issue #161).
        /// </summary>
        [Fact]
        public void AnAgedZeroIsDiscardedWhereAnAgedNonZeroIsKept()
        {
            var stale = Fetched.AddMinutes(30);

            Assert.Null(For(fiveHour: 0).GetSnapshot(stale).SessionPercentValue.Value);
            Assert.Equal(4, For(fiveHour: 4).GetSnapshot(stale).SessionPercentValue.Value);
        }

        /// <summary>A zero that was just written is a measurement, and is shown.</summary>
        [Fact]
        public void AFreshZeroIsAMeasurementAndIsShown()
        {
            Assert.Equal(0, For(fiveHour: 0).GetSnapshot(Fetched.AddMinutes(2)).SessionPercentValue.Value);
        }

        [Fact]
        public void NoCachedBlockIsNoData()
        {
            Assert.Equal(UsageSnapshot.None, new CachedUtilizationProvider(() => null).GetSnapshot(Fetched));
        }

        /// <summary>
        /// A provider that throws must not blank the display, it must fall through, so the
        /// composite can reach a source that still knows something (issue #16's shape).
        /// </summary>
        [Fact]
        public void AReaderThatThrowsIsNoDataRatherThanACrash()
        {
            var provider = new CachedUtilizationProvider(
                () => throw new IOException("locked"));

            Assert.Equal(UsageSnapshot.None, provider.GetSnapshot(Fetched));
        }

        /// <summary>
        /// A bar with a percentage but no reset instant cannot be checked for rollover, so it
        /// leans on the staleness label alone rather than being dropped.
        /// </summary>
        [Fact]
        public void AMeterWithNoResetInstantStillReportsItsPercentage()
        {
            var provider = new CachedUtilizationProvider(() => new CachedUtilization(
                Fetched, "acct", new UtilizationBar(55, null), null));

            var snapshot = provider.GetSnapshot(Fetched.AddMinutes(2));

            Assert.Equal(55, snapshot.SessionPercentValue.Value);
            Assert.Null(snapshot.SessionResetAtUtcValue.Value);
        }
    }
}
