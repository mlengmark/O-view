using OView.Core.Providers.PlanHistory;
using OView.Core.Storage;

namespace OView.Core.Tests;

public class WeeklyResetLogTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("oview-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void RecordingIsIdempotent_ByTimestamp()
    {
        var db = Path.Combine(_dir, "usage.db");
        var reset = new DateTimeOffset(2026, 7, 21, 6, 14, 55, TimeSpan.Zero);

        using var store = new RollupStore(db);
        store.RecordResets([reset]);
        store.RecordResets([reset]);   // same reset seen on a later poll

        Assert.Single(store.GetResets());
        Assert.Equal(reset, store.GetResets()[0]);
    }

    [Fact]
    public void ResetsAccumulate_AndPersistAcrossReopen()
    {
        var db = Path.Combine(_dir, "usage.db");
        var r1 = new DateTimeOffset(2026, 7, 14, 0, 0, 0, TimeSpan.Zero);
        var r2 = new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero);

        using (var store = new RollupStore(db))
        {
            store.RecordResets([r1]);
        }
        using (var store = new RollupStore(db))
        {
            store.RecordResets([r2]);
        }

        using var reopened = new RollupStore(db);
        var resets = reopened.GetResets();

        Assert.Equal(2, resets.Count);
        Assert.Equal(r1, resets[0]);
        Assert.Equal(r2, resets[1]);
    }

    [Fact]
    public void EndToEnd_TwoPersistedResets_ProducePrediction()
    {
        var db = Path.Combine(_dir, "usage.db");
        using var store = new RollupStore(db);
        var r1 = new DateTimeOffset(2026, 7, 14, 0, 0, 0, TimeSpan.Zero);
        var r2 = new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero);
        store.RecordResets([r1, r2]);

        var next = WeeklyResetDetector.PredictNextReset(store.GetResets(), r2.AddHours(1));

        Assert.Equal(r2.AddDays(7), next);
    }
}
