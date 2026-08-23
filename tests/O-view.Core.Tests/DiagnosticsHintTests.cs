using OView.Core.Models;
using OView.Core.Providers.Jsonl;
using OView.Core.Providers.PlanHistory;

namespace OView.Core.Tests;

/// <summary>
/// The panel's explanatory copy tells a stuck user what to do next. Until v0.6.0 it said
/// "Right-click the tray icon → Copy diagnostics" in five places — an instruction that is
/// simply false on Linux, where the SNI menu carries Exit only.
///
/// <para>This is rule 6 at its sharpest: the copy is shown precisely when something has
/// already gone wrong, so sending the reader to a menu item that does not exist turns one
/// failure into two.</para>
/// </summary>
public class DiagnosticsHintTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("oview-hint-").FullName;

    public void Dispose()
    {
        DiagnosticsHint.Reset();   // static state must not leak between tests
        Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Every sentence in the app that points at the diagnostics report.</summary>
    private string[] AllExplanations()
    {
        var missing = PlanHistoryDiagnostics.Inspect(Path.Combine(_dir, "absent.json"));

        var unreadableDir = Path.Combine(_dir, "unreadable");
        Directory.CreateDirectory(unreadableDir);   // a directory where a file is expected
        var unreadable = PlanHistoryDiagnostics.Inspect(unreadableDir);

        // The CLI-only banner (issue #170) points at diagnostics too — for the user who does
        // run Desktop but whose packaged install redirected the file out of reach. Both of
        // its wordings compose the instruction, so both belong under these invariants.
        var projects = Path.Combine(_dir, "projects", "proj-a");
        Directory.CreateDirectory(projects);
        File.WriteAllText(Path.Combine(projects, "session.jsonl"), "{}\n");
        var scope = TranscriptScopeReport.Inspect(Path.Combine(_dir, "projects"), []);

        return
        [
            missing.Explain(),
            unreadable.Explain(),
            TranscriptScopeReport.Inspect(null, []).Explain(),
            PanelBanner.Resolve(false, missing, scope, tokens31Days: 1)!.Detail,
            PanelBanner.Resolve(false, missing, scope, tokens31Days: 0)!.Detail,
        ];
    }

    [Fact]
    public void TheDefaultInstructionIsTrueOnEveryPlatform()
    {
        // An unconfigured head gets something vague rather than something wrong.
        Assert.DoesNotContain("tray", DiagnosticsHint.Default, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("right-click", DiagnosticsHint.Default, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("terminal", DiagnosticsHint.Default, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The regression itself. With the Linux head's instruction configured, no explanation
    /// anywhere may mention a tray menu — that is the affordance this head does not have.
    /// </summary>
    [Fact]
    public void TheLinuxInstructionLeavesNoMentionOfAMenuThisHeadDoesNotHave()
    {
        DiagnosticsHint.Use("Run o-view --diagnose in a terminal");

        foreach (var text in AllExplanations())
        {
            Assert.DoesNotContain("right-click", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("tray menu", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("tray icon", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("o-view --diagnose", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheWindowsInstructionStillNamesTheMenuItemThatExistsThere()
    {
        DiagnosticsHint.Use("Right-click the tray icon → Copy diagnostics");

        foreach (var text in AllExplanations())
        {
            Assert.Contains("Copy diagnostics", text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The phrase is composed into "{Instruction} to report this." and its siblings, so a
    /// trailing full stop or colon would produce "…diagnostics. to report this."
    /// </summary>
    [Fact]
    public void TrailingPunctuationIsTrimmedSoTheSentencesReadCorrectly()
    {
        DiagnosticsHint.Use("Run o-view --diagnose.");
        Assert.Equal("Run o-view --diagnose", DiagnosticsHint.Instruction);

        foreach (var text in AllExplanations())
        {
            Assert.DoesNotContain(". to report this", text, StringComparison.Ordinal);
            Assert.DoesNotContain(". and report this", text, StringComparison.Ordinal);
            Assert.DoesNotContain(". to see the exact", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ABlankInstructionIsIgnoredRatherThanLeavingAHoleInTheSentence()
    {
        DiagnosticsHint.Use("Run o-view --diagnose in a terminal");
        DiagnosticsHint.Use("   ");

        Assert.Equal("Run o-view --diagnose in a terminal", DiagnosticsHint.Instruction);
        foreach (var text in AllExplanations())
        {
            Assert.DoesNotContain("  to report this", text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Whatever the instruction, the copy must still say what O-view *observed*. The hint
    /// replaced a hard-coded remedy; it must not have taken the evidence with it.
    /// </summary>
    [Fact]
    public void TheObservationSurvivesWhicheverInstructionIsConfigured()
    {
        foreach (var instruction in new[]
                 {
                     "Right-click the tray icon → Copy diagnostics",
                     "Run o-view --diagnose in a terminal",
                 })
        {
            DiagnosticsHint.Use(instruction);

            var missing = PlanHistoryDiagnostics.Inspect(Path.Combine(_dir, "absent.json")).Explain();
            Assert.Contains("absent.json", missing, StringComparison.Ordinal);
            Assert.Contains("searched", missing, StringComparison.OrdinalIgnoreCase);
            // The bug this copy was rewritten for in the first place (rule 6).
            Assert.DoesNotContain("Install and run", missing, StringComparison.OrdinalIgnoreCase);
        }
    }
}
