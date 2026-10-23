using OView.Core.Models;
using OView.Core.Providers.CachedUsage;
using OView.Core.Providers.PlanHistory;

namespace OView.Core.Tests;

/// <summary>
/// The same reset instant, fed through every Core path that can produce one, pins which
/// source produced it and whether that source claims the result measured or modelled
/// Complements <see cref="UsageSnapshotStatusTests"/>, which checks each
/// provider's status defaults in isolation — this test's point is that two providers
/// describing the <b>same</b> instant agree on it to the second, while disagreeing, exactly as
/// designed, on how sure they are of it.
/// </summary>
public class ResetTimeProvenanceTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Directory.CreateTempSubdirectory("oview-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteSamples(params (DateTimeOffset at, int fh, int sd)[] samples)
    {
        var rows = samples.Select(s =>
            $"{{\"t\":{s.at.ToUnixTimeMilliseconds()},\"org\":\"org-a\",\"u\":{{\"fh\":{s.fh},\"sd\":{s.sd}}}}}");
        var path = Path.Combine(_dir, "plan-usage-history.json");
        File.WriteAllText(path, $"{{\"version\":2,\"samples\":[{string.Join(',', rows)}]}}");
        return path;
    }

    /// <summary>
    /// Plan history <b>models</b> the session reset: it watches the meter rise from zero and
    /// predicts five hours on from that instant (ADR-0007) — never reported, only ever
    /// inferred from a drop it happened to observe.
    /// </summary>
    [Fact]
    public void PlanHistory_PredictsTheSessionReset_AndClaimsItModelled()
    {
        var risesAt = T0;
        var path = WriteSamples((risesAt.AddMinutes(-5), 0, 7), (risesAt, 47, 8));

        var snapshot = new PlanHistoryProvider(path).GetSnapshot(risesAt.AddMinutes(10));

        Assert.Equal(UsageValueStatus.Estimated, snapshot.SessionResetAtUtcValue.Status);
        Assert.Equal(risesAt + ResetDetector.WindowLength, snapshot.SessionResetAtUtcValue.Value);
    }

    /// <summary>
    /// Plan history never predicts a weekly reset at all (ADR-0014) — the detector that used
    /// to infer one from <c>sd</c> drops is deleted, not merely unused here.
    /// </summary>
    [Fact]
    public void PlanHistory_NeverProducesAWeeklyReset()
    {
        var path = WriteSamples((T0.AddMinutes(-5), 40, 7));

        var snapshot = new PlanHistoryProvider(path).GetSnapshot(T0);

        Assert.Equal(UsageValueStatus.Unavailable, snapshot.WeeklyResetAtUtcValue.Status);
        Assert.Null(snapshot.WeeklyResetAtUtcValue.Value);
    }

    /// <summary>
    /// The cached utilization reading <b>measures</b> both resets: Claude Code reports the
    /// instant outright, so there is nothing here to model and nothing here to bracket.
    ///
    /// <para>The session instant is the same one <see cref="PlanHistory_PredictsTheSessionReset_AndClaimsItModelled"/>
    /// derives above, to the second — pinning that a modelled prediction and a reported fact
    /// can describe the identical moment while still carrying different claims about how it
    /// was obtained.</para>
    /// </summary>
    [Fact]
    public void CachedUtilization_ReportsBothResets_AndClaimsThemMeasured()
    {
        var sessionResets = T0 + ResetDetector.WindowLength;
        var weeklyResets = T0.AddDays(3);
        var provider = new CachedUtilizationProvider(() => new CachedUtilization(
            T0,
            "acct",
            new UtilizationBar(91, sessionResets),
            new UtilizationBar(79, weeklyResets)));

        var snapshot = provider.GetSnapshot(T0.AddMinutes(2));

        Assert.Equal(UsageValueStatus.Real, snapshot.SessionResetAtUtcValue.Status);
        Assert.Equal(sessionResets, snapshot.SessionResetAtUtcValue.Value);
        Assert.Equal(UsageValueStatus.Real, snapshot.WeeklyResetAtUtcValue.Status);
        Assert.Equal(weeklyResets, snapshot.WeeklyResetAtUtcValue.Value);
    }
}
