using OView.Core.Models;
using OView.Core.Providers.PlanHistory;

namespace OView.Core.Tests;

public class PlanHistoryProviderTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Directory.CreateTempSubdirectory("oview-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteSamples(params (DateTimeOffset at, string org, int fh, int sd)[] samples)
    {
        var rows = samples.Select(s =>
            $"{{\"t\":{s.at.ToUnixTimeMilliseconds()},\"org\":\"{s.org}\",\"u\":{{\"fh\":{s.fh},\"sd\":{s.sd}}}}}");
        var path = Path.Combine(_dir, "plan-usage-history.json");
        File.WriteAllText(path, $"{{\"version\":2,\"samples\":[{string.Join(',', rows)}]}}");
        return path;
    }

    private sealed class ThrowingResetLog : IWeeklyResetLog
    {
        public void Record(IEnumerable<WeeklyResetObservation> observations) =>
            throw new InvalidOperationException("reset log unavailable (e.g. unwritable log file)");
        public IReadOnlyList<WeeklyResetObservation> GetObservations(string? orgUuid = null) =>
            throw new InvalidOperationException("reset log unavailable (e.g. unwritable log file)");
    }

    /// <summary>In-memory log, so the provider's discovery loop can be exercised end to end.</summary>
    private sealed class MemoryResetLog : IWeeklyResetLog
    {
        private readonly List<WeeklyResetObservation> _observations = [];

        public IReadOnlyList<WeeklyResetObservation> Recorded => _observations;

        public void Record(IEnumerable<WeeklyResetObservation> observations)
        {
            foreach (var o in observations.Where(o => !_observations.Contains(o)))
            {
                _observations.Add(o);
            }
        }

        public IReadOnlyList<WeeklyResetObservation> GetObservations(string? orgUuid = null) =>
            orgUuid is null ? _observations : _observations.Where(o => o.OrgUuid == orgUuid).ToList();
    }

    [Fact]
    public void MissingFile_ReturnsNone()
    {
        var provider = new PlanHistoryProvider(Path.Combine(_dir, "absent.json"));

        Assert.Equal(UsageSnapshot.None, provider.GetSnapshot(Now));
    }

    [Fact]
    public void FailingResetLog_StillReportsPrimaryData()
    {
        // issue #16: a failing weekly-reset log must not take anything else down. The
        // session/weekly percentages come from the plan-history file, not the log, so they
        // must survive; the weekly RESET degrades to unknown (null), and nothing else does.
        var path = WriteSamples(
            (Now.AddMinutes(-10), "org-a", 40, 7),
            (Now.AddMinutes(-5), "org-a", 47, 8));
        var provider = new PlanHistoryProvider(path, weeklyResetLog: new ThrowingResetLog());

        var snapshot = provider.GetSnapshot(Now);

        Assert.Equal(DataSource.Live, snapshot.Source);
        Assert.Equal(47, snapshot.SessionPercent);
        Assert.Equal(8, snapshot.WeeklyPercent);
        Assert.Null(snapshot.WeeklyResetAtUtc);
    }

    [Fact]
    public void NoResetSeenYet_LeavesTheWeeklyResetUnknown()
    {
        // The "waiting for first reset" state: real percentages, no derived reset. It is
        // reported as null and never guessed (rule 6); the panel explains the wait.
        var path = WriteSamples(
            (Now.AddMinutes(-10), "org-a", 40, 7),
            (Now.AddMinutes(-5), "org-a", 47, 8));

        var snapshot = new PlanHistoryProvider(path, weeklyResetLog: new MemoryResetLog()).GetSnapshot(Now);

        Assert.Equal(8, snapshot.WeeklyPercent);
        Assert.Null(snapshot.WeeklyResetAtUtc);
        Assert.Null(snapshot.WeeklyResetUncertainty);
    }

    [Fact]
    public void ADropInTheSeries_IsDiscovered_RecordedAndForecast()
    {
        // The discovery loop, end to end: the poll that first sees the drop records it and
        // the same poll's snapshot already carries the forecast — shaped like the real
        // 2026-07-28 reset, i.e. across an overnight gap in Desktop's sampling.
        var log = new MemoryResetLog();
        var path = WriteSamples(
            (Now.AddHours(-10), "org-a", 20, 70),
            (Now.AddMinutes(-5), "org-a", 0, 0));

        var snapshot = new PlanHistoryProvider(path, weeklyResetLog: log).GetSnapshot(Now);

        Assert.Single(log.Recorded);
        Assert.Equal(Now.AddMinutes(-5).AddDays(7), snapshot.WeeklyResetAtUtc);
        Assert.Equal(TimeSpan.FromHours(10).Add(TimeSpan.FromMinutes(-5)), snapshot.WeeklyResetUncertainty);
    }

    [Fact]
    public void DiscoveryKeepsLooking_AndDoesNotDuplicateAKnownReset()
    {
        var log = new MemoryResetLog();
        var path = WriteSamples(
            (Now.AddHours(-10), "org-a", 20, 70),
            (Now.AddMinutes(-5), "org-a", 0, 0));
        var provider = new PlanHistoryProvider(path, weeklyResetLog: log);

        for (var poll = 0; poll < 5; poll++)
        {
            provider.GetSnapshot(Now);
        }

        Assert.Single(log.Recorded);
    }

    [Fact]
    public void ResetsAreScopedToTheOrgTheSamplesBelongTo()
    {
        // A log carried over from a different organization must not be used to forecast
        // this one's window — the windows are per-org and unrelated.
        var log = new MemoryResetLog();
        log.Record([new WeeklyResetObservation(Now.AddDays(-3).AddMinutes(-5), Now.AddDays(-3), "org-b")]);
        var path = WriteSamples(
            (Now.AddMinutes(-10), "org-a", 40, 7),
            (Now.AddMinutes(-5), "org-a", 47, 8));

        var snapshot = new PlanHistoryProvider(path, weeklyResetLog: log).GetSnapshot(Now);

        Assert.Null(snapshot.WeeklyResetAtUtc);
    }

    [Fact]
    public void FreshSample_IsLive_WithLatestValues()
    {
        var path = WriteSamples(
            (Now.AddMinutes(-10), "org-a", 40, 7),
            (Now.AddMinutes(-5), "org-a", 47, 8));
        var provider = new PlanHistoryProvider(path);

        var snapshot = provider.GetSnapshot(Now);

        Assert.Equal(DataSource.Live, snapshot.Source);
        Assert.Equal(47, snapshot.SessionPercent);
        Assert.Equal(8, snapshot.WeeklyPercent);
        Assert.Equal(Now.AddMinutes(-5), snapshot.CapturedAtUtc);
    }

    [Fact]
    public void OldSample_IsStale_ValuesStillReported()
    {
        var path = WriteSamples((Now.AddHours(-2), "org-a", 31, 6));
        var provider = new PlanHistoryProvider(path);

        var snapshot = provider.GetSnapshot(Now);

        Assert.Equal(DataSource.Stale, snapshot.Source);
        Assert.Equal(31, snapshot.SessionPercent);
    }

    [Fact]
    public void NoDropObserved_ResetIsNull_NotGuessed()
    {
        var path = WriteSamples(
            (Now.AddMinutes(-10), "org-a", 40, 7),
            (Now.AddMinutes(-5), "org-a", 47, 8));
        var provider = new PlanHistoryProvider(path);

        Assert.Null(provider.GetSnapshot(Now).SessionResetAtUtc);
    }

    /// <summary>
    /// The window runs from <b>first use</b>, not from the drop that ended the previous one
    /// (GitHub issue #180). Here the meter resets to 0 an hour ago and nothing is used for
    /// another 55 minutes — so the current window began at that first use, and the reset is
    /// five hours after it.
    ///
    /// <para>This previously asserted drop + 5 h, which is the grid model: it treated the
    /// end of the old window as the start of the new one. They coincide only when usage
    /// resumes within a sampling interval.</para>
    /// </summary>
    [Fact]
    public void TheWindowRunsFromFirstUse_NotFromTheDropThatEndedTheLastOne()
    {
        var drop = Now.AddHours(-1);
        var firstUse = Now.AddMinutes(-5);
        var path = WriteSamples(
            (drop.AddMinutes(-5), "org-a", 31, 6),
            (drop, "org-a", 0, 6),
            (firstUse, "org-a", 12, 7));
        var provider = new PlanHistoryProvider(path);

        Assert.Equal(firstUse.AddHours(5), provider.GetSnapshot(Now).SessionResetAtUtc);
    }

    /// <summary>
    /// The bracket reaches the snapshot, so the panel can mark a gap-inferred boundary
    /// approximate instead of printing it to the minute (rule 6).
    /// </summary>
    [Fact]
    public void AGapInferredWindowCarriesItsUncertainty()
    {
        var path = WriteSamples(
            (Now.AddHours(-2), "org-a", 0, 6),
            (Now.AddMinutes(-5), "org-a", 12, 7));
        var provider = new PlanHistoryProvider(path);

        var snapshot = provider.GetSnapshot(Now);

        Assert.Equal(TimeSpan.FromMinutes(115), snapshot.SessionResetUncertainty);
    }

    /// <summary>
    /// An expired window is unknown, not extrapolated. The old detector stepped a stale
    /// anchor forward until it landed in the future, which always produced a confident time.
    /// </summary>
    [Fact]
    public void AWindowThatHasAlreadyEndedReportsNoResetTime()
    {
        var path = WriteSamples(
            (Now.AddHours(-8), "org-a", 40, 6),
            (Now.AddHours(-7), "org-a", 2, 6),
            (Now.AddHours(-6), "org-a", 30, 7));
        var provider = new PlanHistoryProvider(path);

        Assert.Null(provider.GetSnapshot(Now).SessionResetAtUtc);
    }

    [Fact]
    public void OtherOrgSamples_AreExcluded()
    {
        var path = WriteSamples(
            (Now.AddMinutes(-10), "org-a", 40, 7),
            (Now.AddMinutes(-5), "org-b", 90, 90));
        var provider = new PlanHistoryProvider(path, orgUuid: "org-a");

        var snapshot = provider.GetSnapshot(Now);

        Assert.Equal(40, snapshot.SessionPercent);
        Assert.Equal(Now.AddMinutes(-10), snapshot.CapturedAtUtc);
    }

    [Fact]
    public void SingleOrgFile_IsShown_EvenWhenAccountOrgDiffers()
    {
        // The regression that blanked new users' panels: Claude Code (~/.claude.json) and
        // Claude Desktop (plan-usage-history.json) signed into different accounts, so the
        // account org matched no Desktop sample. The old filter returned nothing; the file
        // holds one org's perfectly good usage and must be shown.
        var path = WriteSamples(
            (Now.AddMinutes(-10), "org-desktop", 40, 7),
            (Now.AddMinutes(-5), "org-desktop", 47, 8));
        var provider = new PlanHistoryProvider(path, orgUuid: "org-from-claude-code");

        var snapshot = provider.GetSnapshot(Now);

        Assert.Equal(DataSource.Live, snapshot.Source);
        Assert.Equal(47, snapshot.SessionPercent);
        Assert.Equal(8, snapshot.WeeklyPercent);
    }

    [Fact]
    public void MultiOrgFile_AccountMatchesNone_FallsBackToMostRecentOrg()
    {
        // A genuinely interleaved file whose orgs match neither the account: don't blank —
        // resolve to the most-recently-active org (one org, still de-interleaved).
        var path = WriteSamples(
            (Now.AddMinutes(-15), "org-old", 90, 60),
            (Now.AddMinutes(-10), "org-old", 92, 61),
            (Now.AddMinutes(-5), "org-recent", 33, 9));
        var provider = new PlanHistoryProvider(path, orgUuid: "org-unrelated");

        var snapshot = provider.GetSnapshot(Now);

        Assert.Equal(33, snapshot.SessionPercent);
        Assert.Equal(9, snapshot.WeeklyPercent);
        Assert.Equal(Now.AddMinutes(-5), snapshot.CapturedAtUtc);
    }


    // ── an aged zero is not a measurement (issue #161) ────────────────────────────

    /// <summary>
    /// The reported case, reproduced exactly. The five-hour window reset (72 → 0), Desktop
    /// sampled at that instant and then went quiet for 14 minutes while ~6% was consumed.
    ///
    /// <para>O-view showed an empty gauge the whole time, because 14 minutes was inside the
    /// old 15-minute freshness allowance — so the reading was labelled Live and rendered as a
    /// confident, unqualified 0%. Unknown is the honest answer: the window is not empty, and
    /// O-view has no idea what it holds.</para>
    /// </summary>
    [Fact]
    public void AZeroThatHasAgedPastASamplingInterval_IsUnknownRatherThanEmpty()
    {
        var path = WriteSamples(
            (Now.AddMinutes(-30), "org-a", 68, 42),
            (Now.AddMinutes(-22), "org-a", 72, 42),
            (Now.AddMinutes(-14), "org-a", 0, 42));   // the reset, then silence

        var snapshot = new PlanHistoryProvider(path, orgUuid: "org-a").GetSnapshot(Now);

        Assert.Null(snapshot.SessionPercent);
        Assert.Equal(42, snapshot.WeeklyPercent);   // the weekly figure is untouched
    }

    /// <summary>
    /// A zero that is genuinely current still reads zero. This is the normal case immediately
    /// after a reset — Desktop is sampling every ~5 minutes, so a fresh zero arrives and the
    /// gauge legitimately shows an empty window.
    /// </summary>
    [Fact]
    public void AFreshZero_IsStillReportedAsZero()
    {
        var path = WriteSamples(
            (Now.AddMinutes(-8), "org-a", 72, 42),
            (Now.AddMinutes(-2), "org-a", 0, 42));

        var snapshot = new PlanHistoryProvider(path, orgUuid: "org-a").GetSnapshot(Now);

        Assert.Equal(0, snapshot.SessionPercent);
        Assert.Equal(DataSource.Live, snapshot.Source);
    }

    /// <summary>
    /// Only zero is discarded. A non-zero reading of the same age is a lower bound that is
    /// still broadly true — 72% drifting to 75% is information, where "at least 0%" is not.
    /// </summary>
    [Fact]
    public void AnAgedNonZeroReading_IsKept()
    {
        var path = WriteSamples(
            (Now.AddMinutes(-20), "org-a", 68, 42),
            (Now.AddMinutes(-14), "org-a", 72, 42));

        var snapshot = new PlanHistoryProvider(path, orgUuid: "org-a").GetSnapshot(Now);

        Assert.Equal(72, snapshot.SessionPercent);
    }

    /// <summary>
    /// The freshness allowance is anchored to the measured sampling cadence, and the cadence
    /// moved: 5 minutes until 2026-08-10, 15 minutes since (measured across 1,443 gaps in the
    /// same real file — 31 consecutive 15-minute gaps on 08-23 alone, none at 5).
    ///
    /// <para>Eleven minutes was two intervals plus slack at the old cadence and <i>less than
    /// one interval</i> at the new one, which made Live unreachable for four minutes in every
    /// fifteen — a reading as recent as Desktop can produce, labelled stale. Sixteen is one
    /// interval plus slack: stale now means the next sample is overdue.</para>
    ///
    /// <para>Note 14 minutes flipping from Stale to Live. That is the cadence change, not a
    /// relaxation — at 15-minute sampling a 14-minute-old reading is the newest that can
    /// exist. What issue #161 was actually about is a 14-minute-old <b>zero</b>, and that is
    /// still discarded: see <c>ZeroReadingFreshness</c>, which was deliberately left at 6.</para>
    /// </summary>
    [Theory]
    [InlineData(9, DataSource.Live)]
    [InlineData(14, DataSource.Live)]
    [InlineData(17, DataSource.Stale)]
    [InlineData(31, DataSource.Stale)]
    public void SampleAgeDecidesLiveOrStale(int minutesOld, DataSource expected)
    {
        var path = WriteSamples(
            (Now.AddMinutes(-minutesOld - 6), "org-a", 60, 42),
            (Now.AddMinutes(-minutesOld), "org-a", 72, 42));

        Assert.Equal(expected, new PlanHistoryProvider(path, orgUuid: "org-a").GetSnapshot(Now).Source);
    }

    [Fact]
    public void CrossOrgSequence_DoesNotFakeADrop()
    {
        // org-b at 90% followed by org-a at 5% is not a reset. Filtering must happen
        // before drop detection or interleaved orgs manufacture phantom drops.
        var path = WriteSamples(
            (Now.AddMinutes(-15), "org-b", 90, 50),
            (Now.AddMinutes(-10), "org-a", 5, 2),
            (Now.AddMinutes(-5), "org-a", 6, 2));
        var provider = new PlanHistoryProvider(path, orgUuid: "org-a");

        Assert.Null(provider.GetSnapshot(Now).SessionResetAtUtc);
    }

    // ── narrowing with local activity (issue #185) ──────────────────────────────────

    /// <summary>
    /// The measured case. Desktop samples every ~15 minutes, so the window's start is only
    /// bracketed to that — and forecasting from the upper bound left the reset about half an
    /// interval late (~8m53s against Desktop on 2026-08-23). A local request inside the
    /// bracket proves the window was already running, tightening the bound.
    /// </summary>
    [Fact]
    public void ARequestInsideTheBracketTightensTheReset()
    {
        var lastZero = Now.AddMinutes(-20);
        var firstSeen = Now.AddMinutes(-5);
        var firstRequest = Now.AddMinutes(-14);

        var path = WriteSamples(
            (lastZero, "org-a", 0, 6),
            (firstSeen, "org-a", 9, 7));
        var provider = new PlanHistoryProvider(path,
            earliestActivity: (from, to) => firstRequest > from && firstRequest <= to ? firstRequest : null);

        var snapshot = provider.GetSnapshot(Now);

        Assert.Equal(firstRequest.AddHours(5), snapshot.SessionResetAtUtc);
        Assert.Equal(TimeSpan.FromMinutes(6), snapshot.SessionResetUncertainty);
    }

    /// <summary>
    /// No transcript in the bracket — a chat-only or Desktop-only user — falls back to
    /// exactly the plan-history behaviour. This may only ever improve a figure.
    /// </summary>
    [Fact]
    public void NoLocalActivityFallsBackToThePlanHistoryBracket()
    {
        var firstSeen = Now.AddMinutes(-5);
        var path = WriteSamples(
            (Now.AddMinutes(-20), "org-a", 0, 6),
            (firstSeen, "org-a", 9, 7));

        var withoutLookup = new PlanHistoryProvider(path).GetSnapshot(Now);
        var withEmptyLookup = new PlanHistoryProvider(path, earliestActivity: (_, _) => null).GetSnapshot(Now);

        Assert.Equal(firstSeen.AddHours(5), withoutLookup.SessionResetAtUtc);
        Assert.Equal(withoutLookup.SessionResetAtUtc, withEmptyLookup.SessionResetAtUtc);
    }

    /// <summary>
    /// A refinement must never take down the reading it refines. A store that throws leaves
    /// the plan-history answer intact rather than blanking the panel.
    /// </summary>
    [Fact]
    public void AFailingActivityLookupLeavesTheResetIntact()
    {
        var firstSeen = Now.AddMinutes(-5);
        var path = WriteSamples(
            (Now.AddMinutes(-20), "org-a", 0, 6),
            (firstSeen, "org-a", 9, 7));
        var provider = new PlanHistoryProvider(path,
            earliestActivity: (_, _) => throw new InvalidOperationException("store is busy"));

        Assert.Equal(firstSeen.AddHours(5), provider.GetSnapshot(Now).SessionResetAtUtc);
    }

    // ── the entered weekly reset (issue #186) ───────────────────────────────────────

    /// <summary>
    /// Precedence over inference. The user read this off Claude's Settings → Usage; O-view
    /// derives its own from a ~10 hour overnight bracket. The entry wins, and carries zero
    /// uncertainty so it does not wear the "~" that marks an approximation.
    /// </summary>
    /// <summary>
    /// The most recent occurrence of <paramref name="entry"/> strictly before
    /// <paramref name="before"/>, so a test can build an observation that agrees with it.
    /// </summary>
    private static DateTimeOffset PreviousBoundary(ManualWeeklyReset entry, DateTimeOffset before) =>
        entry.NextAfter(before.AddDays(-8), TimeZoneInfo.Local) is var first && first < before
            ? Enumerable.Range(0, 8)
                .Select(i => entry.NextAfter(before.AddDays(-8 + i), TimeZoneInfo.Local))
                .Last(at => at < before)
            : first;

    [Fact]
    public void AnEnteredWeeklyResetBeatsTheDerivedOne()
    {
        var entry = new ManualWeeklyReset(DayOfWeek.Monday, new TimeOnly(22, 59));

        // An observation that AGREES with the entry — a bracket straddling its boundary.
        // The derived forecast would still carry that bracket's uncertainty; the entry
        // replaces it with an exact time.
        var boundary = PreviousBoundary(entry, Now);
        var log = new MemoryResetLog();
        log.Record([new WeeklyResetObservation(
            boundary.AddHours(-4), boundary.AddHours(4), "org-a")]);

        var path = WriteSamples((Now.AddMinutes(-5), "org-a", 20, 60));
        var provider = new PlanHistoryProvider(path, weeklyResetLog: log) { ManualWeeklyReset = entry };

        var snapshot = provider.GetSnapshot(Now);

        Assert.Null(provider.ManualWeeklyResetConflict);
        Assert.Equal(TimeSpan.Zero, snapshot.WeeklyResetUncertainty);
        Assert.Equal(DayOfWeek.Monday,
            TimeZoneInfo.ConvertTime(snapshot.WeeklyResetAtUtc!.Value, TimeZoneInfo.Local).DayOfWeek);
        Assert.Equal(new TimeOnly(22, 59),
            TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(
                snapshot.WeeklyResetAtUtc!.Value, TimeZoneInfo.Local).DateTime));
    }

    /// <summary>
    /// With nothing entered, nothing changes — the derived forecast still runs and still
    /// carries its bracket.
    /// </summary>
    [Fact]
    public void WithNoEntryTheDerivedForecastIsUnchanged()
    {
        var log = new MemoryResetLog();
        log.Record([new WeeklyResetObservation(
            Now.AddDays(-7).AddHours(-10), Now.AddDays(-7), "org-a")]);

        var path = WriteSamples((Now.AddMinutes(-5), "org-a", 20, 60));
        var snapshot = new PlanHistoryProvider(path, weeklyResetLog: log).GetSnapshot(Now);

        Assert.NotNull(snapshot.WeeklyResetAtUtc);
        Assert.True(snapshot.WeeklyResetUncertainty > TimeSpan.Zero);
    }

    /// <summary>
    /// Entry loses to evidence. The reset demonstrably happened inside the observed bracket,
    /// so an entry outside it cannot also be true — and a number O-view has evidence against
    /// must not stay on screen (rule 6). It falls back to the observation AND records the
    /// conflict, because silently overriding leaves the user believing what they typed.
    /// </summary>
    [Fact]
    public void AnObservationThatDisprovesTheEntryWinsAndIsRecorded()
    {
        // Observed reset on a Thursday; the entry claims Monday.
        var observedFrom = Now.AddDays(-2).Date.AddHours(6);
        while (TimeZoneInfo.ConvertTime(new DateTimeOffset(observedFrom, TimeSpan.Zero), TimeZoneInfo.Local)
               .DayOfWeek == DayOfWeek.Monday)
        {
            observedFrom = observedFrom.AddDays(1);
        }

        var observation = new WeeklyResetObservation(
            new DateTimeOffset(observedFrom, TimeSpan.Zero),
            new DateTimeOffset(observedFrom.AddHours(2), TimeSpan.Zero),
            "org-a");

        var log = new MemoryResetLog();
        log.Record([observation]);

        var path = WriteSamples((Now.AddMinutes(-5), "org-a", 20, 60));
        var provider = new PlanHistoryProvider(path, weeklyResetLog: log)
        {
            ManualWeeklyReset = new ManualWeeklyReset(DayOfWeek.Monday, new TimeOnly(22, 59)),
        };

        var snapshot = provider.GetSnapshot(Now);

        Assert.NotNull(provider.ManualWeeklyResetConflict);
        Assert.Equal(observation, provider.ManualWeeklyResetConflict);

        // Fell back to the derived value, which carries a bracket rather than zero.
        Assert.True(snapshot.WeeklyResetUncertainty > TimeSpan.Zero);
    }

    /// <summary>
    /// The conflict clears once the entry and the evidence agree again — otherwise a single
    /// bad week would nag forever after the user corrected it.
    /// </summary>
    [Fact]
    public void TheConflictClearsWhenTheEntryAgreesAgain()
    {
        var log = new MemoryResetLog();
        log.Record([new WeeklyResetObservation(
            Now.AddDays(-7).AddHours(-10), Now.AddDays(-7), "org-a")]);

        var path = WriteSamples((Now.AddMinutes(-5), "org-a", 20, 60));
        var provider = new PlanHistoryProvider(path, weeklyResetLog: log)
        {
            ManualWeeklyReset = new ManualWeeklyReset(DayOfWeek.Sunday, new TimeOnly(3, 0)),
        };

        provider.GetSnapshot(Now);
        var flaggedFirst = provider.ManualWeeklyResetConflict is not null;

        // Re-enter a time that sits inside the observed bracket.
        var inBracket = TimeZoneInfo.ConvertTime(Now.AddDays(-7).AddHours(-5), TimeZoneInfo.Local);
        provider.ManualWeeklyReset = new ManualWeeklyReset(
            inBracket.DayOfWeek, TimeOnly.FromDateTime(inBracket.DateTime));
        provider.GetSnapshot(Now);

        Assert.True(flaggedFirst, "a Sunday 03:00 entry should conflict with the observed bracket");
        Assert.Null(provider.ManualWeeklyResetConflict);
    }
}
