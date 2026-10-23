using OView.Core.Providers.PlanHistory;

namespace OView.Core.Tests;

public class PlanHistoryDiagnosticsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("oview-diag-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string json)
    {
        var path = Path.Combine(_dir, "plan-usage-history.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static string Sample(long epochMs, string org = "org-a", int fh = 40, int sd = 7) =>
        $"{{\"t\":{epochMs},\"org\":\"{org}\",\"u\":{{\"fh\":{fh},\"sd\":{sd}}}}}";

    private static long MinutesAgo(int m) => DateTimeOffset.UtcNow.AddMinutes(-m).ToUnixTimeMilliseconds();

    [Fact]
    public void Missing_file_is_reported_as_such()
    {
        var report = PlanHistoryDiagnostics.Inspect(Path.Combine(_dir, "absent.json"));

        Assert.Equal(PlanDataStatus.FileMissing, report.Status);
        Assert.False(report.FileExists);
        Assert.Contains("found none", report.Explain(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Both writers of the two percentages are named, and the file is not called "the"
    /// source of them. Claude Code caches the same two figures, so the single-source
    /// phrasing was false for exactly the reader most likely to meet this sentence.
    /// </summary>
    [Fact]
    public void The_missing_file_sentence_names_both_writers_of_the_percentages()
    {
        var text = PlanHistoryDiagnostics.Inspect(Path.Combine(_dir, "absent.json")).Explain();

        Assert.Contains("Claude Desktop", text, StringComparison.Ordinal);
        Assert.Contains("Claude Code", text, StringComparison.Ordinal);
        Assert.DoesNotContain("the source of session and weekly", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Neither the missing-file nor the stale sentence tells the reader to start Claude
    /// Desktop. A Claude Code user on a machine with no Desktop cannot act on that, and it
    /// was the only instruction either sentence gave them. State what was looked for and
    /// what was found; the diagnostics report carries the detail.
    /// </summary>
    [Fact]
    public void Neither_sentence_sends_the_reader_to_an_application()
    {
        var missing = PlanHistoryDiagnostics.Inspect(Path.Combine(_dir, "absent.json")).Explain();
        var stale = PlanHistoryDiagnostics
            .Inspect(Write($$"""{"version":2,"samples":[{{Sample(MinutesAgo(120))}}]}"""))
            .Explain();

        foreach (var text in new[] { missing, stale })
        {
            Assert.DoesNotContain("start it", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("is not running", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("reopen this panel", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Unparseable_file_is_unreadable_not_missing()
    {
        var report = PlanHistoryDiagnostics.Inspect(Write("this is not json"));

        Assert.Equal(PlanDataStatus.Unreadable, report.Status);
        Assert.True(report.FileExists);
        Assert.NotNull(report.Detail);
    }

    [Fact]
    public void Entries_present_but_wrong_shape_is_schema_drift_not_empty()
    {
        // The distinguishing signature: the array HAS entries, but none parse. A plain
        // "no data" message cannot express this; the raw-vs-valid gap can.
        var report = PlanHistoryDiagnostics.Inspect(Write(
            """{"version":3,"samples":[{"t":"2026-07-25T09:00:00Z","org":"org-a","usage":{"fiveHour":40}}]}"""));

        Assert.Equal(PlanDataStatus.NoValidSamples, report.Status);
        Assert.Equal(1, report.RawSampleCount);
        Assert.Equal(0, report.ValidSampleCount);
        Assert.Contains("expected format", report.Explain());
    }

    [Fact]
    public void Explanations_state_the_observation_not_an_assumption()
    {
        // A user with Claude Desktop open was told to "install and run the Claude Desktop
        // app", an assertion O-view had no basis for. Every explanation must name the
        // path it actually looked at, and must not claim Desktop is absent.
        var missing = PlanHistoryDiagnostics.Inspect(Path.Combine(_dir, "absent.json"));
        Assert.Contains("absent.json", missing.Explain());
        Assert.DoesNotContain("Install and run", missing.Explain(), StringComparison.OrdinalIgnoreCase);

        var unreadable = PlanHistoryDiagnostics.Inspect(Write("not json"));
        Assert.Contains("plan-usage-history.json", unreadable.Explain());
    }

    [Fact]
    public void Missing_samples_array_is_called_out()
    {
        var report = PlanHistoryDiagnostics.Inspect(Write("""{"version":2}"""));

        Assert.Equal(PlanDataStatus.NoValidSamples, report.Status);
        Assert.Contains("samples", report.Detail!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Fresh_valid_samples_are_ok_with_no_explanation()
    {
        var report = PlanHistoryDiagnostics.Inspect(Write($$"""{"version":2,"samples":[{{Sample(MinutesAgo(3))}}]}"""));

        Assert.Equal(PlanDataStatus.Ok, report.Status);
        Assert.Equal(1, report.ValidSampleCount);
        Assert.Equal(["org-a"], report.Orgs);
        Assert.Equal("", report.Explain());
    }

    [Fact]
    public void Old_samples_are_stale_and_say_so()
    {
        var report = PlanHistoryDiagnostics.Inspect(Write($$"""{"version":2,"samples":[{{Sample(MinutesAgo(120))}}]}"""));

        Assert.Equal(PlanDataStatus.Stale, report.Status);
        // Names the file whose newest sample aged, not an application presumed to be idle.
        Assert.Contains("older than O-view's freshness window", report.Explain(), StringComparison.Ordinal);
        Assert.Contains("plan-usage-history.json", report.Explain(), StringComparison.Ordinal);
    }

    [Fact]
    public void Clipboard_text_carries_the_facts_needed_to_diagnose()
    {
        var report = PlanHistoryDiagnostics.Inspect(Write(
            $$"""{"version":2,"samples":[{{Sample(MinutesAgo(4), "org-x")}}]}"""));

        var text = report.ToClipboardText("0.4.6");

        Assert.Contains("0.4.6", text);
        Assert.Contains("org-x", text);
        Assert.Contains("plan-usage-history.json", text);
        Assert.Contains("Ok", text);
    }
}
