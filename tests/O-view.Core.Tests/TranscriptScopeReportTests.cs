using OView.Core.Providers.CachedUsage;
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

    /// <summary>
    /// The note claims no source for the two percentage bars. It used to say they came from
    /// Claude Desktop's usage file; two sources have reported them since Claude Code began
    /// caching the same figures, nothing reaching this report says which one answered, and
    /// naming either was therefore an assertion about the machine O-view had not made
    /// (CLAUDE.md rule 6).
    ///
    /// <para>The reassurance it exists for is what must survive: a reader looking at zeroed
    /// token tiles beside two moving bars needs to know the bars are not fed by the
    /// transcripts that were not found.</para>
    /// </summary>
    [Fact]
    public void Explain_ClaimsNoSourceForTheTwoPercentageBars()
    {
        var text = TranscriptScopeReport.Inspect(Projects, [Cowork]).Explain();

        Assert.Equal(TranscriptScopeStatus.NoTranscripts,
            TranscriptScopeReport.Inspect(Projects, [Cowork]).Status);

        Assert.DoesNotContain("Claude Desktop", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("usage file", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("come from Claude", text, StringComparison.OrdinalIgnoreCase);

        // What the sentence is for, kept.
        Assert.Contains("do not come from transcripts", text, StringComparison.Ordinal);
        Assert.Contains("cover all of your usage", text, StringComparison.Ordinal);
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
        // The evidence behind a "found none", the same contract PlanHistoryReport keeps.
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
        // and the packaged path. Ingestion de-duplicates, so the evidence must too,
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

    [Fact]
    public void ExcludesTheRefreshersOwnProjectFolder_FromCountAndBytes()
    {
        // A session the refresher itself produced sits beside genuine ones. It must not
        // inflate either figure, because neither is evidence of the user's own usage.
        WriteTranscript(Path.Combine(Projects, "proj-a"), "session.jsonl", "{}\n");
        var ownFolder = Path.Combine(Projects, "own-refresher-folder");
        WriteTranscript(ownFolder, "refresh-session.jsonl", new string('x', 4096));

        var report = TranscriptScopeReport.Inspect(Projects, [Cowork], ownFolder);

        Assert.Equal(1, report.TotalFiles);
        Assert.Equal(1, report.Sources.Single(s => s.Label == "Claude Code").FileCount);
        Assert.Equal(3, report.TotalBytes); // "{}\n" from the one file left after exclusion
    }

    [Fact]
    public void OnlyTheRefreshersOwnTranscripts_ReportNoTranscripts_NeverUnexpected()
    {
        // The whole point of the exclusion: a machine whose only transcripts are the ones
        // O-view's own usage refresh created must not be told that is unexpected and to
        // report a bug (issue #313).
        var ownFolder = Path.Combine(Projects, "own-refresher-folder");
        WriteTranscript(ownFolder, "refresh-session.jsonl", "{}\n");

        var report = TranscriptScopeReport.Inspect(Projects, [Cowork], ownFolder);

        Assert.Equal(0, report.TotalFiles);
        Assert.Equal(TranscriptScopeStatus.NoTranscripts, report.Status);
        Assert.DoesNotContain("unexpected", report.Explain(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ASiblingFolderWithASimilarName_IsNotExcludedByPrefix()
    {
        // "...own-refresher-folder-2" must not be treated as sitting inside
        // "...own-refresher-folder", a plain string prefix check would get this wrong.
        var ownFolder = Path.Combine(Projects, "own-refresher-folder");
        WriteTranscript(ownFolder + "-2", "session.jsonl", "{}\n");

        var report = TranscriptScopeReport.Inspect(Projects, [Cowork], ownFolder);

        Assert.Equal(1, report.TotalFiles);
    }

    [Theory]
    // Windows, this machine's own layout, matches, which is why the exclusion worked here.
    [InlineData(
        @"C:\Users\Maximilian\AppData\Local\O-view",
        "C--Users-Maximilian-AppData-Local-O-view")]
    // Windows, a user name holding a dot or an underscore, "." and "_" are each a separate
    // character Claude Code also maps to "-", missed before and silently breaking the
    // exclusion for exactly this population.
    [InlineData(@"C:\Users\john_doe\AppData\Local\O-view", "C--Users-john-doe-AppData-Local-O-view")]
    [InlineData(
        @"C:\Users\Maximilian\.paperclip\instances\default\_default",
        "C--Users-Maximilian--paperclip-instances-default--default")]
    // Linux, WorkingDirectory resolves to this shape, and the leading "/" plus the "."
    // immediately after it must both become "-", producing a double dash.
    [InlineData("/home/user/.local/share/O-view", "-home-user--local-share-O-view")]
    public void MangleCwd_MatchesHowClaudeCodeNamesTheProjectFolder(string cwd, string expected)
    {
        // issue #313: a folder name derived with only separators and colons replaced never
        // matches Claude Code's own folder on Linux, and silently misses on Windows whenever
        // the user name holds a "." or "_", so the exclusion this whole report depends on
        // quietly does nothing.
        Assert.Equal(expected, ClaudeCliRefresher.MangleCwd(cwd));
    }

    [Fact]
    public void ResolveOwnProjectFolder_MangledPreferredDirectory_WhenItExists()
    {
        var preferred = Directory.CreateTempSubdirectory("oview-own-").FullName;
        try
        {
            var expected = Path.Combine(
                ClaudeProjectsLocator.DefaultRoot, ClaudeCliRefresher.MangleCwd(preferred));

            Assert.Equal(expected, ClaudeCliRefresher.ResolveOwnProjectFolder(preferred));
        }
        finally
        {
            Directory.Delete(preferred);
        }
    }

    [Fact]
    public void ResolveOwnProjectFolder_Null_WhenThePreferredDirectoryIsMissing()
    {
        // O-view's data directory does not exist yet, first run, a fresh install, or a
        // cleared folder, so the refresher actually spawns out of the user-profile fallback
        // instead (ClaudeCliRefresher.WorkingDirectory). That fallback is the same home
        // directory the user's real sessions already use, so excluding its mangled form would
        // drop every genuine transcript they have, issue #313 the other way round. Null means
        // "exclude nothing", which is the only safe answer once the refresher and the user
        // share one folder.
        var absent = Path.Combine(Path.GetTempPath(), $"oview-own-absent-{Guid.NewGuid():N}");

        Assert.Null(ClaudeCliRefresher.ResolveOwnProjectFolder(absent));
    }
}
