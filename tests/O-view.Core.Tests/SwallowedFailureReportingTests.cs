using OView.Core.Models;
using OView.Core.Providers;
using OView.Core.Providers.Jsonl;
using OView.Core.Providers.PlanHistory;
using OView.Core.Storage;

namespace OView.Core.Tests;

/// <summary>
/// Four places swallow a failure and carry on. Every one of them is right to — this is a
/// monitoring tool and a bad provider must not blank the panel — but each was also silent,
/// and silence is what made a five-day ingestion stall undiagnosable from the outside.
///
/// <para>These pin the distinction the field case turned on: <b>degrading quietly is the
/// contract; degrading invisibly is the bug.</b> So each test asserts both halves — the
/// failure is still absorbed, and it is now named.</para>
/// </summary>
public class SwallowedFailureReportingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("oview-swallow-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        GC.SuppressFinalize(this);
    }

    private sealed class ThrowingProvider(Exception ex) : IUsageProvider
    {
        public UsageSnapshot GetSnapshot(DateTimeOffset utcNow) => throw ex;
    }

    private sealed class FixedProvider(UsageSnapshot snapshot) : IUsageProvider
    {
        public UsageSnapshot GetSnapshot(DateTimeOffset utcNow) => snapshot;
    }

    // ── the composite's own catch ───────────────────────────────────────────────────

    /// <summary>
    /// The field case exactly: one provider throwing on every poll while a sibling keeps the
    /// panel looking healthy. The chain must still fall through — and must now say which
    /// provider went, because "no data" and "threw for five days" are the same blank.
    /// </summary>
    [Fact]
    public void AProviderThatThrowsIsStillAbsorbedAndIsNowNamed()
    {
        var lines = new List<string>();
        var healthy = new UsageSnapshot(DataSource.Live, 42, 7, null, DateTimeOffset.UnixEpoch);

        var composite = new CompositeUsageProvider(
            new ThrowingProvider(new InvalidOperationException("store is unreachable")),
            new FixedProvider(healthy))
        {
            Log = lines.Add,
        };

        var snapshot = composite.GetSnapshot(DateTimeOffset.UnixEpoch);

        // Absorbed: the healthy sibling still wins and the panel is unaffected.
        Assert.Equal(42, snapshot.SessionPercent);

        // Named: the provider type and the failure both appear.
        var line = Assert.Single(lines);
        Assert.Contains("ThrowingProvider", line, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", line, StringComparison.Ordinal);
        Assert.Contains("store is unreachable", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// No log wired is the ordinary case for a caller that does not want one, and it must not
    /// change behaviour — the seam is a report, not a dependency.
    /// </summary>
    [Fact]
    public void WithNoLogTheCompositeStillAbsorbsTheFailure()
    {
        var composite = new CompositeUsageProvider(
            new ThrowingProvider(new InvalidOperationException("boom")));

        Assert.Equal(UsageSnapshot.None, composite.GetSnapshot(DateTimeOffset.UnixEpoch));
    }

    // ── ingestion ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Ingestion has three ways to produce nothing — no files found, files found but none
    /// grown, or reading threw — and from outside all three are a token tile that does not
    /// move. The summary separates them, and the "nothing found" case is the one that most
    /// needs saying, because it is indistinguishable from a quiet machine.
    /// </summary>
    [Fact]
    public void IngestionReportsWhenItFindsNoTranscriptsAtAll()
    {
        var lines = new List<string>();
        using var store = new RollupStore(Path.Combine(_dir, "usage.db"));

        var provider = new JsonlUsageProvider(store, projectsRoot: null, coworkRoots: [])
        {
            Log = lines.Add,
        };

        provider.GetSnapshot(DateTimeOffset.UnixEpoch);

        var line = Assert.Single(lines);
        Assert.Contains("jsonl sync: 0 file(s)", line, StringComparison.Ordinal);
        Assert.Contains("0 record(s) ingested", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The healthy case, so the line is trustworthy when it reports work rather than only
    /// when it reports none. A summary that only ever says "0" cannot be told from a summary
    /// that is broken.
    /// </summary>
    [Fact]
    public void IngestionReportsTheFilesItReadAndTheRecordsItStored()
    {
        var lines = new List<string>();
        var projects = Directory.CreateDirectory(Path.Combine(_dir, "projects")).FullName;

        File.WriteAllText(Path.Combine(projects, "session.jsonl"), string.Join('\n',
        [
            AssistantRecord("req-1", "2026-08-25T10:00:00Z", 120),
            AssistantRecord("req-2", "2026-08-25T10:01:00Z", 240),
            "",
        ]));

        using var store = new RollupStore(Path.Combine(_dir, "usage.db"));
        var provider = new JsonlUsageProvider(store, projects, []) { Log = lines.Add };

        provider.GetSnapshot(DateTimeOffset.UnixEpoch);

        var line = Assert.Single(lines);
        Assert.Contains("jsonl sync: 1 file(s)", line, StringComparison.Ordinal);
        Assert.Contains("1 changed", line, StringComparison.Ordinal);
        Assert.Contains("2 record(s) ingested", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The second poll over an unchanged file reports zero changed rather than zero found —
    /// which is precisely the distinction between "stuck" and "nothing new", and the question
    /// a stalled machine needs answered first.
    /// </summary>
    [Fact]
    public void AnUnchangedFileIsReportedAsSeenButNotChanged()
    {
        var lines = new List<string>();
        var projects = Directory.CreateDirectory(Path.Combine(_dir, "projects")).FullName;
        File.WriteAllText(Path.Combine(projects, "session.jsonl"),
            AssistantRecord("req-1", "2026-08-25T10:00:00Z", 120) + "\n");

        using var store = new RollupStore(Path.Combine(_dir, "usage.db"));
        var provider = new JsonlUsageProvider(store, projects, []) { Log = lines.Add };

        provider.GetSnapshot(DateTimeOffset.UnixEpoch);
        provider.GetSnapshot(DateTimeOffset.UnixEpoch);

        Assert.Contains("1 file(s), 0 changed", lines[^1], StringComparison.Ordinal);
    }

    // ── the weekly-reset log ────────────────────────────────────────────────────────

    /// <summary>
    /// The weekly anchor's write was the quietest failure of the lot: a bundle reported six
    /// observations while the file on disk held five and had not been written for five days.
    /// The store behind it changed under ADR-0014; the obligation did not.
    /// </summary>
    [Fact]
    public void AFailedWeeklyAnchorWriteIsAbsorbedAndIsNowNamed()
    {
        var lines = new List<string>();

        // A directory where the file belongs: the write cannot succeed and must not throw.
        var target = Path.Combine(_dir, "weekly-reset.json");
        Directory.CreateDirectory(target);

        var anchor = new WeeklyResetAnchor(target) { Log = lines.Add };

        var thrown = Record.Exception(() => anchor.Save(DateTimeOffset.UnixEpoch, "org-a"));

        Assert.Null(thrown);
        Assert.Contains(lines, l => l.Contains("weekly reset anchor write FAILED", StringComparison.Ordinal));
    }

    /// <summary>
    /// And the success case, so a silent run reads as "nothing changed" rather than "the write
    /// is failing again" — which matters more here than it used to, because the anchor is
    /// written once and then stays quiet for good.
    /// </summary>
    [Fact]
    public void AStoredWeeklyAnchorSaysWhatItRecorded()
    {
        var lines = new List<string>();
        var anchor = new WeeklyResetAnchor(Path.Combine(_dir, "weekly-reset.json")) { Log = lines.Add };

        Assert.True(anchor.Save(new DateTimeOffset(2026, 8, 24, 20, 59, 59, TimeSpan.Zero), "org-a"));

        Assert.Contains(lines, l => l.Contains("weekly reset anchor stored", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("Monday", StringComparison.Ordinal));
    }

    /// <summary>
    /// Claude reports the same boundary with a different date every week, so an anchor already
    /// on that grid is not news. Rewriting the file on every refresh forever would make its
    /// timestamp meaningless — and that timestamp is how a support bundle dates the anchor.
    /// </summary>
    [Fact]
    public void AnAnchorAlreadyOnTheSameGridIsNotRewritten()
    {
        var anchor = new WeeklyResetAnchor(Path.Combine(_dir, "weekly-reset.json"));
        var first = new DateTimeOffset(2026, 8, 24, 20, 59, 59, TimeSpan.Zero);

        Assert.True(anchor.Save(first, "org-a"));
        Assert.False(anchor.Save(first.AddDays(7), "org-a"));   // next week, same schedule
        Assert.True(anchor.Save(first.AddDays(2), "org-a"));    // a different weekday is news
    }

    /// <summary>
    /// One assistant record. Not called <c>Record</c>: that shadows xUnit's
    /// <see cref="Xunit.Record"/> helper, which this file uses to assert that a failure is
    /// absorbed rather than thrown.
    /// </summary>
    private static string AssistantRecord(string requestId, string timestamp, int outputTokens) =>
        "{\"type\":\"assistant\",\"requestId\":\"" + requestId + "\","
        + "\"timestamp\":\"" + timestamp + "\","
        + "\"message\":{\"model\":\"claude-opus-5\",\"usage\":"
        + "{\"input_tokens\":10,\"output_tokens\":" + outputTokens + "}}}";
}
