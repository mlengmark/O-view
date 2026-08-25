using OView.App.Diagnostics;

namespace OView.App.Tests;

/// <summary>
/// The log is on by default now, so its two new obligations are load-bearing: it must stay
/// bounded on a machine that runs the app for months, and it must never fail the thing it is
/// recording. Neither mattered while it was opt-in and short-lived.
/// </summary>
public class FileLogTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose()
    {
        _dir.Dispose();
        GC.SuppressFinalize(this);
    }

    private string LogPath => _dir.File("oview.log");

    private string Generation(int n) => _dir.File($"oview.{n}.log");

    /// <summary>
    /// Beside the rollup store and the weekly-reset log, not in the install directory — which
    /// on Windows is per-user and replaced wholesale by every update, so a log kept there
    /// would be destroyed by the upgrade a user installs to fix the problem they are logging.
    /// </summary>
    [Fact]
    public void TheDefaultPathSitsWithTheAppsOtherLocalState()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "O-view", "logs", "oview.log");

        Assert.Equal(expected, FileLog.DefaultPath);
    }

    /// <summary>
    /// The bound that makes always-on affordable. Without it the file grows for as long as
    /// the app is installed, and this one writes three lines per poll.
    /// </summary>
    [Fact]
    public void TheLiveFileRollsOnceItPassesItsCapAndTheSetStaysBounded()
    {
        var log = new FileLog(LogPath, maxBytes: 2_000);
        var line = new string('x', 200);

        // Comfortably more than KeepGenerations rolls' worth.
        for (var i = 0; i < 400; i++)
        {
            log.Write(line);
        }

        Assert.True(File.Exists(LogPath));
        Assert.True(File.Exists(Generation(1)));
        Assert.True(File.Exists(Generation(2)));

        // Nothing beyond the retained generations survives, so the set has a ceiling rather
        // than a growth rate.
        Assert.False(File.Exists(Generation(3)));
        Assert.Equal(3, Directory.GetFiles(_dir.Path, "oview*.log").Length);
    }

    /// <summary>
    /// Rolling, not truncating. A stall is diagnosed from what happened immediately before
    /// it, so the generation that just rolled has to still be readable.
    /// </summary>
    [Fact]
    public void RollingKeepsThePreviousGenerationRatherThanDiscardingIt()
    {
        var log = new FileLog(LogPath, maxBytes: 1_000);
        log.Write(new string('a', 1_200));   // pushes the live file past the cap
        log.Write("after the roll");

        Assert.Contains("after the roll", File.ReadAllText(LogPath));
        Assert.Contains("aaaa", File.ReadAllText(Generation(1)));
    }

    /// <summary>
    /// It is called from the poll's own failure handlers. An exception here would replace a
    /// recorded failure with an unrecorded one — strictly worse than not logging at all.
    /// </summary>
    [Fact]
    public void AnUnwritableTargetIsSwallowedRatherThanThrown()
    {
        // A directory where the file should be: every write attempt fails, none may escape.
        Directory.CreateDirectory(LogPath);

        var log = new FileLog(LogPath);

        var thrown = Record.Exception(() => log.Write("this cannot be written anywhere"));

        Assert.Null(thrown);
    }

    /// <summary>
    /// What the support bundle carries. Oldest first, so a pasted bundle reads in the order
    /// events happened rather than backwards.
    /// </summary>
    [Fact]
    public void TailReturnsTheMostRecentLinesOldestFirst()
    {
        var log = new FileLog(LogPath);
        for (var i = 1; i <= 10; i++)
        {
            log.Write($"line {i}");
        }

        var tail = FileLog.Tail(LogPath, lines: 3);

        Assert.Equal(3, tail.Count);
        Assert.Contains("line 8", tail[0]);
        Assert.Contains("line 9", tail[1]);
        Assert.Contains("line 10", tail[2]);
    }

    /// <summary>
    /// Fewer lines than asked for is the ordinary state just after a fresh install, and a
    /// missing file is the state before the first write. Neither may fail the bundle.
    /// </summary>
    [Fact]
    public void TailCopesWithAShortLogAndWithNoLogAtAll()
    {
        Assert.Empty(FileLog.Tail(_dir.File("never-written.log")));

        var log = new FileLog(LogPath);
        log.Write("only line");

        var tail = FileLog.Tail(LogPath, lines: 30);

        Assert.Single(tail);
        Assert.Contains("only line", tail[0]);
    }

    /// <summary>
    /// A rolling log outlives the process, so "which lines belong to this launch" has to be
    /// answerable — a stall that survives a restart looks identical to one that does not
    /// until the restarts are visible.
    /// </summary>
    [Fact]
    public void TheSessionHeaderNamesTheBuildAndTheProcess()
    {
        var log = new FileLog(LogPath);
        log.WriteSessionHeader("0.6.22", "WindowsInstaller");

        var text = File.ReadAllText(LogPath);

        Assert.Contains("session start", text);
        Assert.Contains("v0.6.22", text);
        Assert.Contains("WindowsInstaller", text);
        Assert.Contains($"pid {Environment.ProcessId}", text);
    }

    /// <summary>
    /// Every line carries a full date. The old format was time-only, which was fine for a
    /// file that lived as long as one <c>--log</c> run and is not fine for one that spans
    /// days: "18:14:52" cannot be placed against a plan-history sample without it.
    /// </summary>
    [Fact]
    public void EveryLineIsStampedWithADateNotJustATime()
    {
        var log = new FileLog(LogPath);
        log.Write("something happened");

        var line = File.ReadAllLines(LogPath)[0];

        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}Z ", line);
    }
}
