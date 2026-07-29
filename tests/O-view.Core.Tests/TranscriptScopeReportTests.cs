using OView.Core.Providers.Jsonl;

namespace OView.Core.Tests;

/// <summary>
/// The panel's token-scope note was a string literal for its whole life, so nothing
/// asserted anything about it and it stayed wrong through two ingestion changes: it named
/// a hard-coded %USERPROFILE%\.claude\projects and only Claude Code, while ingestion had
/// read Cowork audit logs across every resolved root since issue #44 (GitHub issue #58).
/// These pin the properties that made it wrong.
/// </summary>
public class TranscriptScopeReportTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "oview-scope-" + Guid.NewGuid().ToString("N"));

    private string Projects => Path.Combine(_root, "projects");
    private string Cowork => Path.Combine(_root, "cowork");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string WriteTranscript(string dir, string name, string content)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void CountsBothSources_NotJustClaudeCode()
    {
        // The bug: Cowork usage is read and counted, but the note said it was not.
        WriteTranscript(Path.Combine(Projects, "proj-a"), "session.jsonl", "{}\n");
        WriteTranscript(Path.Combine(Cowork, "org", "user", "s1"), "audit.jsonl", "{}\n");

        var report = TranscriptScopeReport.Inspect(Projects, [Cowork]);

        Assert.Equal(2, report.TotalFiles);
        Assert.Equal(1, report.Sources.Single(s => s.Label == "Claude Code").FileCount);
        Assert.Equal(1, report.Sources.Single(s => s.Label == "Cowork").FileCount);
    }

    [Fact]
    public void Explain_NamesBothSources_AndNeitherHardCodesAPath()
    {
        var report = TranscriptScopeReport.Inspect(Projects, [Cowork]);
        var text = report.Explain();

        Assert.Contains("Claude Code", text, StringComparison.Ordinal);
        Assert.Contains("Cowork", text, StringComparison.Ordinal);

        // The literal that made the note wrong on a packaged Desktop install. Paths belong
        // in Copy diagnostics, which resolves them, not in a sentence that guesses.
        Assert.DoesNotContain(".claude", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("%USERPROFILE%", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("APPDATA", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Explain_DoesNotBlameTheDesktopApp()
    {
        // CLAUDE.md rule 9: Claude Code sessions hosted in Desktop DO write to the normal
        // location and are counted. Chat is the surface that genuinely cannot be measured,
        // so that is what the note must name.
        var text = TranscriptScopeReport.Inspect(Projects, [Cowork]).Explain();

        Assert.Contains("Chat", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("chats in the Claude Desktop app", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Explain_DistinguishesNoFilesFromFilesThatYieldedNothing()
    {
        var empty = TranscriptScopeReport.Inspect(Projects, [Cowork]);
        Assert.Equal(TranscriptScopeStatus.NoTranscripts, empty.Status);
        // Nothing on disk: the note explains the absence and must not imply a fault.
        Assert.DoesNotContain("unexpected", empty.Explain(), StringComparison.OrdinalIgnoreCase);

        // Files present but nothing recorded is a different fact and a different action:
        // it points at ingestion rather than at an absent source, so it must say so and
        // must quantify what it found.
        WriteTranscript(Path.Combine(Projects, "proj-a"), "session.jsonl", new string('x', 4096));
        var present = TranscriptScopeReport.Inspect(Projects, [Cowork]);

        Assert.Equal(TranscriptScopeStatus.TranscriptsPresent, present.Status);
        Assert.Contains("unexpected", present.Explain(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1 local transcript file", present.Explain(), StringComparison.Ordinal);
        Assert.Contains("4.0 KB", present.Explain(), StringComparison.Ordinal);
        Assert.NotEqual(empty.Explain(), present.Explain());
    }

    [Fact]
    public void Explain_ReportsHowManyLocationsWereChecked()
    {
        // The evidence behind a "found none" — the same contract PlanHistoryReport keeps.
        var report = TranscriptScopeReport.Inspect(Projects, [Cowork, Path.Combine(_root, "cowork2")]);

        Assert.Equal(3, report.SearchedRoots.Count);
        Assert.Contains("3 location(s)", report.Explain(), StringComparison.Ordinal);
    }

    [Fact]
    public void NullProjectsRoot_AndEmptyCoworkList_EachSkipThatSource()
    {
        // Mirrors JsonlUsageProvider: a source is stated or absent by choice, and neither
        // silently falls back to a machine default. Naming one root while the other
        // resolved to a real directory once made a test ingest a developer's real history.
        WriteTranscript(Path.Combine(Cowork, "org", "user", "s1"), "audit.jsonl", "{}\n");

        var coworkOnly = TranscriptScopeReport.Inspect(projectsRoot: null, [Cowork]);
        Assert.Equal(1, coworkOnly.TotalFiles);
        Assert.Empty(coworkOnly.Sources.Single(s => s.Label == "Claude Code").Roots);

        var neither = TranscriptScopeReport.Inspect(projectsRoot: null, []);
        Assert.Equal(0, neither.TotalFiles);
        Assert.Empty(neither.SearchedRoots);
    }

    [Fact]
    public void DeduplicatesCoworkFiles_SeenThroughTwoRoots()
    {
        // MSIX write-redirection can expose one set of sessions through both the canonical
        // and the packaged path. Ingestion de-duplicates, so the evidence must too —
        // otherwise the report overstates what is on disk.
        WriteTranscript(Path.Combine(Cowork, "org", "user", "s1"), "audit.jsonl", "{}\n");

        var report = TranscriptScopeReport.Inspect(projectsRoot: null, [Cowork, Cowork]);

        Assert.Equal(1, report.TotalFiles);
    }

    [Fact]
    public void ClipboardText_ListsEveryRootSearched()
    {
        var report = TranscriptScopeReport.Inspect(Projects, [Cowork]);
        var text = report.ToClipboardText();

        Assert.Contains(Projects, text, StringComparison.Ordinal);
        Assert.Contains(Cowork, text, StringComparison.Ordinal);
        Assert.Contains("Claude Code", text, StringComparison.Ordinal);
        Assert.Contains("Cowork", text, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingRoots_AreReportedNotThrown()
    {
        // A user who has never opened Cowork is the normal case, not an error.
        var report = TranscriptScopeReport.Inspect(
            Path.Combine(_root, "nope"), [Path.Combine(_root, "also-nope")]);

        Assert.Equal(0, report.TotalFiles);
        Assert.Null(report.NewestWriteUtc);
        Assert.Equal(TranscriptScopeStatus.NoTranscripts, report.Status);
    }
}
