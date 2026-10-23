using OView.Core.Models;
using OView.Core.Providers.CachedUsage;
using OView.Core.Providers.Jsonl;
using OView.Core.Providers.PlanHistory;
using OView.Core.Storage;

namespace OView.Core.Tests;

/// <summary>
/// Each provider now says, per percentage and per reset time, whether what it produced was
/// measured, modelled or absent, alongside the existing nullable members, which
/// read exactly as before and are covered by each provider's own test file already.
/// </summary>
public class UsageSnapshotStatusTests
{
    [Fact]
    public void NoData_IsAbsentOnEveryTypedValue()
    {
        var none = UsageSnapshot.None;

        Assert.Equal(UsageValueStatus.Unavailable, none.SessionPercentValue.Status);
        Assert.Equal(UsageValueStatus.Unavailable, none.WeeklyPercentValue.Status);
        Assert.Equal(UsageValueStatus.Unavailable, none.SessionResetAtUtcValue.Status);
        Assert.Equal(UsageValueStatus.Unavailable, none.CapturedAtUtcValue.Status);
        Assert.Equal(UsageValueStatus.Unavailable, none.WeeklyResetAtUtcValue.Status);
        Assert.Null(none.SessionPercentValue.Value);
        Assert.Null(none.CapturedAtUtcValue.Value);
    }

    /// <summary>Every figure here is Claude's own cache — reported, so always measured.</summary>
    public class CachedUtilizationStatus
    {
        private static readonly DateTimeOffset Fetched = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

        private static CachedUtilizationProvider For(int? fiveHour, int? sevenDay) =>
            new(() => new CachedUtilization(
                Fetched,
                "acct",
                fiveHour is { } fh ? new UtilizationBar(fh, Fetched.AddHours(5)) : null,
                sevenDay is { } sd ? new UtilizationBar(sd, Fetched.AddDays(7)) : null));

        [Fact]
        public void ReportedFigures_AreMeasured()
        {
            var snapshot = For(91, 79).GetSnapshot(Fetched.AddMinutes(2));

            Assert.Equal(UsageValueStatus.Real, snapshot.SessionPercentValue.Status);
            Assert.Equal(91, snapshot.SessionPercentValue.Value);
            Assert.Equal(UsageValueStatus.Real, snapshot.WeeklyPercentValue.Status);
            Assert.Equal(79, snapshot.WeeklyPercentValue.Value);
            Assert.Equal(UsageValueStatus.Real, snapshot.SessionResetAtUtcValue.Status);
            Assert.Equal(UsageValueStatus.Real, snapshot.WeeklyResetAtUtcValue.Status);
            Assert.Equal(UsageValueStatus.Real, snapshot.CapturedAtUtcValue.Status);
        }

        [Fact]
        public void MissingMeter_IsAbsent_NotGuessed()
        {
            var snapshot = For(91, null).GetSnapshot(Fetched.AddMinutes(2));

            Assert.Equal(UsageValueStatus.Real, snapshot.SessionPercentValue.Status);
            Assert.Equal(UsageValueStatus.Unavailable, snapshot.WeeklyPercentValue.Status);
            Assert.Null(snapshot.WeeklyPercentValue.Value);
            Assert.Equal(UsageValueStatus.Unavailable, snapshot.WeeklyResetAtUtcValue.Status);
        }
    }

    /// <summary>
    /// The two percentages and the sample's timestamp are measured; the session reset is
    /// predicted from an observed drop rather than reported, so it is estimated instead
    /// (ADR-0007). This provider never produces a weekly reset at all.
    /// </summary>
    public class PlanHistoryStatus : IDisposable
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

        [Fact]
        public void PercentagesAndCaptureTime_AreMeasured_SessionResetIsEstimated()
        {
            var path = WriteSamples(
                (Now.AddMinutes(-10), "org-a", 0, 7),
                (Now.AddMinutes(-5), "org-a", 47, 8));

            var snapshot = new PlanHistoryProvider(path).GetSnapshot(Now);

            Assert.Equal(UsageValueStatus.Real, snapshot.SessionPercentValue.Status);
            Assert.Equal(47, snapshot.SessionPercentValue.Value);
            Assert.Equal(UsageValueStatus.Real, snapshot.WeeklyPercentValue.Status);
            Assert.Equal(UsageValueStatus.Real, snapshot.CapturedAtUtcValue.Status);
            Assert.Equal(UsageValueStatus.Estimated, snapshot.SessionResetAtUtcValue.Status);
            Assert.NotNull(snapshot.SessionResetAtUtcValue.Value);
        }

        [Fact]
        public void WeeklyReset_IsAlwaysAbsent_PlanHistoryDoesNotProduceOne()
        {
            var path = WriteSamples((Now.AddMinutes(-5), "org-a", 40, 7));

            var snapshot = new PlanHistoryProvider(path).GetSnapshot(Now);

            Assert.Equal(UsageValueStatus.Unavailable, snapshot.WeeklyResetAtUtcValue.Status);
            Assert.Null(snapshot.WeeklyResetAtUtcValue.Value);
        }

        [Fact]
        public void NoDropObservedYet_SessionResetIsAbsent_NotEstimatedFromNothing()
        {
            // A single sample means no drop has been seen, so FindCurrentWindowStart (and the
            // reset it would predict) has nothing to go on.
            var path = WriteSamples((Now.AddMinutes(-5), "org-a", 0, 7));

            var snapshot = new PlanHistoryProvider(path).GetSnapshot(Now);

            Assert.Equal(UsageValueStatus.Unavailable, snapshot.SessionResetAtUtcValue.Status);
            Assert.Null(snapshot.SessionResetAtUtcValue.Value);
        }
    }

    /// <summary>
    /// Never a percentage or a reset time — only ever the latest-activity instant, modelled
    /// from local transcripts rather than reported by a vendor, matching the Estimate label
    /// the whole snapshot already carries.
    /// </summary>
    public class JsonlStatus : IDisposable
    {
        private readonly string _dir = Directory.CreateTempSubdirectory("oview-tests-").FullName;
        private readonly RollupStore _store;

        public JsonlStatus()
        {
            _store = new RollupStore(Path.Combine(_dir, "usage.db"));
        }

        public void Dispose()
        {
            _store.Dispose();
            Directory.Delete(_dir, recursive: true);
        }

        [Fact]
        public void LatestActivity_IsEstimated_PercentagesAndResetsAreAbsent()
        {
            var at = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
            var line = "{\"type\":\"assistant\",\"requestId\":\"req_A\",\"timestamp\":\"" +
                at.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") +
                "\",\"message\":{\"model\":\"claude-opus-4-8\",\"usage\":{\"input_tokens\":2," +
                "\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0,\"output_tokens\":10}}}";
            var path = Path.Combine(_dir, "session.jsonl");
            File.WriteAllText(path, line + "\n");
            _store.Ingest(TranscriptReader.ReadFile(path));

            var provider = new JsonlUsageProvider(_store, projectsRoot: null, coworkRoots: []);
            var snapshot = provider.GetSnapshot(at.AddMinutes(1));

            Assert.Equal(UsageValueStatus.Estimated, snapshot.CapturedAtUtcValue.Status);
            Assert.Equal(at, snapshot.CapturedAtUtcValue.Value);
            Assert.Equal(UsageValueStatus.Unavailable, snapshot.SessionPercentValue.Status);
            Assert.Equal(UsageValueStatus.Unavailable, snapshot.WeeklyPercentValue.Status);
            Assert.Equal(UsageValueStatus.Unavailable, snapshot.SessionResetAtUtcValue.Status);
            Assert.Equal(UsageValueStatus.Unavailable, snapshot.WeeklyResetAtUtcValue.Status);
        }

        [Fact]
        public void NothingIngested_YieldsNone()
        {
            var provider = new JsonlUsageProvider(_store, projectsRoot: null, coworkRoots: []);

            Assert.Equal(UsageSnapshot.None, provider.GetSnapshot(DateTimeOffset.UtcNow));
        }
    }
}
