using OView.Core.Providers.Jsonl;

namespace OView.Core.Tests;

/// <summary>
/// Where Claude is actually writing (GitHub issue #218).
///
/// <para>Every other scan looks in two known places and reports what it found there. That is
/// right for ingestion and wrong for a fault where the files have moved: a machine actively
/// running Cowork whose transcripts appear nowhere produces the same report as an idle one,
/// because both scans only ever look where the answer is already believed to be.</para>
/// </summary>
public class ClaudeWriteSurveyTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Directory.CreateTempSubdirectory("oview-writesurvey-").FullName;

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

    private string Root(string name) =>
        Directory.CreateDirectory(Path.Combine(_dir, name)).FullName;

    private static string Write(string root, string relative, string content = "{}\n")
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>
    /// <b>The point of the whole sweep.</b> A transcript somewhere this build does not scan is
    /// still found and its path printed — which is the difference between "nothing is being
    /// written" and "it is being written somewhere else".
    /// </summary>
    [Fact]
    public void ATranscriptOutsideTheKnownLocationsIsStillFoundAndNamed()
    {
        var data = Root("data");
        Write(data, Path.Combine("somewhere-new", "session-1.jsonl"));

        var text = ClaudeWriteSurvey.Inspect([("data", data)]).ToClipboardText(Now);

        Assert.Contains("1 file(s)", text, StringComparison.Ordinal);
        Assert.Contains("somewhere-new", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The line that cannot come back empty on a machine where Claude is running.</b>
    ///
    /// <para>Every other field here — and every scan in the app — asks after a shape someone has
    /// already thought of, so all of them report "none" identically whether the files moved or
    /// were never written. A session recorded under a name nobody anticipated has to show up as
    /// itself, or the sweep repeats one level up the same blindness it was written to fix.</para>
    /// </summary>
    [Fact]
    public void AFileMatchingNoKnownPatternStillAppearsAmongTheNewest()
    {
        var data = Root("data");
        Write(data, Path.Combine("some-new-place", "session.sqlite3"));

        var survey = ClaudeWriteSurvey.Inspect([("data", data)]);
        var root = Assert.Single(survey.Roots);

        Assert.Empty(root.Transcripts);
        Assert.Empty(root.Registries);
        Assert.Contains(root.Newest, f => f.Path.EndsWith("session.sqlite3", StringComparison.Ordinal));
        Assert.Contains("session.sqlite3", survey.ToClipboardText(Now), StringComparison.Ordinal);
    }

    /// <summary>Newest first, so the list answers "what changed most recently" at a glance.</summary>
    [Fact]
    public void TheNewestListIsOrderedByRecency()
    {
        var data = Root("data");
        var old = Write(data, "older.log");
        var recent = Write(data, "newer.log");
        File.SetLastWriteTimeUtc(old, Now.AddDays(-4).UtcDateTime);
        File.SetLastWriteTimeUtc(recent, Now.AddMinutes(-2).UtcDateTime);

        var root = Assert.Single(ClaudeWriteSurvey.Inspect([("data", data)]).Roots);

        Assert.EndsWith("newer.log", root.Newest[0].Path, StringComparison.Ordinal);
        Assert.EndsWith("older.log", root.Newest[1].Path, StringComparison.Ordinal);
    }

    /// <summary>
    /// Cowork's session register is swept for as well as read, because this is the report that
    /// answers "and if it is not where we look, where is it?".
    /// </summary>
    [Fact]
    public void TheSessionRegisterIsSweptForSeparatelyFromTranscripts()
    {
        var data = Root("data");
        Write(data, Path.Combine("claude-code-sessions", "org", "local_abc.json"));

        var survey = ClaudeWriteSurvey.Inspect([("data", data)]);
        var root = Assert.Single(survey.Roots);

        Assert.Empty(root.Transcripts);
        Assert.Single(root.Registries);
        Assert.Contains("local_*.json: 1 file(s)", survey.ToClipboardText(Now), StringComparison.Ordinal);
    }

    /// <summary>
    /// Immediate subdirectories by recency — what separates "Claude is not running" from
    /// "Claude is running and writing no transcripts". Nothing else in the bundle shows the
    /// second.
    /// </summary>
    [Fact]
    public void SubdirectoriesAreListedByRecency()
    {
        var config = Root("config");
        Write(config, Path.Combine("stale", "x.txt"));
        Write(config, Path.Combine("fresh", "y.txt"));
        Directory.SetLastWriteTimeUtc(Path.Combine(config, "stale"), Now.AddDays(-9).UtcDateTime);
        Directory.SetLastWriteTimeUtc(Path.Combine(config, "fresh"), Now.AddMinutes(-1).UtcDateTime);

        var root = Assert.Single(ClaudeWriteSurvey.Inspect([("config", config)]).Roots);

        Assert.Equal("fresh", root.Children[0].Name);
        Assert.Equal("stale", root.Children[1].Name);
    }

    /// <summary>
    /// Two roots showing the same files are one tree seen twice. MSIX presents a package's
    /// store through the canonical path as well as its own, neither is a link, and printing
    /// both in full doubles the longest section of the bundle while inviting the reader to add
    /// two numbers that are the same number.
    /// </summary>
    [Fact]
    public void ARootThatMirrorsAnotherIsNamedRatherThanRepeated()
    {
        var first = Root("first");
        var second = Root("second");
        Write(first, Path.Combine("sub", "a.jsonl"));
        Write(second, Path.Combine("sub", "a.jsonl"));

        var survey = ClaudeWriteSurvey.Inspect([("data", first), ("data", second)]);

        Assert.Null(survey.Roots[0].Mirrors);
        Assert.Equal(first, survey.Roots[1].Mirrors);
        Assert.Contains($"(same files as {first})", survey.ToClipboardText(Now), StringComparison.Ordinal);
    }

    /// <summary>Two roots with genuinely different contents are both reported in full.</summary>
    [Fact]
    public void RootsWithDifferentContentsAreBothDetailed()
    {
        var first = Root("first");
        var second = Root("second");
        Write(first, Path.Combine("sub", "a.jsonl"));
        Write(second, Path.Combine("sub", "b.jsonl"));

        var survey = ClaudeWriteSurvey.Inspect([("data", first), ("data", second)]);

        Assert.Null(survey.Roots[0].Mirrors);
        Assert.Null(survey.Roots[1].Mirrors);
    }

    /// <summary>
    /// A root that does not exist is named as missing rather than skipped — the whole purpose
    /// of printing resolved paths is that a wrong one is visible.
    /// </summary>
    [Fact]
    public void AMissingRootIsNamedRatherThanOmitted()
    {
        var text = ClaudeWriteSurvey
            .Inspect([("data", Path.Combine(_dir, "never-created"))])
            .ToClipboardText(Now);

        Assert.Contains("<-- missing", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Claude Code encodes a whole working directory into one folder name, so a session whose
    /// cwd was itself deep inside Claude's tree produces a segment hundreds of characters long
    /// — measured at 236 on the development machine. Both ends carry the diagnostic; only the
    /// middle is expendable.
    /// </summary>
    [Fact]
    public void AVeryLongPathIsElidedInTheMiddleAndKeepsBothEnds()
    {
        var data = Root("data");

        // ~200 characters: longer than the elision threshold and shorter than the 255-character
        // limit a single Windows path component actually has. The real one measured 236, so a
        // fixture that cannot exist on the platform would be testing nothing.
        var deep = string.Join("-", Enumerable.Repeat("segment", 25));
        Write(data, Path.Combine("head-of-the-path", deep, "tail-session.jsonl"));

        var text = ClaudeWriteSurvey.Inspect([("data", data)]).ToClipboardText(Now);

        Assert.Contains("head-of-the-path", text, StringComparison.Ordinal);
        Assert.Contains("tail-session.jsonl", text, StringComparison.Ordinal);
        Assert.Contains("…", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Trimming happens per segment, and each segment keeps its <b>head</b>.
    ///
    /// <para>This is a privacy property, not a layout one. An encoded working directory carries
    /// the account name near its start (<c>C--Users-ada-work</c>), and eliding the middle of the
    /// joined path cut wherever the character count happened to land — potentially straight
    /// through that name, leaving two halves <c>Redact</c> could match against neither. Keeping
    /// segment heads keeps the name intact, and therefore redactable.
    /// </summary>
    [Fact]
    public void TheStartOfAnEncodedDirectoryNameSurvivesTrimming()
    {
        var data = Root("data");
        var encoded = "C--Users-ada-" + string.Join("-", Enumerable.Repeat("deep", 30));
        Write(data, Path.Combine("projects", encoded, "session.jsonl"));

        var text = ClaudeWriteSurvey.Inspect([("data", data)]).ToClipboardText(Now);

        // The account name must reach the redactor whole, or it cannot be removed.
        Assert.Contains("C--Users-ada-", text, StringComparison.Ordinal);
        Assert.Contains("session.jsonl", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A file written while the sweep runs, or on a machine whose clock has just been
    /// corrected, must not be reported as "-0.0 h old" — an age that cannot exist and that
    /// reads as a rendering fault rather than as freshness.
    /// </summary>
    [Fact]
    public void AFileNewerThanTheClockIsNotReportedWithANegativeAge()
    {
        var data = Root("data");
        var file = Write(data, "future.jsonl");
        File.SetLastWriteTimeUtc(file, Now.AddHours(2).UtcDateTime);

        Assert.DoesNotContain("-0.0 h old",
            ClaudeWriteSurvey.Inspect([("data", data)]).ToClipboardText(Now), StringComparison.Ordinal);
    }
}
