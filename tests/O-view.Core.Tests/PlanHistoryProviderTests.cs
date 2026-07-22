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
        public void RecordResets(IEnumerable<DateTimeOffset> resets) =>
            throw new InvalidOperationException("reset log unavailable (e.g. corrupt store)");
        public IReadOnlyList<DateTimeOffset> GetResets() =>
            throw new InvalidOperationException("reset log unavailable (e.g. corrupt store)");
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
        // issue #16: a corrupt rollup DB throws from the weekly-reset log, but the
        // session/weekly percentages come from the plan-history file, not the DB, and
        // must survive. The weekly RESET degrades to unknown (null); nothing else does.
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
    public void OrgFilterMatchingNothing_ReturnsNone()
    {
        var path = WriteSamples((Now.AddMinutes(-5), "org-a", 40, 7));
        var provider = new PlanHistoryProvider(path, orgUuid: "org-c");

        Assert.Equal(UsageSnapshot.None, provider.GetSnapshot(Now));
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
