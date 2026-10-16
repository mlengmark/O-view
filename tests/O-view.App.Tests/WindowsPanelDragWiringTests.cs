using System.Text.RegularExpressions;

namespace OView.App.Tests;

/// <summary>
/// The Windows head never gives a window a position, and never stores one, except through the
/// shared placement rules — <c>PanelPlacementMemory</c> for a panel the user has dragged and
/// <c>WorkAreaPlacement</c> for one that has not.
///
/// <para><b>Why this is asserted over the source.</b> No test project references
/// <c>O-view.Tray</c> — it is WPF and <c>net10.0-windows</c>, and the three test projects must
/// build and pass on Linux (CLAUDE.md rule 1). So there is no way to call into this head at all,
/// and a later change that set a window's <c>Left</c> from its own arithmetic, or wrote a place
/// straight to the file, would compile and run and look right on the one machine it was tried
/// on. What would be lost is invisible on a single desktop: the clamp that keeps a restored
/// panel inside a work area that has since shrunk, and the snap that puts a released one flush
/// to an edge. The property worth keeping is therefore about what the head's source may
/// <i>contain</i>, which is what is read here — the same reasoning, and the same shape, as
/// <see cref="SkinBoundaryTests"/>.</para>
///
/// <para><b>Two files, not one.</b> The funnel is a pair: <c>PopupPositioner</c> decides every
/// position and <c>DockedFlyout</c> applies it to a window. Allowing both is what lets the scan
/// below be a plain "no other file" check rather than a judgement about which assignments are
/// placements.</para>
/// </summary>
public class WindowsPanelDragWiringTests
{
    private const string Head = "O-view.Tray";

    private const string Positioner = "src/O-view.Tray/Popup/PopupPositioner.cs";
    private const string Flyout = "src/O-view.Tray/Popup/DockedFlyout.cs";

    /// <summary>
    /// Below this, something has moved and the scan is reading an almost empty directory —
    /// which would pass while asserting nothing. The head has forty-nine source files today.
    /// </summary>
    private const int FewestFilesTheHeadCanHave = 20;

    /// <summary>
    /// A window's position being written: an assignment, or the tuple deconstruction the
    /// placement pair actually uses. Reading one is not a write, so a trailing operator does not
    /// match — <c>r.Left - r.Right</c> and <c>ink.Top)</c> are both left alone.
    /// </summary>
    private static readonly Regex PositionWrite =
        new(@"\b\w+\.(Left|Top)[ \t]*(=|,)", RegexOptions.Compiled);

    [Fact]
    public void EveryWindowPositionComesFromThePlacementPair()
    {
        var offenders = Scan(PositionWrite, allowed: [Positioner, Flyout]);

        Assert.True(offenders.Count == 0,
            "a window's position may only be set by PopupPositioner (which decides it) and "
            + $"DockedFlyout (which applies it), so that the shared rules stay the only two "
            + $"sources of one:{Environment.NewLine}{Report(offenders)}");
    }

    /// <summary>
    /// The funnel's two answers are the two shared rules and nothing else. Asserted by name
    /// because that is the whole claim: a <c>Place</c> that stopped asking the memory would
    /// silently drop the clamp, and one that stopped asking <c>WorkAreaPlacement</c> would drop
    /// the tray corner every panel without a remembered place still opens at.
    /// </summary>
    [Theory]
    [InlineData("PanelPlacementMemory", "the remembered-place rule is not named at all")]
    [InlineData(".Resolve(", "nothing re-clamps a remembered place into today's work area")]
    [InlineData(".Remember(", "nothing snaps and stores a released place")]
    [InlineData("WorkAreaPlacement.Place(", "the tray-corner placement is gone")]
    public void ThePlacementFunnelAsksBothSharedRules(string required, string whatIsLost)
    {
        var source = ReadHeadFile(Positioner);

        Assert.True(source.Contains(required, StringComparison.Ordinal),
            $"{Positioner} no longer contains \"{required}\" — {whatIsLost}");
    }

    /// <summary>
    /// Only the funnel reads or writes the stored place, and only it names the corner rule's
    /// placement call. Either one reached from elsewhere is a position that skipped the clamp or
    /// the snap — the two things a single desktop cannot show are missing.
    /// </summary>
    [Theory]
    [InlineData("PanelPositionFile")]
    [InlineData("WorkAreaPlacement.Place(")]
    public void OnlyTheFunnelReachesTheStoredPlaceAndTheCornerRule(string pattern)
    {
        var offenders = Scan(new Regex(Regex.Escape(pattern), RegexOptions.Compiled), allowed: [Positioner]);

        Assert.True(offenders.Count == 0,
            $"\"{pattern}\" belongs to {Positioner} alone — every place the head stores or "
            + $"restores goes through the shared rule there:{Environment.NewLine}{Report(offenders)}");
    }

    /// <summary>
    /// The drag's release reaches the rule. <c>DragMove</c> is where a position arrives from
    /// outside the head's own arithmetic — the system's move loop sets it — so the file that
    /// starts that loop is the file that must hand the result to <c>PopupPositioner.Remember</c>.
    /// A second <c>DragMove</c> somewhere else would be a second release path with no such
    /// obligation, which is why the count is pinned rather than only the pairing.
    /// </summary>
    [Fact]
    public void EveryDragReleaseSavesThroughTheSharedRule()
    {
        var files = HeadFiles()
            .Where(file => File.ReadAllText(file.Path).Contains("DragMove", StringComparison.Ordinal))
            .ToArray();

        Assert.True(files.Length == 1,
            $"expected exactly one file in {Head} to run the system move loop, found "
            + $"{files.Length}: {string.Join(", ", files.Select(f => f.Name))}");

        Assert.True(
            File.ReadAllText(files[0].Path).Contains("PopupPositioner.Remember(", StringComparison.Ordinal),
            $"{files[0].Name} drags the window but never calls PopupPositioner.Remember — a "
            + "released position that is not handed to the shared rule is never snapped, never "
            + "stored, and gone at the next open");
    }

    /// <summary>
    /// Files matching <paramref name="pattern"/> outside <paramref name="allowed"/>, as
    /// <c>path:line  text</c>.
    /// </summary>
    private static List<string> Scan(Regex pattern, string[] allowed)
    {
        var offenders = new List<string>();

        foreach (var (path, name) in HeadFiles())
        {
            if (allowed.Contains(name, StringComparer.Ordinal))
            {
                continue;
            }

            offenders.AddRange(File.ReadLines(path)
                .Select((line, i) => (Number: i + 1, Text: line.Trim()))
                .Where(line => pattern.IsMatch(line.Text))
                .Select(line => $"{name}:{line.Number}  {line.Text}"));
        }

        return offenders;
    }

    private static string ReadHeadFile(string name)
    {
        var path = Path.Combine(RepositoryRoot(), name.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"no file at {name} — the assertion below would read nothing");
        return File.ReadAllText(path);
    }

    /// <summary>
    /// Everything the Windows head is written in, minus what the build wrote, with the
    /// repository-relative name each failure message reports.
    /// </summary>
    private static (string Path, string Name)[] HeadFiles()
    {
        var root = RepositoryRoot();
        var directory = Path.Combine(root, "src", Head);
        Assert.True(Directory.Exists(directory), $"no head at {directory} — the scan would read nothing");

        var files = new[] { "*.cs", "*.xaml" }
            .SelectMany(pattern => Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => (Path: f, Name: Path.GetRelativePath(root, f).Replace('\\', '/')))
            .ToArray();

        Assert.True(files.Length >= FewestFilesTheHeadCanHave,
            $"only {files.Length} source files under {directory}, so these tests are no longer "
            + "reading the head they name — has it moved?");

        return files;
    }

    /// <summary>
    /// The offending lines, and the files that were read. The scanned list is on every failure
    /// for the reason <see cref="SkinBoundaryTests"/> gives: a scan that has quietly stopped
    /// looking at the file in question is the one way this can be green and worthless.
    /// </summary>
    private static string Report(List<string> offenders) =>
        string.Join(Environment.NewLine, offenders)
        + $"{Environment.NewLine}{Environment.NewLine}Scanned "
        + $"{string.Join(", ", HeadFiles().Select(f => f.Name))}";

    /// <summary>
    /// Walks up from the test binary to the directory holding the solution file, exactly as
    /// <see cref="SkinBoundaryTests"/> does — the tests run from <c>bin/</c>, several levels
    /// below, and the depth differs by configuration.
    /// </summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "O-view.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }
}
