using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using OView.Core.Providers.Jsonl;

namespace OView.Core.Storage;

/// <summary>One surface, reconciled against the ledger.</summary>
/// <param name="Files">Transcript files parsed for this surface.</param>
/// <param name="Records">Assistant records read out of them, before de-duplication.</param>
/// <param name="Requests">Distinct request ids, which is what the ledger is keyed by.</param>
/// <param name="Present">Those request ids the ledger holds.</param>
/// <param name="Tokens">The four token fields summed over the distinct requests on disk.</param>
/// <param name="TokensStored">The same sum over only the ones the ledger holds.</param>
public sealed record IngestAuditSource(
    string Source, int Files, int Records, int Requests, int Present, long Tokens, long TokensStored)
{
    public int Missing => Requests - Present;
}

/// <summary>
/// Re-reads every local transcript from byte zero and asks the ledger, request id by request
/// id, whether it holds them (GitHub issue #218).
///
/// <para><b>Why this exists when the store already reports itself.</b> Every other field in the
/// bundle is a claim the store makes about its own state: how many rows it has, how far it read
/// each file, how many records that produced. All of those are written by the same code path
/// whose correctness is in question, and on an install that predates the source column they are
/// silent about the only thing being asked. This is the independent measurement — it parses the
/// files the way ingestion does and compares the result against what is actually stored, so a
/// missing surface shows up as a number rather than as an inference from two file counts.</para>
///
/// <para><b>It is not cheap and does not pretend to be.</b> The watermarks exist precisely so a
/// poll does not do this; one machine carries 563 MB of history (issue #125). So it runs only
/// from <c>--diagnose</c>, which is a deliberate command-line invocation on a machine that is
/// already misbehaving, and never from the tray's Copy diagnostics, which runs on the UI thread.
/// The elapsed time is printed so a slow report is legible as work rather than as a hang.</para>
///
/// <para><b>Strictly read-only</b>, on both inputs: the database is opened
/// <c>Mode=ReadOnly</c> so no schema migration, no journal recovery and no write can happen as a
/// side effect of diagnosing, and the transcripts are read the same way every other reader in
/// this app reads them. Producing a bundle must never change what the bundle describes.</para>
///
/// <para>Carries counts only — request ids are compared in memory and never printed, exactly as
/// ADR-0006 keeps conversation content out of the store itself.</para>
/// </summary>
public sealed record IngestAuditReport(
    IReadOnlyList<IngestAuditSource> Sources,
    long LedgerRows,
    int SharedRequests,
    TimeSpan Elapsed,
    string? Failure = null)
{
    /// <summary>
    /// The bundle was produced without this pass. Printed rather than omitted: a section that
    /// is simply absent cannot be told apart from one that failed to render, and this one is
    /// absent from most bundles by design, so it says how to get it.
    /// </summary>
    public static IngestAuditReport NotRun { get; } =
        new([], 0, 0, TimeSpan.Zero, "not run — pass --diagnose to include it");

    /// <summary>
    /// Request ids found in more than one surface's files. Ingestion de-duplicates on the id
    /// across every file (rule 4), so such a request is stored once under whichever source
    /// happened to reach it first. Non-zero here means the per-source split above is
    /// approximate by exactly that much, and saying so is cheaper than a reader discovering it
    /// from two totals that do not add up.
    /// </summary>
    public bool HasOverlap => SharedRequests > 0;

    public string ToClipboardText()
    {
        var text = new StringBuilder();

        if (Failure is { Length: > 0 } failure)
        {
            text.AppendLine($"  ingest audit  : {failure}");
            return text.ToString();
        }

        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"  ingest audit  : {LedgerRows:N0} ledger row(s) compared in {Elapsed.TotalSeconds:0.0}s"));

        foreach (var source in Sources)
        {
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"    {source.Source,-11} : {source.Files} file(s), {source.Records:N0} record(s), "
                + $"{source.Requests:N0} request(s)"));

            // The two numbers the whole pass is for, on their own line so they are not lost at
            // the end of a long one.
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"                  {source.Present:N0} in ledger, {source.Missing:N0} MISSING, "
                + $"{source.TokensStored:N0} of {source.Tokens:N0} tokens stored"));
        }

        if (HasOverlap)
        {
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"    shared      : {SharedRequests:N0} request(s) appear under more than one source"));
        }

        return text.ToString();
    }

    /// <summary>Audits the real machine layout — every root ingestion actually reads.</summary>
    public static IngestAuditReport Run() =>
        Run(null, ClaudeProjectsLocator.DefaultRoot, CoworkAuditLocator.DefaultRoots);

    /// <summary>
    /// Overload taking explicit inputs so the pass is testable against a synthetic layout.
    /// Mirrors <see cref="JsonlUsageProvider"/>'s own constructor: a null projects root and an
    /// empty Cowork list each skip that source outright, and neither falls back to a machine
    /// default — naming one root while the other silently resolved to a real directory once
    /// made a test read this developer's actual Cowork history.
    /// </summary>
    public static IngestAuditReport Run(
        string? dbPath, string? projectsRoot, IReadOnlyList<string> coworkRoots)
    {
        var clock = Stopwatch.StartNew();
        var path = dbPath ?? RollupStore.DefaultPath;

        Dictionary<string, long> ledger;
        try
        {
            if (!File.Exists(path))
            {
                return new IngestAuditReport([], 0, 0, clock.Elapsed, "no database file yet");
            }

            ledger = ReadLedger(path);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            return new IngestAuditReport([], 0, 0, clock.Elapsed, $"{ex.GetType().Name}: {ex.Message}");
        }

        var files = new (string Source, IReadOnlyList<string> Paths)[]
        {
            (TranscriptSources.ClaudeCode,
                projectsRoot is null ? [] : ClaudeProjectsLocator.FindTranscripts(projectsRoot)),
            (TranscriptSources.Cowork,
                CoworkAuditLocator.FindTranscripts(coworkRoots).Distinct(PathIdentity.Comparer).ToList()),
        };

        var sources = new List<IngestAuditSource>();
        var seenAnywhere = new HashSet<string>(StringComparer.Ordinal);
        var shared = 0;

        foreach (var (source, paths) in files)
        {
            // Last occurrence wins, exactly as ingestion's upsert leaves it: streaming writes
            // the same request several times and only the final one is complete (rule 4). An
            // audit that summed them instead would report a token total no build has ever
            // stored, and then blame the store for the difference.
            var latest = new Dictionary<string, TranscriptRecord>(StringComparer.Ordinal);
            var records = 0;

            foreach (var file in paths)
            {
                foreach (var record in TranscriptReader.ReadFile(file))
                {
                    records++;
                    latest[record.RequestId] = record;
                }
            }

            long tokens = 0;
            long tokensStored = 0;
            var present = 0;

            foreach (var (id, record) in latest)
            {
                var value = record.InputTokens + record.CacheCreationTokens
                            + record.CacheReadTokens + record.OutputTokens;
                tokens += value;

                if (ledger.ContainsKey(id))
                {
                    present++;
                    tokensStored += value;
                }

                if (!seenAnywhere.Add(id))
                {
                    shared++;
                }
            }

            sources.Add(new IngestAuditSource(
                source, paths.Count, records, latest.Count, present, tokens, tokensStored));
        }

        return new IngestAuditReport(sources, ledger.Count, shared, clock.Elapsed);
    }

    /// <summary>
    /// Every stored request id. Read whole rather than probed per id: one pass over the ledger
    /// beats tens of thousands of parameterised lookups, and the ids are short.
    ///
    /// <para><c>Mode=ReadOnly</c> is load-bearing. Opening this store the ordinary way would run
    /// the schema migration and let SQLite fold in any journal beside it — so the act of
    /// diagnosing a suspected rollback (#213) would perform one.</para>
    /// </summary>
    private static Dictionary<string, long> ReadLedger(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT request_id, input_tokens + cache_creation_tokens + cache_read_tokens + output_tokens "
            + "FROM ingested_requests";

        var ledger = new Dictionary<string, long>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            ledger[reader.GetString(0)] = reader.GetInt64(1);
        }

        return ledger;
    }
}
