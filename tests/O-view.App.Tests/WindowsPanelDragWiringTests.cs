using System.Text.RegularExpressions;
using OView.App.Rendering;

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
    /// which would pass while asserting nothing. The head has fifty source files today.
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
    [InlineData(".Nudge(", "the keyboard moves the panel without the shared rule, so a step "
        + "skips the clamp, the snap and the save a release goes through")]
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
    /// And the release moves the window the user is looking at, rather than only the file — the
    /// B15 half of the question, asked of this head because the Linux one failed it: its settled
    /// place reached the log and the store and the open panel stayed where it was dropped.
    ///
    /// <para><b>Three calls in one order is the whole claim.</b> <c>DragMove</c> blocks until the
    /// user lets go, so what follows it runs with the panel still open and the system's move loop
    /// already finished — the timing the Linux head has to work for. <c>Remember</c> snaps and
    /// stores the released place and <c>Redock</c> reads it straight back onto the window;
    /// either one missing, or <c>Redock</c> running first, leaves the window where it was
    /// dropped.</para>
    ///
    /// <para>Read from the source for the reason this whole file gives: no test project can
    /// reference this head, so a <c>Redock</c> quietly dropped from the release would compile,
    /// run, and look right on the one desktop it was tried on.</para>
    /// </summary>
    [Fact]
    public void TheDragReleaseMovesTheOpenWindowToTheSettledPlace()
    {
        var release = MethodBody(ReadHeadFile(Flyout), "private void Drag()");

        var order = new[] { "host.DragMove()", "PopupPositioner.Remember(", "Redock(" }
            .Select(call => (Call: call, At: release.IndexOf(call, StringComparison.Ordinal)))
            .ToArray();

        foreach (var (call, at) in order)
        {
            Assert.True(at >= 0,
                $"DockedFlyout.Drag no longer calls {call}, so a released panel is either never "
                + $"stored or never moved to where the shared rule puts it:{Environment.NewLine}{release}");
        }

        Assert.True(order.Select(step => step.At).SequenceEqual(order.Select(step => step.At).Order()),
            "DockedFlyout.Drag must run the move loop, then store the released place, then "
            + $"re-place the window from it, in that order:{Environment.NewLine}{release}");
    }

    /// <summary>
    /// What <c>Redock</c> does with it: the window's own position, straight out of the funnel. A
    /// re-placement that read the position from anywhere else would apply a settled place that
    /// had skipped the clamp — which is the one thing a drop half off-screen needs.
    /// </summary>
    [Theory]
    [InlineData("PopupPositioner.Place(")]
    [InlineData("host.Left")]
    public void TheRePlacementSetsTheWindowFromTheFunnel(string required)
    {
        var redock = MethodBody(ReadHeadFile(Flyout), "public void Redock(");

        Assert.True(redock.Contains(required, StringComparison.Ordinal),
            $"DockedFlyout.Redock no longer contains \"{required}\", so the surface is not moved "
            + $"to the place the shared rule answers with:{Environment.NewLine}{redock}");
    }

    /// <summary>
    /// And the place that re-placement applies, for the drop B15 reported: dropped at 791,571 on
    /// a 1024×768 screen whose work area is 958×736+66+32, a 400×712 panel ends at 624,56 —
    /// pulled fully back on screen and flush into the corner it was thrown at.
    ///
    /// <para>The rule is shared, so this is the figure both heads' releases apply. Asserted here
    /// as well as on the Linux side because the two heads reach it by completely different
    /// routes, and the Windows one has no other test that can run at all.</para>
    /// </summary>
    [Fact]
    public void ADropHalfOffScreenIsPulledBackInsideTheWorkArea()
    {
        var memory = new PanelPlacementMemory(new FakePanelPositionStore());
        var work = new DipBox(66, 32, 958, 736);

        memory.Remember("DISPLAY1", leftDip: 791, topDip: 571, widthDip: 400, heightDip: 712, work);
        var settled = memory.Resolve("DISPLAY1", work, widthDip: 400, heightDip: 712);

        Assert.NotNull(settled);
        Assert.Equal(624, settled!.Value.LeftDip, precision: 3);
        Assert.Equal(56, settled.Value.TopDip, precision: 3);
    }

    /// <summary>
    /// The keyboard reaches the same funnel. Which arrow key means which direction is read in
    /// exactly one file, and that file is one of the pair — anywhere else it would be a second
    /// way to move the panel, with its own arithmetic and no obligation to save what it did.
    ///
    /// <para>Pinned by the count for the reason <see cref="EveryDragReleaseSavesThroughTheSharedRule"/>
    /// gives, and because the mapping has nowhere else it could sensibly go: the host window
    /// forwards the press and reads no key of its own.</para>
    ///
    /// <para>The horizontal key is the sentinel because the tray menu reads Up and Down for its
    /// own row navigation, which moves a highlight rather than a window and has nothing to do
    /// with placement.</para>
    /// </summary>
    [Fact]
    public void TheKeyboardStepIsMappedOnlyInThePlacementPair()
    {
        var files = HeadFiles()
            .Where(file => File.ReadAllText(file.Path).Contains("Key.Right", StringComparison.Ordinal))
            .ToArray();

        Assert.True(files.Length == 1,
            $"expected exactly one file in {Head} to read the arrow keys, found {files.Length}: "
            + string.Join(", ", files.Select(f => f.Name)));

        Assert.Equal(Flyout, files[0].Name);

        Assert.True(
            File.ReadAllText(files[0].Path).Contains("PopupPositioner.Nudge(", StringComparison.Ordinal),
            $"{files[0].Name} reads the arrow keys but never calls PopupPositioner.Nudge — a "
            + "step that is not handed to the shared rule is never snapped, never clamped and "
            + "never stored, so the panel is back at its old place at the next open");
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

    /// <summary>
    /// The body of the method <paramref name="signature"/> opens, by matching its braces — so a
    /// claim about what one method does is read from that method rather than from the file it
    /// happens to share with a dozen others.
    /// </summary>
    private static string MethodBody(string source, string signature)
    {
        var at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"no \"{signature}\" in the file — the assertion below would read nothing");

        var open = source.IndexOf('{', at);
        Assert.True(open > 0, $"\"{signature}\" has no body");

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            depth += source[i] switch { '{' => 1, '}' => -1, _ => 0 };
            if (depth == 0)
            {
                return source[open..(i + 1)];
            }
        }

        Assert.Fail($"\"{signature}\" has no closing brace — the file does not parse");
        return string.Empty;
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
