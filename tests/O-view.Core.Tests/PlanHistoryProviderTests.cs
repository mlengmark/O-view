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

    [Fact]
    public void ObservedDrop_YieldsDropPlusFiveHours()
    {
        var drop = Now.AddHours(-1);
        var path = WriteSamples(
            (drop.AddMinutes(-5), "org-a", 31, 6),
            (drop, "org-a", 0, 6),
            (Now.AddMinutes(-5), "org-a", 12, 7));
        var provider = new PlanHistoryProvider(path);

        Assert.Equal(drop.AddHours(5), provider.GetSnapshot(Now).SessionResetAtUtc);
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
}
