using OView.App.Diagnostics;
using OView.Core.Models;
using OView.Core.Providers.Jsonl;
using OView.Core.Providers.PlanHistory;
using OView.Core.Storage;

namespace OView.App.Tests;

/// <summary>
/// The support bundle. It is the only view a maintainer gets of someone else's machine, so
/// what it says has to be true of that machine and readable by someone who did not write
/// it.
/// </summary>
public class DiagnosticsBundleTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose()
    {
        _dir.Dispose();
        GC.SuppressFinalize(this);
    }

    private string Build(
        DiagnosticsEnvironment environment,
        CorruptBackupReport? corruptBackups = null,
        IReadOnlyList<string>? logTail = null,
        RollupStoreReport? store = null,
        TranscriptScopeReport? scope = null,
        IngestAuditReport? ingestAudit = null) =>
        DiagnosticsBundle.Build(
            environment,
            PlanHistoryDiagnostics.Inspect(_dir.File("absent.json")),
            scope ?? TranscriptScopeReport.Inspect(null, []),
            account: null,
            new WeeklyResetAnchor(_dir.File("weekly-reset.json")),
            new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero),
            corruptBackups,
            logTail,
            store,
            ingestAudit);

    private static DiagnosticsEnvironment Windows => new("0.6.0", "WindowsInstaller");

    private static DiagnosticsEnvironment Linux => new(
        "0.6.0", "LinuxPackage", Desktop: "GNOME", SessionType: "wayland", TrayHost: "Absent");

    // ── the labels the issue was raised about ───────────────────────────────────────

    /// <summary>
    /// "appdata root" is a Windows word. On Linux it pointed at ~/.config, so the label
    /// taught the reader the wrong concept for the path beside it — and the whole reason
    /// the roots are printed is to make a wrong resolution visible (rule 6).
    /// </summary>
    [Fact]
    public void RootLabelsNameWhatThePathIsNotWhichPlatformItCameFrom()
    {
        var bundle = Build(Linux);

        Assert.Contains("config root", bundle, StringComparison.Ordinal);
        Assert.Contains("data root", bundle, StringComparison.Ordinal);
        Assert.Contains("home", bundle, StringComparison.Ordinal);
        Assert.DoesNotContain("appdata root", bundle, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("user profile", bundle, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryRootIsPrintedWithItsResolvedAbsolutePath()
    {
        var bundle = Build(Windows);

        // The label alone is useless; the point is the value beside it.
        //
        // Compared against the redacted spelling, not the raw one. The bundle now removes
        // the account name before returning (Redact), so the raw path never appears — but
        // what this test guards is unchanged: that the RESOLVED value is printed rather
        // than the label alone, because a wrong SpecialFolder resolution is only visible
        // in the value. Redaction replaces the account segment and nothing else, so the
        // resolution is still what is being asserted.
        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.ApplicationData,
                     Environment.SpecialFolder.LocalApplicationData,
                     Environment.SpecialFolder.UserProfile,
                 })
        {
            var resolved = Redact.Bundle(Environment.GetFolderPath(folder));

            Assert.Contains(resolved, bundle, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NothingIdentifyingSurvivesIntoTheBundle()
    {
        // The bundle is pasted into public issues. This asserts the property at the funnel,
        // so a field added later cannot reintroduce the leak without failing here.
        var bundle = Build(Windows);

        foreach (var name in Redact.AccountNames())
        {
            Assert.DoesNotContain(name, bundle, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotMatch(
            @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
            bundle);
    }

    // ── the platform block ──────────────────────────────────────────────────────────

    [Fact]
    public void EveryBundleSaysWhatKindOfMachineItIs()
    {
        var bundle = Build(Windows);

        Assert.Contains("platform", bundle, StringComparison.Ordinal);
        Assert.Contains("install kind  : WindowsInstaller", bundle, StringComparison.Ordinal);
    }

    /// <summary>
    /// The highest-value field in the whole bundle. "My icon doesn't appear" is the most
    /// likely Linux report by a long way, and an Avalonia tray icon reports success whether
    /// or not a host exists — so this line is the only written record of the truth.
    /// </summary>
    [Fact]
    public void ALinuxBundleReportsTheTrayHostDesktopAndSession()
    {
        var bundle = Build(Linux);

        Assert.Contains("tray host     : Absent", bundle, StringComparison.Ordinal);
        Assert.Contains("desktop       : GNOME", bundle, StringComparison.Ordinal);
        Assert.Contains("session       : wayland", bundle, StringComparison.Ordinal);
    }

    /// <summary>
    /// Windows has one desktop, and its notification area is part of the shell and cannot
    /// be absent. Padding those lines with "n/a" would make the fields that DO mean
    /// something harder to find in a pasted report.
    /// </summary>
    [Fact]
    public void AWindowsBundleOmitsTheFieldsThatDoNotApply()
    {
        var bundle = Build(Windows);

        Assert.DoesNotContain("desktop  ", bundle, StringComparison.Ordinal);
        Assert.DoesNotContain("session  ", bundle, StringComparison.Ordinal);
        Assert.DoesNotContain("tray host", bundle, StringComparison.Ordinal);
    }

    // ── the fields Windows must not lose ────────────────────────────────────────────

    [Fact]
    public void TheWindowsBundleStillCarriesEveryFieldItHadBefore()
    {
        var bundle = Build(Windows);

        Assert.Contains("app version", bundle, StringComparison.Ordinal);
        Assert.Contains("process", bundle, StringComparison.Ordinal);
        Assert.Contains("account file", bundle, StringComparison.Ordinal);
        Assert.Contains("weekly anchor", bundle, StringComparison.Ordinal);
        // "installed : True/False" became "install kind", which subsumes it — a portable
        // build and an installer build are still distinguishable, and now so are the Linux
        // kinds.
        Assert.Contains("install kind", bundle, StringComparison.Ordinal);
    }

    /// <summary>
    /// The bundle is pasted into public bug reports, so it must carry no credential of any
    /// kind and no conversation content.
    ///
    /// <para>This used to be called <c>NothingIdentifyingBeyondTheOrgKeyIsAdded</c>, on the
    /// reasoning that the org UUID was a permitted exception because it is the documented
    /// filter key. It is no longer an exception: <see cref="Redact"/> truncates it, which
    /// keeps the comparison the filter key is needed for without publishing the identifier.
    /// The name went with the reasoning — a test asserting the old rule by name, while the
    /// code enforces a stricter one, is a comment that lies.</para>
    /// </summary>
    [Fact]
    public void NoCredentialOrConversationContentReachesTheBundle()
    {
        var bundle = Build(Linux);

        foreach (var forbidden in new[] { "token", "sk-ant", "Bearer", "password", "secret" })
        {
            Assert.DoesNotContain(forbidden, bundle, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void AnUnreadableWeeklyAnchorDoesNotTakeTheBundleDown()
    {
        // A support bundle that throws is worse than one with a gap in it — the user is
        // already reporting a problem.
        var bundle = DiagnosticsBundle.Build(
            Linux,
            PlanHistoryDiagnostics.Inspect(_dir.File("absent.json")),
            TranscriptScopeReport.Inspect(null, []),
            account: null,
            new WeeklyResetAnchor(_dir.Path),   // a directory, not a file
            DateTimeOffset.UnixEpoch);

        Assert.Contains("weekly anchor", bundle, StringComparison.Ordinal);
    }

    // ── the rollup store ────────────────────────────────────────────────────────────

    /// <summary>
    /// The store section, and specifically which reader produced it. A bundle from the running
    /// app reports the store through the connection that app holds; <c>--diagnose</c> opens its
    /// own. Printing which is what turns a disagreement between them into evidence.
    /// </summary>
    [Fact]
    public void TheStoreSectionNamesItsReaderAndWhatItFound()
    {
        var bundle = Build(Windows, store: new RollupStoreReport(
            Path: @"C:\store\usage.db",
            Origin: RollupStoreReport.LiveInstance,
            FileBytes: 1_183_744,
            WalBytes: 2_575_032,
            JournalMode: "wal",
            Integrity: "ok",
            WritesAccepted: true,
            Failure: null,
            LedgerRows: 5_072,
            FirstDay: "2026-07-17",
            LastDay: "2026-08-20",
            NewestTimestamp: "2026-08-20T19:21:47Z",
            TrackedFiles: 32,
            FilesBehind: 2,
            UnreadBytes: 409_501,
            FilesGone: 2));

        Assert.Contains("rollup store", bundle, StringComparison.Ordinal);
        Assert.Contains(RollupStoreReport.LiveInstance, bundle, StringComparison.Ordinal);
        Assert.Contains("5,072 row(s)", bundle, StringComparison.Ordinal);
        Assert.Contains("2 behind by 409,501 bytes", bundle, StringComparison.Ordinal);
        Assert.Contains("writes accepted", bundle, StringComparison.Ordinal);
    }

    // ── the ingest gap and the reconciliation (issue #218) ──────────────────────────

    /// <summary>
    /// Transcripts on disk that the store has no watermark for at all.
    ///
    /// <para>Both figures were already in the bundle, twenty lines apart and written for
    /// different reasons; the machine this came from read 38 and 32 and nobody subtracted them.
    /// <c>0 behind by 0 bytes</c> is only ever a statement about files the store already knows
    /// about — a file it has never seen is behind by its whole length and appears in none of
    /// those numbers.</para>
    /// </summary>
    [Fact]
    public void TranscriptsWithNoWatermarkAreCountedAgainstWhatTheStoreTracks()
    {
        var projects = Directory.CreateDirectory(Path.Combine(_dir.Path, "projects")).FullName;
        foreach (var name in new[] { "a.jsonl", "b.jsonl", "c.jsonl" })
        {
            File.WriteAllText(Path.Combine(projects, name), "{}\n");
        }

        var bundle = Build(Windows,
            store: Tracked(trackedFiles: 1, filesGone: 0),
            scope: TranscriptScopeReport.Inspect(projects, []));

        Assert.Contains("3 file(s) on disk, 1 tracked and present, 2 with no watermark",
            bundle, StringComparison.Ordinal);
    }

    /// <summary>
    /// A watermark for a transcript Claude Code has since deleted is not a tracked file, and
    /// must not be counted as one — it would hide a real gap behind a stale row.
    /// </summary>
    [Fact]
    public void WatermarksForDeletedTranscriptsDoNotCountAsTracked()
    {
        var projects = Directory.CreateDirectory(Path.Combine(_dir.Path, "projects")).FullName;
        File.WriteAllText(Path.Combine(projects, "a.jsonl"), "{}\n");

        var bundle = Build(Windows,
            store: Tracked(trackedFiles: 4, filesGone: 3),
            scope: TranscriptScopeReport.Inspect(projects, []));

        Assert.Contains("1 file(s) on disk, 1 tracked and present, 0 with no watermark",
            bundle, StringComparison.Ordinal);
    }

    /// <summary>
    /// A store that could not be read contributes no gap line: "0 tracked" would then be a fact
    /// about this reader rather than about the machine (rule 6).
    /// </summary>
    [Fact]
    public void AnUnreadableStoreProducesNoIngestGapLine()
    {
        var bundle = Build(Windows, store: RollupStoreReport.Unavailable(
            @"C:\store\usage.db", RollupStoreReport.OpenedForReport, "no database file yet"));

        Assert.DoesNotContain("ingest gap", bundle, StringComparison.Ordinal);
    }

    /// <summary>
    /// The reconciliation pass, when it has been run: what is on disk against what is stored.
    /// This is the section that answers the question the store's own numbers raise and cannot
    /// settle, and it works on a store carrying no attribution at all.
    /// </summary>
    [Fact]
    public void TheIngestAuditReportsWhatIsMissingPerSurface()
    {
        var bundle = Build(Windows, ingestAudit: new IngestAuditReport(
            [
                new IngestAuditSource(TranscriptSources.ClaudeCode, 30, 13_357, 7_081, 4_895, 2_685_535_944, 1_838_290_166),
                new IngestAuditSource(TranscriptSources.Cowork, 8, 590, 174, 174, 14_910_462, 14_910_462),
            ],
            LedgerRows: 5_072,
            SharedRequests: 0,
            Elapsed: TimeSpan.FromSeconds(0.4)));

        Assert.Contains("2,186 MISSING", bundle, StringComparison.Ordinal);
        Assert.Contains("0 MISSING", bundle, StringComparison.Ordinal);
        Assert.Contains(TranscriptSources.Cowork, bundle, StringComparison.Ordinal);
    }

    /// <summary>
    /// Most bundles are produced without it, and say so rather than omitting the section — an
    /// absent section cannot be told apart from one that failed to render.
    /// </summary>
    [Fact]
    public void ABundleWithoutTheAuditSaysSoRatherThanOmittingIt()
    {
        var bundle = Build(Windows);

        Assert.Contains("ingest audit", bundle, StringComparison.Ordinal);
        Assert.Contains("--diagnose", bundle, StringComparison.Ordinal);
    }

    /// <summary>A store report whose only interesting fields are the two the gap line reads.</summary>
    private static RollupStoreReport Tracked(int trackedFiles, int filesGone) => new(
        Path: @"C:\store\usage.db",
        Origin: RollupStoreReport.LiveInstance,
        FileBytes: 1024,
        WalBytes: 0,
        JournalMode: "wal",
        Integrity: "ok",
        WritesAccepted: true,
        Failure: null,
        LedgerRows: 1,
        FirstDay: "2026-08-01",
        LastDay: "2026-08-01",
        NewestTimestamp: "2026-08-01T00:00:00Z",
        TrackedFiles: trackedFiles,
        FilesBehind: 0,
        UnreadBytes: 0,
        FilesGone: filesGone);

    /// <summary>
    /// A store that could not be read says so. An omitted section is indistinguishable from one
    /// that failed to render, which is the ambiguity this whole bundle exists to remove.
    /// </summary>
    [Fact]
    public void AStoreThatWasNotInspectedSaysSoRatherThanVanishing()
    {
        var bundle = Build(Windows);

        Assert.Contains("rollup store", bundle, StringComparison.Ordinal);
        Assert.Contains("unreadable", bundle, StringComparison.Ordinal);
    }

    // ── the recent log ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The only part of the bundle that says what the app <i>did</i>. Everything else
    /// describes its inputs, and all of that can read perfectly while no poll has completed
    /// in days — which is exactly the report that arrived three times, each leading with
    /// <c>status : Ok</c>.
    /// </summary>
    [Fact]
    public void TheRecentLogIsCarriedInTheBundle()
    {
        var bundle = Build(Windows, logTail:
        [
            "2026-08-25 18:14:52.001Z poll read begin",
            "2026-08-25 18:14:52.123Z poll read done in 122 ms",
            "2026-08-25 18:14:52.140Z poll published after 139 ms",
        ]);

        Assert.Contains("log           :", bundle, StringComparison.Ordinal);
        Assert.Contains("poll read begin", bundle, StringComparison.Ordinal);
        Assert.Contains("poll published after 139 ms", bundle, StringComparison.Ordinal);
    }

    /// <summary>
    /// On a build that logs by default, "no log lines" is itself a finding — it means the run
    /// never reached the first write. So it is stated rather than omitted, because an absent
    /// section is indistinguishable from one that failed to render.
    /// </summary>
    [Fact]
    public void AnAbsentLogSaysSoRatherThanVanishing()
    {
        var bundle = Build(Windows);

        Assert.Contains("log           :", bundle, StringComparison.Ordinal);
        Assert.Contains("(no log lines yet)", bundle, StringComparison.Ordinal);
    }

    // ── quarantined rollup stores (issue #160) ──────────────────────────────────────

    /// <summary>
    /// The half of #160 that makes the pruning defensible. The <c>.corrupt-*</c> files were
    /// retained "so the corruption can still be examined" while nothing surfaced them —
    /// <c>--diagnose</c> did not list them, the bundle did not mention them, the panel said
    /// nothing. A backup nobody is told about is residue, not evidence.
    /// </summary>
    [Fact]
    public void QuarantinedStoresAreNamedWithTheirCountStampAndSize()
    {
        var bundle = Build(Windows, new CorruptBackupReport(2, "20260804-120000", 6 * 1024 * 1024));

        Assert.Contains("corrupt stores", bundle, StringComparison.Ordinal);
        Assert.Contains("2 quarantined", bundle, StringComparison.Ordinal);
        Assert.Contains("20260804-120000", bundle, StringComparison.Ordinal);
        Assert.Contains("6.0 MB", bundle, StringComparison.Ordinal);
    }

    /// <summary>
    /// Printed rather than omitted on a clean machine, so "no line" is never ambiguous between
    /// "never corrupted" and "this field failed to render".
    /// </summary>
    [Fact]
    public void ACleanMachineSaysSoRatherThanOmittingTheLine()
    {
        var bundle = Build(Windows);

        Assert.Contains("corrupt stores: none", bundle, StringComparison.Ordinal);
    }

    /// <summary>
    /// The count is of files retained, which pruning caps — so the bundle prints the cap
    /// beside it. Without that, a reader takes "2 quarantined" for the number of times the
    /// store has corrupted, which after a prune it is not (rule 6).
    /// </summary>
    [Fact]
    public void TheRetentionLimitIsPrintedBesideTheCountSoItIsNotReadAsATotal()
    {
        var bundle = Build(Windows, new CorruptBackupReport(2, "20260804-120000", 1024));

        Assert.Contains($"keeping {CorruptBackups.KeepGenerations}", bundle, StringComparison.Ordinal);
    }

    /// <summary>
    /// Which rates the Est. figures were computed from (GitHub issues #255–#257).
    ///
    /// <para>Every other section here reports an input; this reports an assumption. Two wrong
    /// numbers in that table produced money figures 50% high and 28% low, and a bundle could
    /// read perfectly through both — there was nothing to look at. The date and the source are
    /// printed unconditionally for that reason, not only once the card is stale.</para>
    /// </summary>
    [Fact]
    public void TheBundleNamesTheRatesTheEstFiguresWerePricedFrom()
    {
        var bundle = Build(Windows);

        Assert.Contains("rate card", bundle, StringComparison.Ordinal);
        Assert.Contains($"as of {ModelCatalog.AsOf:yyyy-MM-dd}", bundle, StringComparison.Ordinal);
        Assert.Contains("bundled", bundle, StringComparison.Ordinal);
    }

    /// <summary>
    /// The bundle names the calibration and does not fake it. No file on this machine carries a
    /// dollar figure Claude Code reported, so O-view cannot run the comparison itself — saying
    /// how a maintainer runs it is honest, and printing a number O-view did not compute would
    /// be the fabrication rule 6 is about.
    /// </summary>
    [Fact]
    public void TheBundleNamesTheCalibrationRatherThanInventingOne()
    {
        var bundle = Build(Windows);

        Assert.Contains("RelativeError", bundle, StringComparison.Ordinal);
        Assert.Contains("never installs rates", bundle, StringComparison.Ordinal);
    }
}
