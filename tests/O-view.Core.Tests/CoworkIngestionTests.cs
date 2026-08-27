using System.Diagnostics;
using OView.Core.Providers;
using OView.Core.Providers.Jsonl;
using OView.Core.Storage;

namespace OView.Core.Tests;

/// <summary>
/// Cowork writes its transcript to `audit.jsonl` inside a sandboxed session home, using
/// `request_id` where Claude Code uses `requestId`. Both differences fail silently —
/// wrong folder or wrong key yields zero rows and an empty tile, never an error — so
/// each gets an explicit test (GitHub issue #44).
/// </summary>
public class CoworkIngestionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("oview-cowork-").FullName;
    private readonly RollupStore _store;

    public CoworkIngestionTests()
    {
        _store = new RollupStore(Path.Combine(_dir, "usage.db"));
    }

    public void Dispose()
    {
        _store.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A broken junction left by a failed test cannot be removed recursively.
        }
    }

    /// <summary>An audit record as Cowork writes it: snake_case id, same usage shape.</summary>
    private static string AuditLine(string requestId, string timestamp, long output) =>
        $"{{\"type\":\"assistant\",\"request_id\":\"{requestId}\",\"timestamp\":\"{timestamp}\"," +
        $"\"message\":{{\"model\":\"claude-sonnet-5\",\"usage\":{{\"input_tokens\":2," +
        $"\"cache_creation_input_tokens\":100,\"cache_read_input_tokens\":200,\"output_tokens\":{output}}}}}}}";

    private long TotalOutputTokens() =>
        _store.GetDailyRollups(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, TimeZoneInfo.Utc)
            .Sum(r => r.OutputTokens);

    /// <summary>Builds a realistic Cowork layout: sandbox home, empty projects dir, audit log.</summary>
    private string WriteCoworkSession(string sessionId, params string[] lines)
    {
        var session = Path.Combine(_dir, "local-agent-mode-sessions", "org", "user", sessionId);

        // The sandbox really does contain this, and it really is always empty — that is
        // exactly why the miss was invisible.
        Directory.CreateDirectory(Path.Combine(session, ".claude", "projects"));

        Directory.CreateDirectory(session);
        var path = Path.Combine(session, CoworkAuditLocator.AuditFileName);
        File.WriteAllLines(path, lines);
        return path;
    }

    private string CoworkRoot => Path.Combine(_dir, "local-agent-mode-sessions");

    // ── The key-name trap ──────────────────────────────────────────────────────
    [Fact]
    public void AuditRecord_WithSnakeCaseRequestId_IsParsed()
    {
        var path = WriteCoworkSession("local_a", AuditLine("req_A", "2026-07-20T12:00:00.000Z", output: 120));

        var records = TranscriptReader.ReadFile(path);

        Assert.Single(records);
        Assert.Equal("req_A", records[0].RequestId);
        Assert.Equal(120, records[0].OutputTokens);
        Assert.Equal("claude-sonnet-5", records[0].Model);
    }

    [Fact]
    public void AuditRecord_DuplicateRequestIds_DeduplicatedLikeTranscripts()
    {
        // CLAUDE.md rule 4 applies to audit logs too — streaming writes the id repeatedly.
        var path = WriteCoworkSession("local_a",
            AuditLine("req_A", "2026-07-20T12:00:00.000Z", output: 10),
            AuditLine("req_A", "2026-07-20T12:00:01.000Z", output: 120),
            AuditLine("req_B", "2026-07-20T12:05:00.000Z", output: 80));

        _store.Ingest(TranscriptReader.ReadFile(path));

        Assert.Equal(200, TotalOutputTokens());
    }

    // ── The wrong-folder trap ──────────────────────────────────────────────────
    [Fact]
    public void CoworkAuditLogs_AreFound_EvenThoughSandboxProjectsDirIsEmpty()
    {
        WriteCoworkSession("local_a", AuditLine("req_A", "2026-07-20T12:00:00.000Z", output: 120));
        WriteCoworkSession("local_b", AuditLine("req_B", "2026-07-20T12:00:00.000Z", output: 80));

        // The sandbox's own projects dir — the only place the Claude Code locator would
        // ever have looked inside a Cowork session — holds nothing.
        var sandboxProjects = Path.Combine(CoworkRoot, "org", "user", "local_a", ".claude", "projects");
        Assert.True(Directory.Exists(sandboxProjects));
        Assert.Empty(ClaudeProjectsLocator.FindTranscripts(sandboxProjects));

        Assert.Equal(2, CoworkAuditLocator.FindTranscripts(CoworkRoot).Count);
    }

    [Fact]
    public void Provider_IngestsBothSources_IntoOneTotal()
    {
        var projects = Path.Combine(_dir, "projects", "C--Users-X");
        Directory.CreateDirectory(projects);
        File.WriteAllLines(Path.Combine(projects, "session.jsonl"), [
            "{\"type\":\"assistant\",\"requestId\":\"req_code\",\"timestamp\":\"2026-07-20T12:00:00.000Z\"," +
            "\"message\":{\"model\":\"claude-opus-4-8\",\"usage\":{\"input_tokens\":2," +
            "\"cache_creation_input_tokens\":100,\"cache_read_input_tokens\":200,\"output_tokens\":500}}}",
        ]);
        WriteCoworkSession("local_a", AuditLine("req_cowork", "2026-07-20T12:00:00.000Z", output: 120));

        var provider = new JsonlUsageProvider(_store, Path.Combine(_dir, "projects"), [CoworkRoot]);
        provider.GetSnapshot(DateTimeOffset.Parse("2026-07-20T13:00:00Z"));

        Assert.Equal(620, TotalOutputTokens());
    }

    [Fact]
    public void Provider_ReScan_DoesNotDoubleCountCoworkUsage()
    {
        WriteCoworkSession("local_a", AuditLine("req_A", "2026-07-20T12:00:00.000Z", output: 120));
        var provider = new JsonlUsageProvider(_store, Path.Combine(_dir, "no-projects"), [CoworkRoot]);

        provider.GetSnapshot(DateTimeOffset.Parse("2026-07-20T13:00:00Z"));
        provider.GetSnapshot(DateTimeOffset.Parse("2026-07-20T13:01:00Z"));

        Assert.Equal(120, TotalOutputTokens());
    }

    // ── The broken-junction trap ───────────────────────────────────────────────
    [Fact]
    public void BrokenJunction_SkipsThatNodeOnly_DoesNotZeroTheScan()
    {
        WriteCoworkSession("local_a", AuditLine("req_A", "2026-07-20T12:00:00.000Z", output: 120));

        var target = Path.Combine(_dir, "junction-target");
        Directory.CreateDirectory(target);
        var link = Path.Combine(CoworkRoot, "org", "user", "outputs-link");

        if (!TryCreateJunction(link, target))
        {
            // Junction creation is unavailable in this environment; the rest of the
            // suite still covers the scan. Nothing to assert here.
            return;
        }

        // Breaking the target is what makes enumeration throw DirectoryNotFoundException —
        // an IOException, which the old `GetFiles(AllDirectories)` shape swallowed into
        // an empty result for the entire tree.
        Directory.Delete(target, recursive: true);

        var found = CoworkAuditLocator.FindTranscripts(CoworkRoot);

        // Remove the link before asserting: a broken junction blocks the recursive
        // delete in Dispose, which would fail the test for the wrong reason.
        Directory.Delete(link);

        Assert.Single(found);
        Assert.EndsWith(CoworkAuditLocator.AuditFileName, found[0]);
    }

    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (process is null) return false;
            process.WaitForExit(10_000);
            return process.HasExited && process.ExitCode == 0 && Directory.Exists(link);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    [Fact]
    public void MissingCoworkRoot_IsEmpty_NotAnError()
    {
        // A user who has never opened Cowork is the normal case.
        Assert.Empty(CoworkAuditLocator.FindTranscripts(Path.Combine(_dir, "does-not-exist")));
    }

    // ── The packaged-install trap ──────────────────────────────────────────────
    // Claude Desktop ships as MSIX, and Windows redirects its %APPDATA% writes into a
    // per-package store. On a machine where only the package store is populated, a
    // canonical-path-only locator finds nothing and the whole fix silently does nothing.

    [Fact]
    public void PackagedDataRoot_IsDiscovered_AlongsideCanonical()
    {
        var localAppData = Path.Combine(_dir, "LocalAppData");
        var packageRoot = Path.Combine(
            localAppData, "Packages", "Claude_pzs8sxrjxfjjc", "LocalCache", "Roaming", "Claude");
        Directory.CreateDirectory(packageRoot);

        var packaged = ClaudeDataRoots.Packaged(localAppData);

        Assert.Single(packaged);
        Assert.Equal(packageRoot, packaged[0]);
        Assert.Equal(ClaudeDataRoots.Canonical, ClaudeDataRoots.All(localAppData)[0]);
    }

    [Fact]
    public void NoPackagesDirectory_YieldsCanonicalRootOnly()
    {
        var roots = ClaudeDataRoots.All(Path.Combine(_dir, "no-local-appdata"));

        Assert.Equal([ClaudeDataRoots.Canonical], roots);
    }

    [Fact]
    public void SameSessionThroughTwoRoots_IsCountedOnce()
    {
        // MSIX redirection can expose one set of files through both the canonical and
        // packaged paths — observed on the dev machine, identical session ids and totals.
        // Scanning both must not double the tokens.
        //
        // It must not double the FILE COUNT either, which this used to assert that it did:
        // the union was taken deliberately and ingestion's request-id de-duplication kept the
        // tokens right. The tokens were right and the counts were not — the bundle reported
        // "16 file(s), 46,994,714 bytes" for 8 files and 23 MB, halving and doubling through
        // the day as the canonical root came and went. Mirrors are now collapsed on the path
        // relative to their root, which is the only thing that makes two of them comparable.
        var mirrorA = Path.Combine(_dir, "rootA");
        var mirrorB = Path.Combine(_dir, "rootB");
        foreach (var root in new[] { mirrorA, mirrorB })
        {
            var session = Path.Combine(root, "org", "user", "local_a");
            Directory.CreateDirectory(session);
            File.WriteAllLines(
                Path.Combine(session, CoworkAuditLocator.AuditFileName),
                [AuditLine("req_A", "2026-07-20T12:00:00.000Z", output: 120)]);
        }

        Assert.Single(CoworkAuditLocator.FindTranscripts([mirrorA, mirrorB]));

        var provider = new JsonlUsageProvider(_store, null, [mirrorA, mirrorB]);
        provider.GetSnapshot(DateTimeOffset.Parse("2026-07-20T13:00:00Z"));

        Assert.Equal(120, TotalOutputTokens());
    }

    /// <summary>
    /// Two genuinely different sessions are not mistaken for mirrors. <c>audit.jsonl</c> repeats
    /// in every session directory, so collapsing on the file name alone would discard real
    /// transcripts — which is why the match is on the path relative to its root.
    /// </summary>
    [Fact]
    public void TwoSessionsUnderOneRootAreBothKept()
    {
        foreach (var name in new[] { "local_a", "local_b" })
        {
            var session = Path.Combine(CoworkRoot, "org", "user", name);
            Directory.CreateDirectory(session);
            File.WriteAllLines(
                Path.Combine(session, CoworkAuditLocator.AuditFileName),
                [AuditLine($"req_{name}", "2026-07-20T12:00:00.000Z", output: 120)]);
        }

        Assert.Equal(2, CoworkAuditLocator.FindTranscripts([CoworkRoot]).Count);
    }

    /// <summary>
    /// <b>The gap issue #224 was filed for.</b> A Cowork session that runs Claude Code inside
    /// its sandbox writes to the sandbox's own <c>.claude\projects</c>, not to
    /// <c>audit.jsonl</c> — and this class asserted for a year that that directory was always
    /// empty. Measured: 4 such files on the development machine and 38 on the machine that
    /// reported #218, none of them ever read.
    /// </summary>
    [Fact]
    public void ATranscriptInsideTheSandboxProjectsDirectoryIsFound()
    {
        var sandbox = Path.Combine(
            CoworkRoot, "org", "user", "local_a", ".claude", "projects", "C--Users-ada-work");
        Directory.CreateDirectory(sandbox);
        File.WriteAllLines(
            Path.Combine(sandbox, "9d1f0d6a.jsonl"),
            [AuditLine("req_sandbox", "2026-07-20T12:00:00.000Z", output: 500)]);

        Assert.Single(CoworkAuditLocator.FindTranscripts([CoworkRoot]));

        var provider = new JsonlUsageProvider(_store, null, [CoworkRoot]);
        provider.GetSnapshot(DateTimeOffset.Parse("2026-07-20T13:00:00Z"));

        Assert.Equal(500, TotalOutputTokens());
    }
}
