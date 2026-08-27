using OView.Core.Providers.Jsonl;

namespace OView.Core.Tests;

/// <summary>
/// Cowork's own register of its sessions, checked against the transcripts O-view can find
/// (GitHub issue #218).
///
/// <para>Every other transcript field in the bundle answers "what did O-view find"; none of them
/// can answer "what should have been there". A machine actively running Cowork whose newest
/// local transcript was 52 hours old read as entirely healthy in every one of those lines. This
/// is the independent expectation to measure against.</para>
///
/// <para>Session ids here are deliberately not UUID-shaped. Real ones are, and the bundle
/// truncates them on the way out — but a UUID literal in a tracked file is what the repository's
/// identifier scan exists to reject, and a fixture is not a good reason to carve an exception
/// into a guard that protects against publishing real ones. Nothing in the lookup cares: the id
/// is a file name.</para>
/// </summary>
public class CoworkSessionRegistryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Directory.CreateTempSubdirectory("oview-coworksessions-").FullName;

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

    private string Sessions => Directory.CreateDirectory(
        Path.Combine(_dir, CoworkSessionReport.SessionsDirectoryName, "org", "user")).FullName;

    private string Projects => Directory.CreateDirectory(Path.Combine(_dir, "projects")).FullName;

    /// <summary>
    /// A registration as Cowork writes one: the session's own id, its cwd, when it was last
    /// touched — and a pile of session state around them, which is the reason only three
    /// fields are read.
    /// </summary>
    private void Register(string sessionId, DateTimeOffset lastActivity, string cwd = @"C:\work")
    {
        var json = $$"""
            {
              "sessionId": "local_{{sessionId}}",
              "cliSessionId": "{{sessionId}}",
              "cwd": "{{cwd.Replace("\\", "\\\\")}}",
              "lastFocusedAt": {{lastActivity.ToUnixTimeMilliseconds()}},
              "lastActivityAt": {{lastActivity.ToUnixTimeMilliseconds()}},
              "title": "SECRET-CONVERSATION-TITLE",
              "completedTurns": [{"a": 1}, {"b": [2, 3]}],
              "isArchived": false
            }
            """;

        File.WriteAllText(Path.Combine(Sessions, $"local_{sessionId}.json"), json);
    }

    /// <summary>
    /// A transcript, in a project directory whose name is deliberately <b>not</b> derived from
    /// the cwd. Claude Code's encoding rule is not ours and is not written down anywhere; the
    /// lookup is by file name for exactly that reason.
    /// </summary>
    private void WriteTranscript(string sessionId, string projectDir = "some--encoded--dir")
    {
        var dir = Directory.CreateDirectory(Path.Combine(Projects, projectDir)).FullName;
        File.WriteAllText(Path.Combine(dir, $"{sessionId}.jsonl"), "{}\n");
    }

    private CoworkSessionReport Inspect() =>
        CoworkSessionReport.Inspect(
            [Path.Combine(_dir, CoworkSessionReport.SessionsDirectoryName)], Projects, Now);

    /// <summary>A registered session whose transcript exists resolves, and is not a finding.</summary>
    [Fact]
    public void ASessionWithATranscriptResolves()
    {
        Register("session-resolved", Now.AddMinutes(-5));
        WriteTranscript("session-resolved");

        var report = Inspect();

        Assert.Equal(1, report.Registered);
        Assert.Equal(1, report.Resolved);
        Assert.Equal(0, report.Unresolved);
        Assert.Empty(report.RecentWithoutTranscript(Now));
        Assert.DoesNotContain("!!", report.ToClipboardText(Now), StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The finding.</b> A session Cowork touched minutes ago with nothing written for it
    /// means the machine is producing usage that nothing local records — which is exactly the
    /// state that reads as "the token tiles have stopped", and which no other field in the
    /// bundle can see.
    /// </summary>
    [Fact]
    public void ARecentSessionWithNoTranscriptIsTheFinding()
    {
        Register("session-recent", Now.AddMinutes(-10));

        var report = Inspect();
        var text = report.ToClipboardText(Now);

        Assert.Equal(1, report.Unresolved);
        Assert.Single(report.RecentWithoutTranscript(Now));
        Assert.Contains("have NO transcript", text, StringComparison.Ordinal);
        Assert.Contains("session-recent", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// An old registration without a transcript is ordinary, not a fault: Claude Code deletes
    /// its transcripts after ~30 days while the registration outlives them. Flagging those
    /// would bury the live case under a permanent list of expected absences.
    /// </summary>
    [Fact]
    public void AnOldSessionWithNoTranscriptIsNotFlagged()
    {
        Register("session-old", Now.AddDays(-40));

        var report = Inspect();

        Assert.Equal(1, report.Unresolved);
        Assert.Empty(report.RecentWithoutTranscript(Now));
        Assert.DoesNotContain("!!", report.ToClipboardText(Now), StringComparison.Ordinal);
    }

    /// <summary>
    /// The transcript is found by its file name wherever it sits. Rebuilding the encoded
    /// directory from the cwd would break the moment Claude Code changed a rule this project
    /// does not control — and break in the direction that invents a fault, reporting every
    /// session as missing on a machine where nothing is.
    /// </summary>
    [Fact]
    public void TheLookupDoesNotDependOnHowTheProjectDirectoryIsNamed()
    {
        Register("session-elsewhere", Now.AddMinutes(-5), cwd: @"C:\Users\someone\deep\path");
        WriteTranscript("session-elsewhere", projectDir: "nothing-like-the-cwd");

        Assert.Equal(1, Inspect().Resolved);
    }

    /// <summary>
    /// A machine that has never opened Cowork says so rather than rendering nothing — an
    /// omitted section is indistinguishable from one that failed.
    /// </summary>
    [Fact]
    public void AMachineWithNoCoworkSessionsSaysSo()
    {
        Assert.Contains("none registered", Inspect().ToClipboardText(Now), StringComparison.Ordinal);
    }

    /// <summary>
    /// Only the newest few registrations are read in full — they run to hundreds of kilobytes
    /// each and this builds on the UI thread — but the true total is still counted, so the
    /// bound is never mistaken for the whole picture.
    /// </summary>
    [Fact]
    public void TheTotalIsCountedEvenThoughOnlyTheNewestAreRead()
    {
        for (var i = 0; i < CoworkSessionReport.InspectLimit + 7; i++)
        {
            Register($"session-{i}", Now.AddMinutes(-i));
        }

        var report = Inspect();

        Assert.Equal(CoworkSessionReport.InspectLimit + 7, report.Registered);
        Assert.Equal(CoworkSessionReport.InspectLimit, report.Sessions.Count);
        Assert.Contains("newest read", report.ToClipboardText(Now), StringComparison.Ordinal);
    }

    /// <summary>
    /// These files carry conversation titles. The report reads three fields and never
    /// materialises the rest, and nothing identifying reaches the bundle — the same line the
    /// plan-history and transcript-scope reports hold.
    /// </summary>
    [Fact]
    public void NoConversationContentReachesTheReport()
    {
        Register("session-private", Now.AddMinutes(-5));

        Assert.DoesNotContain("SECRET-CONVERSATION-TITLE",
            Inspect().ToClipboardText(Now), StringComparison.Ordinal);
    }
}
