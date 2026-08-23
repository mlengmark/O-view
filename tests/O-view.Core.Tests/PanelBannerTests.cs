using OView.Core.Models;
using OView.Core.Providers.Jsonl;
using OView.Core.Providers.PlanHistory;

namespace OView.Core.Tests;

/// <summary>
/// A user who runs only the Claude Code CLI was shown "No usage data", and told to start
/// Claude Desktop, directly above two token tiles that were populated from their own
/// transcripts (GitHub issue #170). The plan-history file is written by Desktop, so its
/// absence is normal for them — and O-view read it as a fault because it looked at that
/// report alone.
///
/// <para>These pin the join: the reassuring banner appears only when the plan file is
/// genuinely absent <i>and</i> transcripts were found, and every other combination keeps
/// the fault banner that was written for a real fault.</para>
/// </summary>
public class PanelBannerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("oview-banner-").FullName;

    private string Projects => Path.Combine(_root, "projects");
    private string Cowork => Path.Combine(_root, "cowork");

    public void Dispose()
    {
        // Deliberately does NOT touch DiagnosticsHint: that static belongs to
        // DiagnosticsHintTests, and resetting it from here would race a class xUnit runs
        // in parallel with this one.
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private void WriteTranscript(string dir, string name)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), "{}\n");
    }

    /// <summary>A plan-history report for a file that is not there — the CLI user's normal state.</summary>
    private PlanHistoryReport MissingPlanFile() =>
        PlanHistoryDiagnostics.Inspect(Path.Combine(_root, "absent.json"));

    /// <summary>
    /// Present but not JSON at all. Note that pointing <c>Inspect</c> at a <i>directory</i>
    /// does not produce this — <c>File.Exists</c> is false for one, so that yields
    /// <see cref="PlanDataStatus.FileMissing"/> and would silently test the wrong branch.
    /// </summary>
    private PlanHistoryReport UnreadablePlanFile() => WritePlanFile("unreadable.json", "{ not json");

    /// <summary>Parses, but carries nothing matching the expected sample shape — schema drift.</summary>
    private PlanHistoryReport MalformedPlanFile() => WritePlanFile("drifted.json", """{"samples":[]}""");

    private PlanHistoryReport WritePlanFile(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return PlanHistoryDiagnostics.Inspect(path);
    }

    private TranscriptScopeReport ClaudeCodeOnly()
    {
        WriteTranscript(Path.Combine(Projects, "proj-a"), "session.jsonl");
        return TranscriptScopeReport.Inspect(Projects, [Cowork]);
    }

    // ── the regression ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The report itself. No plan file, but transcripts are being read and counted, so the
    /// panel must not claim there is no usage data — the tiles below it disagree.
    /// </summary>
    [Fact]
    public void ACliOnlyUserIsNotToldTheyHaveNoUsageData()
    {
        var banner = PanelBanner.Resolve(
            authoritative: false, MissingPlanFile(), ClaudeCodeOnly(), tokens31Days: 235_600_000);

        Assert.NotNull(banner);
        Assert.Equal(PanelBanner.ScopeTitle, banner.Title);
        Assert.DoesNotContain("No usage data", banner.Title, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("No usage data", banner.Detail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The second half of the complaint: the old copy's only remedy was to start an
    /// application this user does not have and does not want. Advice they cannot act on
    /// reads as a fault report.
    /// </summary>
    [Fact]
    public void ACliOnlyUserIsNotToldToStartAnApplicationTheyDoNotUse()
    {
        var banner = PanelBanner.Resolve(
            authoritative: false, MissingPlanFile(), ClaudeCodeOnly(), tokens31Days: 1);

        Assert.NotNull(banner);
        Assert.DoesNotContain("start it", banner.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("is not running", banner.Detail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A blank gauge with no stated reason is indistinguishable from a broken one. "unknown"
    /// is honest when O-view cannot tell why; here it can, so it says which.
    /// </summary>
    [Fact]
    public void TheGaugesNameTheirMissingSourceRatherThanReadingUnknown()
    {
        var banner = PanelBanner.Resolve(
            authoritative: false, MissingPlanFile(), ClaudeCodeOnly(), tokens31Days: 1);

        Assert.NotNull(banner);
        Assert.Equal(PanelBanner.NeedsDesktopGauge, banner.GaugePlaceholder);
        Assert.NotEqual(PanelBanner.UnknownGauge, banner.GaugePlaceholder);
    }

    /// <summary>The banner says the token figures are unaffected, because they are.</summary>
    [Fact]
    public void TheDetailSaysTheTokenFiguresStillWork()
    {
        var banner = PanelBanner.Resolve(
            authoritative: false, MissingPlanFile(), ClaudeCodeOnly(), tokens31Days: 1);

        Assert.NotNull(banner);
        Assert.Contains("unaffected", banner.Detail, StringComparison.OrdinalIgnoreCase);
    }

    // ── the reassuring case stays narrow ────────────────────────────────────────────

    /// <summary>
    /// No plan file and no transcripts either: nothing to reassure anyone about, and the
    /// original banner is still exactly right.
    /// </summary>
    [Fact]
    public void WithNoTranscriptsEitherTheFaultBannerIsUnchanged()
    {
        var banner = PanelBanner.Resolve(
            authoritative: false, MissingPlanFile(), TranscriptScopeReport.Inspect(null, []), 0);

        Assert.NotNull(banner);
        Assert.Equal(PanelBanner.NoDataTitle, banner.Title);
        Assert.Equal(PanelBanner.UnknownGauge, banner.GaugePlaceholder);
    }

    /// <summary>
    /// A plan file that is present but broken is a real fault, whatever the transcripts say.
    /// Only <see cref="PlanDataStatus.FileMissing"/> means "this machine has no Desktop file
    /// to read"; every other status means one is there and something went wrong with it, and
    /// reassuring copy would bury exactly the case worth reporting.
    /// </summary>
    [Fact]
    public void APresentButBrokenPlanFileIsStillAFaultEvenWithTranscriptsPresent()
    {
        var scope = ClaudeCodeOnly();

        foreach (var report in new[] { UnreadablePlanFile(), MalformedPlanFile() })
        {
            Assert.NotEqual(PlanDataStatus.FileMissing, report.Status);

            var banner = PanelBanner.Resolve(authoritative: false, report, scope, tokens31Days: 1);

            Assert.NotNull(banner);
            Assert.Equal(PanelBanner.NoDataTitle, banner.Title);
            Assert.Equal(PanelBanner.UnknownGauge, banner.GaugePlaceholder);
        }
    }

    /// <summary>A missing scope report is no evidence of anything, so it must not reassure.</summary>
    [Fact]
    public void AMissingScopeReportDoesNotReassure()
    {
        var banner = PanelBanner.Resolve(authoritative: false, MissingPlanFile(), null, tokens31Days: 1);

        Assert.NotNull(banner);
        Assert.Equal(PanelBanner.NoDataTitle, banner.Title);
    }

    /// <summary>When the figures are trustworthy there is nothing to explain.</summary>
    [Fact]
    public void NoBannerWhenTheSnapshotIsAuthoritative()
    {
        Assert.Null(PanelBanner.Resolve(
            authoritative: true, MissingPlanFile(), ClaudeCodeOnly(), tokens31Days: 1));
    }

    [Fact]
    public void NoBannerWithoutAPlanReportAtAll()
    {
        Assert.Null(PanelBanner.Resolve(authoritative: false, null, ClaudeCodeOnly(), 1));
    }

    // ── nothing recorded is not the same as nothing to worry about ──────────────────

    /// <summary>
    /// Transcripts found but nothing ingested from them. The reassuring wording would be
    /// false beside two zeroed tiles, which is the same defect as the banner this replaces —
    /// so this case gets its own sentence and points at diagnostics.
    /// </summary>
    [Fact]
    public void TranscriptsFoundButNothingRecordedIsReportedRatherThanReassured()
    {
        var banner = PanelBanner.Resolve(
            authoritative: false, MissingPlanFile(), ClaudeCodeOnly(), tokens31Days: 0);

        Assert.NotNull(banner);
        Assert.Equal(PanelBanner.ScopeTitle, banner.Title);
        Assert.Equal(PanelBanner.NeedsDesktopGauge, banner.GaugePlaceholder);
        Assert.DoesNotContain("unaffected", banner.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unexpected", banner.Detail, StringComparison.OrdinalIgnoreCase);
    }

    // ── the copy names the user's own surfaces ──────────────────────────────────────

    /// <summary>
    /// Issue #58 pointed the other way: telling a Cowork user their tokens come from Claude
    /// Code is the same error as telling a Claude Code user they come from Cowork.
    /// </summary>
    [Fact]
    public void CoworkOnlyIsNamedAsCoworkNotAsClaudeCode()
    {
        WriteTranscript(Path.Combine(Cowork, "org", "user", "s1"), "audit.jsonl");
        var scope = TranscriptScopeReport.Inspect(Projects, [Cowork]);

        var banner = PanelBanner.Resolve(authoritative: false, MissingPlanFile(), scope, 1);

        Assert.NotNull(banner);
        Assert.Contains("Cowork", banner.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Claude Code", banner.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void BothSurfacesAreNamedWhenBothArePresent()
    {
        WriteTranscript(Path.Combine(Projects, "proj-a"), "session.jsonl");
        WriteTranscript(Path.Combine(Cowork, "org", "user", "s1"), "audit.jsonl");
        var scope = TranscriptScopeReport.Inspect(Projects, [Cowork]);

        var banner = PanelBanner.Resolve(authoritative: false, MissingPlanFile(), scope, 1);

        Assert.NotNull(banner);
        Assert.Contains("Claude Code", banner.Detail, StringComparison.Ordinal);
        Assert.Contains("Cowork", banner.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// Paths belong in Copy diagnostics, which resolves them. A sentence that guesses one is
    /// how a packaged Desktop user got pointed at a directory O-view never searched (#58).
    /// </summary>
    [Fact]
    public void TheCopyHardCodesNoPath()
    {
        var banner = PanelBanner.Resolve(
            authoritative: false, MissingPlanFile(), ClaudeCodeOnly(), tokens31Days: 1);

        Assert.NotNull(banner);
        Assert.DoesNotContain(".claude", banner.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("%USERPROFILE%", banner.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("APPDATA", banner.Detail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The same file goes missing when a packaged install redirects it somewhere O-view did
    /// not look, so the banner must not assert Desktop is absent — it states what was
    /// searched and leaves the conclusion to the reader (rule 6).
    ///
    /// <para>That these sentences also carry the diagnostics instruction is asserted in
    /// <see cref="DiagnosticsHintTests"/>, which owns that static and must not be raced for
    /// it: xUnit runs test classes in parallel, so setting it here failed that class
    /// intermittently while passing in isolation.</para>
    /// </summary>
    [Fact]
    public void TheCopyStatesWhatWasSearchedAndDoesNotAssertDesktopIsAbsent()
    {
        var banner = PanelBanner.Resolve(
            authoritative: false, MissingPlanFile(), ClaudeCodeOnly(), tokens31Days: 1);

        Assert.NotNull(banner);
        Assert.Contains("location(s) it checked", banner.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("not installed", banner.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("isn't installed", banner.Detail, StringComparison.OrdinalIgnoreCase);
    }
}
