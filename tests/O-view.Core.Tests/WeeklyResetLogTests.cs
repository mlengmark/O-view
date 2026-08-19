using OView.Core.Providers.PlanHistory;
using OView.Core.Storage;

namespace OView.Core.Tests;

public class WeeklyResetLogTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("oview-tests-").FullName;
    private const string Org = "org-a";

    private string LogPath => Path.Combine(_dir, "weekly-resets.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static WeeklyResetObservation Observation(string utc, TimeSpan bracket, string org = Org)
    {
        var latest = DateTimeOffset.Parse(utc, null,
            System.Globalization.DateTimeStyles.AssumeUniversal |
            System.Globalization.DateTimeStyles.AdjustToUniversal);
        return new WeeklyResetObservation(latest - bracket, latest, org);
    }

    [Fact]
    public void UnwrittenLog_ReadsAsNothingObserved()
    {
        Assert.Empty(new WeeklyResetLog(LogPath).GetObservations());
    }

    [Fact]
    public void ObservationsPersistAcrossReopen()
    {
        var first = Observation("2026-07-21T06:14:55Z", TimeSpan.FromHours(9));
        var second = Observation("2026-07-28T06:28:57Z", TimeSpan.FromHours(10));

        new WeeklyResetLog(LogPath).Record([first]);
        new WeeklyResetLog(LogPath).Record([second]);

        var stored = new WeeklyResetLog(LogPath).GetObservations();

        Assert.Equal(2, stored.Count);
        Assert.Equal(first, stored[0]);
        Assert.Equal(second, stored[1]);
    }

    [Fact]
    public void RecordingIsIdempotent_TheSameResetSeenEveryPoll()
    {
        var log = new WeeklyResetLog(LogPath);
        var reset = Observation("2026-07-28T06:28:57Z", TimeSpan.FromHours(10));

        for (var poll = 0; poll < 20; poll++)
        {
            log.Record([reset]);
        }

        Assert.Single(log.GetObservations());
    }

    [Fact]
    public void ReObservingAReset_TightensItsBracket_RatherThanDuplicatingIt()
    {
        // Later polls can see the same drop with a nearer preceding sample, once Desktop
        // has been running a while. Both brackets contain the reset, so their overlap does.
        var log = new WeeklyResetLog(LogPath);
        log.Record([Observation("2026-07-28T06:28:57Z", TimeSpan.FromHours(10))]);
        log.Record([Observation("2026-07-28T06:28:57Z", TimeSpan.FromMinutes(5))]);

        var stored = Assert.Single(log.GetObservations());

        Assert.Equal(TimeSpan.FromMinutes(5), stored.Uncertainty);
        Assert.True(stored.IsPrecise);
    }

    [Fact]
    public void DistinctWeeklyResets_AreNotMergedTogether()
    {
        var log = new WeeklyResetLog(LogPath);
        log.Record([
            Observation("2026-07-21T06:14:55Z", TimeSpan.FromHours(9)),
            Observation("2026-07-28T06:28:57Z", TimeSpan.FromHours(10)),
        ]);

        Assert.Equal(2, log.GetObservations().Count);
    }

    /// <summary>
    /// Issue #136, with the exact brackets that produced it on the development machine.
    ///
    /// <para>Desktop was closed for a week at a time, so each reset is bracketed ~6.7 days
    /// wide. The two brackets end up disjoint by <b>47 minutes</b> — close enough that the
    /// old 12-hour proximity rule called them one reset, computed an inverted intersection,
    /// and discarded the earlier one. The 2026-08-03 reset was detected and then thrown away,
    /// and a weekly reset costs a week to re-observe.</para>
    ///
    /// <para>Written with literal timestamps rather than the <c>Observation</c> helper
    /// because the 47-minute separation is the entire point and deriving it would hide it.</para>
    /// </summary>
    [Fact]
    public void TwoWeekWideBrackets_AreSeparateResets_EvenWhenTheyNearlyTouch()
    {
        var log = new WeeklyResetLog(LogPath);

        // sd 59 -> 3 across a 162.9 h sampling gap.
        var earlier = new WeeklyResetObservation(
            DateTimeOffset.Parse("2026-08-03T20:16:00Z"),
            DateTimeOffset.Parse("2026-08-10T15:07:00Z"), Org);

        // sd 3 -> 0 across a 158.4 h gap, starting 47 minutes after the first bracket ends.
        var later = new WeeklyResetObservation(
            DateTimeOffset.Parse("2026-08-10T15:54:00Z"),
            DateTimeOffset.Parse("2026-08-17T06:17:00Z"), Org);

        log.Record([earlier]);
        log.Record([later]);

        var stored = log.GetObservations(Org);

        Assert.Equal(2, stored.Count);
        Assert.Contains(stored, o => o.EarliestUtc == earlier.EarliestUtc && o.LatestUtc == earlier.LatestUtc);
        Assert.Contains(stored, o => o.EarliestUtc == later.EarliestUtc && o.LatestUtc == later.LatestUtc);
    }

    /// <summary>
    /// The other half of #136, and the reason the fix is not simply "keep everything
    /// disjoint". Two <i>narrow</i> brackets an hour apart cannot be two weekly resets — no
    /// cadence puts resets a day apart — so they still contradict, and the tighter still
    /// wins. Removing the proximity rule must not lose this.
    /// </summary>
    [Fact]
    public void TwoNarrowBracketsAnHourApart_StillContradict_AndTheTighterWins()
    {
        var log = new WeeklyResetLog(LogPath);

        var loose = Observation("2026-07-28T06:00:00Z", TimeSpan.FromHours(2));
        var tight = Observation("2026-07-28T09:00:00Z", TimeSpan.FromMinutes(10));

        log.Record([loose]);
        log.Record([tight]);

        var stored = log.GetObservations(Org);

        Assert.Single(stored);
        Assert.Equal(tight.LatestUtc, stored[0].LatestUtc);
    }

    [Fact]
    public void ObservationsAreScopedByOrg()
    {
        var log = new WeeklyResetLog(LogPath);
        log.Record([
            Observation("2026-07-28T06:28:57Z", TimeSpan.FromHours(10), "org-a"),
            Observation("2026-07-28T09:00:00Z", TimeSpan.FromHours(1), "org-b"),
        ]);

        Assert.Equal(2, log.GetObservations().Count);
        Assert.Single(log.GetObservations("org-a"));
        Assert.Single(log.GetObservations("org-b"));
    }

    [Fact]
    public void CorruptLog_DegradesToEmpty_AndIsRewritable()
    {
        File.WriteAllText(LogPath, "{ not json at all");

        var log = new WeeklyResetLog(LogPath);
        Assert.Empty(log.GetObservations());

        // Discovery simply refills it — a log we cannot read must not be a dead end.
        log.Record([Observation("2026-07-28T06:28:57Z", TimeSpan.FromHours(10))]);
        Assert.Single(new WeeklyResetLog(LogPath).GetObservations());
    }

    [Fact]
    public void InvertedEntries_AreRejectedOnRead()
    {
        File.WriteAllText(LogPath, """
            { "version": 1, "observations": [
              { "earliest": "2026-07-28T12:00:00Z", "latest": "2026-07-28T06:00:00Z", "org": "org-a" },
              { "earliest": "2026-07-28T06:00:00Z", "latest": "2026-07-28T06:05:00Z", "org": "org-a" }
            ] }
            """);

        Assert.Single(new WeeklyResetLog(LogPath).GetObservations());
    }

    [Fact]
    public void TheLogIsBounded()
    {
        var log = new WeeklyResetLog(LogPath);
        var start = DateTimeOffset.Parse("2020-01-01T00:00:00Z", null,
            System.Globalization.DateTimeStyles.AssumeUniversal |
            System.Globalization.DateTimeStyles.AdjustToUniversal);

        log.Record(Enumerable.Range(0, WeeklyResetLog.MaxObservations + 20)
            .Select(week => new WeeklyResetObservation(
                start.AddDays(7 * week).AddMinutes(-5), start.AddDays(7 * week), Org)));

        var stored = log.GetObservations();
        Assert.Equal(WeeklyResetLog.MaxObservations, stored.Count);
        // The oldest are dropped, not the newest.
        Assert.Equal(start.AddDays(7 * (WeeklyResetLog.MaxObservations + 19)), stored[^1].LatestUtc);
    }

    [Fact]
    public void LegacyRowsAreImported_AsPreciseObservations()
    {
        // Pre-ADR-0011 rows were single instants, and only the in-cadence detector could
        // write them — so they carry a sampling-width bracket, not a fabricated exact one.
        var legacy = DateTimeOffset.Parse("2026-07-21T06:14:55Z", null,
            System.Globalization.DateTimeStyles.AssumeUniversal |
            System.Globalization.DateTimeStyles.AdjustToUniversal);

        var log = new WeeklyResetLog(LogPath);
        log.ImportLegacy([legacy], Org);
        log.ImportLegacy([legacy], Org);   // runs on every launch; must not duplicate

        var stored = Assert.Single(log.GetObservations());
        Assert.Equal(legacy, stored.LatestUtc);
        Assert.True(stored.IsPrecise);
    }

    [Fact]
    public void EndToEnd_OnePersistedReset_ProducesAPrediction()
    {
        var log = new WeeklyResetLog(LogPath);
        log.Record([Observation("2026-07-28T06:28:57Z", TimeSpan.FromHours(10))]);

        var forecast = WeeklyResetDetector.PredictNextReset(
            log.GetObservations(Org),
            DateTimeOffset.Parse("2026-07-28T12:00:00Z", null,
                System.Globalization.DateTimeStyles.AssumeUniversal |
                System.Globalization.DateTimeStyles.AdjustToUniversal));

        Assert.NotNull(forecast);
        Assert.Equal(log.GetObservations()[0].LatestUtc.AddDays(7), forecast.AtUtc);
    }
}
