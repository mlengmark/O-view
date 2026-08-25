using OView.Core.Providers.Jsonl;
using OView.Core.Storage;

namespace OView.Core.Tests;

/// <summary>
/// The bundle's view of the rollup store. Every other field in a support bundle describes an
/// <i>input</i> — which paths exist, how many samples they hold, how fresh they are — and all
/// of them can read perfectly while the token tiles have not moved in a week. Three reports in
/// a row led with <c>status : Ok</c> while exactly that was happening.
/// </summary>
public class RollupStoreReportTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("oview-storereport-").FullName;

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

    private string DbPath => Path.Combine(_dir, "usage.db");

    private static string AssistantRecord(string requestId, string timestamp) =>
        "{\"type\":\"assistant\",\"requestId\":\"" + requestId + "\","
        + "\"timestamp\":\"" + timestamp + "\","
        + "\"message\":{\"model\":\"claude-opus-5\",\"usage\":"
        + "{\"input_tokens\":10,\"output_tokens\":120}}}";

    /// <summary>
    /// A store that has ingested something reports what it holds. The ledger's span and its
    /// newest timestamp are the two figures that answer "has this stalled, and since when".
    /// </summary>
    [Fact]
    public void TheReportNamesWhatTheLedgerHolds()
    {
        var projects = Directory.CreateDirectory(Path.Combine(_dir, "projects")).FullName;
        File.WriteAllText(Path.Combine(projects, "session.jsonl"),
            AssistantRecord("req-1", "2026-08-20T19:21:47Z") + "\n");

        using (var store = new RollupStore(DbPath))
        {
            new JsonlUsageProvider(store, projects, []).GetSnapshot(DateTimeOffset.UnixEpoch);
        }

        var report = RollupStoreReport.Inspect(DbPath);

        Assert.Equal(1, report.LedgerRows);
        Assert.Equal("2026-08-20", report.FirstDay);
        Assert.Equal("2026-08-20", report.LastDay);
        Assert.Contains("2026-08-20", report.NewestTimestamp!, StringComparison.Ordinal);
        Assert.Equal("ok", report.Integrity);
        Assert.True(report.WritesAccepted);
    }

    /// <summary>
    /// <b>The field this whole report exists for.</b> A tracked transcript longer than the
    /// length recorded beside its offset is content the app believes it has accounted for and
    /// has not read — the signature of a stalled ingest, and visible nowhere else. Measured in
    /// the field as 409,501 bytes across two files while the panel looked healthy.
    /// </summary>
    [Fact]
    public void ATranscriptThatGrewSinceTheLastIngestIsCountedAsBehind()
    {
        var projects = Directory.CreateDirectory(Path.Combine(_dir, "projects")).FullName;
        var transcript = Path.Combine(projects, "session.jsonl");
        File.WriteAllText(transcript, AssistantRecord("req-1", "2026-08-20T10:00:00Z") + "\n");

        using (var store = new RollupStore(DbPath))
        {
            new JsonlUsageProvider(store, projects, []).GetSnapshot(DateTimeOffset.UnixEpoch);
        }

        // Grown since. Nothing has read it, and nothing else in a bundle would say so.
        var appended = AssistantRecord("req-2", "2026-08-25T10:00:00Z") + "\n";
        File.AppendAllText(transcript, appended);

        var report = RollupStoreReport.Inspect(DbPath);

        Assert.Equal(1, report.TrackedFiles);
        Assert.Equal(1, report.FilesBehind);
        Assert.Equal(appended.Length, report.UnreadBytes);
    }

    /// <summary>A transcript Claude Code has since deleted is counted, not mistaken for behind.</summary>
    [Fact]
    public void ATranscriptThatHasSinceVanishedIsCountedSeparately()
    {
        var projects = Directory.CreateDirectory(Path.Combine(_dir, "projects")).FullName;
        var transcript = Path.Combine(projects, "session.jsonl");
        File.WriteAllText(transcript, AssistantRecord("req-1", "2026-08-20T10:00:00Z") + "\n");

        using (var store = new RollupStore(DbPath))
        {
            new JsonlUsageProvider(store, projects, []).GetSnapshot(DateTimeOffset.UnixEpoch);
        }

        File.Delete(transcript);

        var report = RollupStoreReport.Inspect(DbPath);

        Assert.Equal(1, report.FilesGone);
        Assert.Equal(0, report.FilesBehind);
        Assert.Equal(0, report.UnreadBytes);
    }

    /// <summary>
    /// The two readers label themselves differently, and that is the point: a bundle from the
    /// running app reports the store through the connection that app holds, while
    /// <c>--diagnose</c> opens its own. They should agree, and a disagreement is evidence
    /// rather than confusion.
    /// </summary>
    [Fact]
    public void TheLiveInstanceAndAFreshReaderIdentifyThemselves()
    {
        using var store = new RollupStore(DbPath);

        Assert.Equal(RollupStoreReport.LiveInstance, store.Inspect().Origin);
        Assert.Equal(RollupStoreReport.OpenedForReport, RollupStoreReport.Inspect(DbPath).Origin);
    }

    /// <summary>
    /// A brand-new install has no database yet. That is ordinary, must not throw, and must be
    /// stated rather than rendered as an empty section — an absent field is indistinguishable
    /// from one that failed to render.
    /// </summary>
    [Fact]
    public void AMissingDatabaseIsReportedRatherThanThrown()
    {
        var report = RollupStoreReport.Inspect(Path.Combine(_dir, "never-created.db"));

        Assert.NotNull(report.Failure);
        Assert.Contains("unreadable", report.ToClipboardText(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Writes are probed rather than assumed. <c>PRAGMA quick_check</c> is a read, so a store
    /// that reads perfectly and refuses every write passes it and then throws on every ingest —
    /// which is precisely the shape that stayed invisible behind a silent catch.
    /// </summary>
    [Fact]
    public void TheReportProbesWritesAndNotJustIntegrity()
    {
        using var store = new RollupStore(DbPath);

        var text = store.Inspect().ToClipboardText();

        Assert.Contains("integrity ok", text, StringComparison.Ordinal);
        Assert.Contains("writes accepted", text, StringComparison.Ordinal);
    }
}
