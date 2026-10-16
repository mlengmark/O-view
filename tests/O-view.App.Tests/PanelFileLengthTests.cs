namespace OView.App.Tests;

/// <summary>
/// The file that assembles a whole panel stays under 600 lines on both heads
/// (docs/architecture.md), for each head's own copy of the same reason: past that length a
/// panel file is usually one that has started computing content again rather than only laying
/// out the view model it was handed — the layering <see cref="SkinBoundaryTests"/> guards from
/// the other direction.
///
/// <para><b>Why a line count, not a name scan.</b> Nothing about computing content needs a
/// banned word — it looks exactly like ordinary C#, formatting a number or branching on a
/// figure — so there is nothing to grep for. Length is the symptom that is actually visible from
/// outside: every split this repository has made so far (<c>PopupScroll</c>, <c>PopupFit</c>,
/// <c>PanelScroll</c>, and the pieces named for what they draw on each head) was made to bring
/// the assembling file back under this limit, not the other way round.</para>
///
/// <para>Both files are read directly rather than through <c>HeadFiles()</c>'s whole-head scan:
/// the claim is about these two specific files, not about the head they sit in.</para>
/// </summary>
public class PanelFileLengthTests
{
    private const string WindowsPanel = "src/O-view.Tray/Popup/PopupWindow.xaml.cs";
    private const string LinuxPanel = "src/O-view.Linux/Panel/PanelContent.cs";

    private const int MostLinesAPanelFileMayHave = 600;

    [Theory]
    [InlineData(WindowsPanel)]
    [InlineData(LinuxPanel)]
    public void ThePanelFileStaysUnderTheLineLimit(string name)
    {
        var path = Path.Combine(RepositoryRoot(), name.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"no file at {name} — the assertion below would read nothing");

        var lines = File.ReadAllLines(path).Length;

        Assert.True(lines < MostLinesAPanelFileMayHave,
            $"{name} is {lines} lines, at or past the {MostLinesAPanelFileMayHave}-line limit "
            + "docs/architecture.md sets for the file that assembles a whole panel — split "
            + "another piece out of it named for what that piece draws or does, the way "
            + "PopupScroll, PopupFit and PanelScroll already were");
    }

    /// <summary>
    /// Walks up from the test binary to the directory holding the solution file, the way
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
