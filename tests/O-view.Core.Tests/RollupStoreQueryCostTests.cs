using OView.Core.Pricing;
using System.Diagnostics;
using OView.Core.Providers.Jsonl;
using OView.Core.Storage;
using Xunit.Abstractions;

namespace OView.Core.Tests;

/// <summary>
/// What local-day bucketing costs to query (issue #211).
///
/// <para>The concern was concrete and worth answering rather than waving through: filtering on
/// <c>last_timestamp</c> instead of the indexed <c>utc_date</c> gives up
/// <c>ix_requests_date</c>, and SQLite cannot do the timezone conversion, so the rows arrive
/// at request grain and are grouped in C#. That is more work per panel open than a
/// <c>GROUP BY</c> over an indexed column.</para>
///
/// <para><b>The measurement is taken against a synthetic ledger the size of a real one</b> —
/// the development machine's held 6,917 rows when the issue was written. It is deliberately
/// <i>not</i> taken against that real store: reading it means opening it, and issue #213 is
/// about an orphaned journal on exactly that file turning an open into permanent data
/// loss.</para>
///
/// <para><b>What this asserts, and what it does not.</b> The bound is loose on purpose — a
/// wall-clock budget on a shared CI runner cannot be tight without becoming the flakiest test
/// in the suite, which is the failure mode issue #212 is about. It catches the shape of
/// regression that would matter here: a query that has gone super-linear, or one that has
/// quietly started scanning all of history for a 31-day window. It is not a benchmark, and the
/// numbers it prints are not a performance claim.</para>
/// </summary>
public class RollupStoreQueryCostTests : IDisposable
{
    /// <summary>The development machine's ledger, rounded up — see the class summary.</summary>
    private const int RealisticLedgerRows = 7_000;

    /// <summary>
    /// Generous enough to survive a cold, contended runner; tight enough that a query which
    /// stopped using the window would blow through it. A 31-day slice of this ledger is a few
    /// hundred rows.
    /// </summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(1);

    private static readonly TimeZoneInfo PlusTwo =
        TimeZoneInfo.CreateCustomTimeZone("test-plus-2", TimeSpan.FromHours(2), "UTC+2", "UTC+2");

    private readonly string _dir = Directory.CreateTempSubdirectory("oview-tests-").FullName;
    private readonly RollupStore _store;

    /// <summary>
    /// Where the figures go. A measurement nobody can read is an assertion pretending to be
    /// one — <c>dotnet test --logger "console;verbosity=detailed"</c> prints these, so the
    /// numbers behind the bound can be quoted rather than guessed at.
    /// </summary>
    private readonly ITestOutputHelper _output;

    public RollupStoreQueryCostTests(ITestOutputHelper output)
    {
        _output = output;
        _store = new RollupStore(Path.Combine(_dir, "usage.db"));

        // A year of history at a realistic density, so the 31-day window is a small slice of a
        // much larger table — which is the case the range scan has to stay cheap in.
        var models = new[] { "claude-opus-4-8", "claude-sonnet-5", "claude-fable-5" };
        var start = new DateTimeOffset(2025, 9, 1, 0, 0, 0, TimeSpan.Zero);

        _store.Ingest(Enumerable.Range(0, RealisticLedgerRows).Select(i => new TranscriptRecord(
            $"req-{i}",
            start.AddMinutes(i * 75),
            models[i % models.Length],
            new TokenSplit(10, 0, 100, 0, 200, 30),
            UsageModifiers.Standard)));
    }

    public void Dispose()
    {
        _store.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void A31DayWindowStaysCheapOnARealisticLedger()
    {
        var now = new DateTimeOffset(2026, 8, 25, 23, 26, 0, TimeSpan.Zero);
        var from = now.AddDays(-31);

        // Warm the connection and the page cache first: the first query on a fresh store pays
        // for opening it, which is not what is being measured.
        _ = _store.GetDailyRollups(from, now, PlusTwo);

        var elapsed = Time(() => _store.GetDailyRollups(from, now, PlusTwo));
        _output.WriteLine(
            $"31-day window over {RealisticLedgerRows:N0} rows: " +
            $"{elapsed.TotalMilliseconds / Repeats:0.00} ms per read ({Repeats} reads)");

        Assert.InRange(elapsed, TimeSpan.Zero, Budget);
    }

    /// <summary>
    /// The window is doing the work, not the grouping: reading a 31-day slice must not cost
    /// what reading the whole ledger costs. This is the assertion that would fail if the range
    /// predicate were ever dropped or made unusable by an index — the failure that turns a
    /// panel open into a full-table scan, and which grows silently with the store.
    /// </summary>
    [Fact]
    public void AWindowedReadIsCheaperThanReadingAllOfHistory()
    {
        var now = new DateTimeOffset(2026, 8, 25, 23, 26, 0, TimeSpan.Zero);

        _ = _store.GetDailyRollups(now.AddDays(-31), now, PlusTwo);

        var windowed = Time(() => _store.GetDailyRollups(now.AddDays(-31), now, PlusTwo));
        var everything = Time(() => _store.GetDailyRollups(DateTimeOffset.MinValue, now, PlusTwo));

        _output.WriteLine(
            $"per read — 31 days: {windowed.TotalMilliseconds / Repeats:0.00} ms · " +
            $"whole ledger: {everything.TotalMilliseconds / Repeats:0.00} ms");

        Assert.True(
            windowed < everything,
            $"a 31-day read took {windowed.TotalMilliseconds:0.0} ms against " +
            $"{everything.TotalMilliseconds:0.0} ms for the whole ledger — the window is not " +
            "narrowing the scan");
    }

    /// <summary>Reads per measurement — enough that the timer's resolution is not the answer.</summary>
    private const int Repeats = 20;

    private static TimeSpan Time(Action work)
    {
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < Repeats; i++)
        {
            work();
        }
        return clock.Elapsed;
    }
}
