using OView.Core.Providers.CachedUsage;

namespace OView.Core.Tests;

/// <summary>
/// The real cost guard, against a real directory (issue #234).
///
/// <para><b>These exist because their absence shipped a bug.</b> Every test of
/// <see cref="ClaudeCliRefresher"/> injects a fake guard, so the guard's own rule — which files
/// count as this invocation's — was never exercised. The first version filtered on "created or
/// written since a watermark", which is wrong in the one way that matters: a Claude Code session
/// that is already running writes its transcript continuously and carries request ids on every
/// line. Measured on the development machine, an active session's transcript created at 12:23 and
/// still being written at 15:41 would have matched. The guard would have reported a charge on the
/// first refresh and permanently disabled the feature — on exactly the machines it is for.</para>
///
/// <para>The fix is identity, not time: only a file that did not exist before the invocation can
/// be the invocation's. <see cref="AnAlreadyRunningSessionIsNotACharge"/> is the regression.</para>
/// </summary>
public class TranscriptCostGuardTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("oview-costguard-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>A transcript that reached the model — the shape measured on a billed invocation.</summary>
    private string Billed(string name, string idField = "requestId")
    {
        var path = Path.Combine(_root, name);
        File.WriteAllLines(path,
        [
            """{"type":"user","message":{"role":"user","content":"hi"}}""",
            """{"type":"assistant","ID":"req_01ABC","message":{"usage":{"output_tokens":470}}}"""
                .Replace("ID", idField, StringComparison.Ordinal),
        ]);
        return path;
    }

    /// <summary>
    /// A transcript from a locally handled slash command. Six lines, no request id, no usage
    /// record — the measured shape of a free <c>claude -p "/usage"</c> invocation.
    /// </summary>
    private string Free(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllLines(path,
        [
            """{"type":"queue-operation","op":"enqueue"}""",
            """{"type":"user","message":{"role":"user","content":"/usage"}}""",
            """{"type":"system","subtype":"local_command"}""",
            """{"type":"last-prompt","prompt":"/usage"}""",
        ]);
        return path;
    }

    private TranscriptCostGuard Guard() => new(_root);

    /// <summary>
    /// <b>The regression.</b> A session already running when the refresh starts is not this
    /// invocation's, however recently it was written and however many request ids it carries.
    /// </summary>
    [Fact]
    public void AnAlreadyRunningSessionIsNotACharge()
    {
        Billed("live-session.jsonl");
        var guard = Guard();

        var before = guard.Snapshot();

        // The live session keeps writing while the refresh runs, exactly as it does in life.
        File.AppendAllLines(Path.Combine(_root, "live-session.jsonl"),
            ["""{"type":"assistant","requestId":"req_02DEF","message":{"usage":{}}}"""]);

        Assert.Null(guard.FindBilled(before));
    }

    /// <summary>The free invocation: a new transcript, and nothing in it reached the model.</summary>
    [Fact]
    public void ANewTranscriptWithNoRequestIdIsNotACharge()
    {
        Billed("live-session.jsonl");
        var guard = Guard();
        var before = guard.Snapshot();

        Free("refresh.jsonl");

        Assert.Null(guard.FindBilled(before));
    }

    /// <summary>The failure this guards: the argument stopped being a slash command.</summary>
    [Fact]
    public void ANewTranscriptCarryingARequestIdIsACharge()
    {
        Billed("live-session.jsonl");
        var guard = Guard();
        var before = guard.Snapshot();

        Billed("refresh.jsonl");

        Assert.Equal("refresh.jsonl", guard.FindBilled(before));
    }

    /// <summary>
    /// Cowork writes <c>request_id</c> on an otherwise identical record (CLAUDE.md rule 4).
    /// Checking one spelling is how a whole source goes unseen — here it would be a missed charge.
    /// </summary>
    [Fact]
    public void TheUnderscoreSpellingCountsToo()
    {
        var guard = Guard();
        var before = guard.Snapshot();

        Billed("refresh.jsonl", idField: "request_id");

        Assert.Equal("refresh.jsonl", guard.FindBilled(before));
    }

    /// <summary>
    /// Nested per-project directories are the real layout — Claude Code encodes a cwd into a
    /// directory name. A guard that only looked at the top level would miss every charge.
    /// </summary>
    [Fact]
    public void TranscriptsInProjectSubdirectoriesAreSeen()
    {
        var project = Directory.CreateDirectory(Path.Combine(_root, "C--Users-x")).FullName;
        var guard = Guard();
        var before = guard.Snapshot();

        File.WriteAllText(
            Path.Combine(project, "new.jsonl"),
            """{"type":"assistant","requestId":"req_03GHI","message":{"usage":{}}}""");

        Assert.Equal("new.jsonl", guard.FindBilled(before));
    }

    /// <summary>
    /// Two projects can hold transcripts with the same file name. Keying the snapshot on names
    /// would treat a genuinely new file as pre-existing — a missed charge, which is the direction
    /// that costs money.
    /// </summary>
    [Fact]
    public void SameNamedTranscriptsInDifferentProjectsAreDistinct()
    {
        var a = Directory.CreateDirectory(Path.Combine(_root, "proj-a")).FullName;
        File.WriteAllText(Path.Combine(a, "session.jsonl"), """{"type":"user"}""");

        var guard = Guard();
        var before = guard.Snapshot();

        var b = Directory.CreateDirectory(Path.Combine(_root, "proj-b")).FullName;
        File.WriteAllText(
            Path.Combine(b, "session.jsonl"),
            """{"type":"assistant","requestId":"req_04JKL","message":{"usage":{}}}""");

        Assert.Equal("session.jsonl", guard.FindBilled(before));
    }

    /// <summary>
    /// An absent tree is not evidence of a charge, and must not throw. A machine that has never
    /// run Claude Code has no projects directory, and the refresh there simply finds nothing.
    /// </summary>
    [Fact]
    public void AMissingRootIsNotACharge()
    {
        var guard = new TranscriptCostGuard(Path.Combine(_root, "does-not-exist"));

        Assert.Empty(guard.Snapshot());
        Assert.Null(guard.FindBilled(new HashSet<string>()));
    }

    /// <summary>
    /// An unreadable transcript throws rather than reporting "not billed".
    /// <c>ClaudeCliRefresher</c> turns a throwing guard into a charge, which is the conservative
    /// direction: stopping a feature is recoverable, a silent 50K-token-per-poll leak is not.
    /// </summary>
    [Fact]
    public void AnUnreadableTranscriptThrowsRatherThanReportingNoCharge()
    {
        var guard = new TranscriptCostGuard(
            () => [new FileInfo(Path.Combine(_root, "new.jsonl"))],
            _ => throw new IOException("locked"));

        Assert.Throws<IOException>(() => guard.FindBilled(new HashSet<string>()));
    }
}
