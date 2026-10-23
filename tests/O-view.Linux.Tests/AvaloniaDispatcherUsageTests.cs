using System.Text.RegularExpressions;

namespace OView.Linux.Tests;

/// <summary>
/// Every test file that touches the real <see cref="Avalonia.Threading.Dispatcher"/> claims it
/// for its own thread first, through <see cref="AvaloniaDispatcherTestSupport"/>.
///
/// <para><b>Why this is asserted over the source.</b> The race that class guards against is rare
/// on an idle machine and only shows up once several test runs share the machine, so a test
/// class that skips the claim can sit green for a long time before it flakes, and when it does,
/// nothing points back at the missing call. Scanning for the pairing catches it the moment the
/// class is added, rather than the next time the machine is busy.</para>
/// </summary>
public class AvaloniaDispatcherUsageTests
{
    private static readonly Regex TouchesTheRealDispatcher = new(@"\bDispatcher\.UIThread\b", RegexOptions.Compiled);
    private static readonly Regex ClaimsItFirst = new(@"\bAvaloniaDispatcherTestSupport\.ClaimUiThreadForThisThread\(\)", RegexOptions.Compiled);

    /// <summary>
    /// Files the sweep itself must not flag, with the reason, kept explicit so that excluding
    /// one is a decision someone wrote down, rather than a gap the sweep quietly stops seeing.
    ///
    /// <para><c>AvaloniaDispatcherTestSupport.cs</c> names the dispatcher without claiming it,
    /// it is the claim. <c>AvaloniaDispatcherUsageTests.cs</c> is this file: its own failure
    /// message names the pattern it looks for. <c>TrayClickRuntimeTests.cs</c> names the real
    /// dispatcher only in a doc comment about the <c>linux-runtime</c> job, which runs for real
    /// over a session bus and display in its own process, there is no in-process touch here for
    /// a claim to guard.</para>
    /// </summary>
    private static readonly string[] NotFlagged =
    [
        "AvaloniaDispatcherTestSupport.cs",
        "AvaloniaDispatcherUsageTests.cs",
        $"Runtime{Path.DirectorySeparatorChar}TrayClickRuntimeTests.cs",
    ];

    [Fact]
    public void EveryFileThatTouchesTheRealDispatcherClaimsItFirst()
    {
        var root = RepositoryRoot();
        var directory = Path.Combine(root, "tests", "O-view.Linux.Tests");
        Assert.True(Directory.Exists(directory), $"no test project at {directory}, the scan below would read nothing");

        var files = Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !NotFlagged.Any(name => f.EndsWith(name, StringComparison.Ordinal)))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

        Assert.True(files.Length > 0, "scanned no files, has the project moved?");

        var offenders = files
            .Select(f => (Name: Path.GetRelativePath(root, f).Replace('\\', '/'), Text: File.ReadAllText(f)))
            .Where(f => TouchesTheRealDispatcher.IsMatch(f.Text) && !ClaimsItFirst.IsMatch(f.Text))
            .Select(f => f.Name)
            .ToArray();

        Assert.True(offenders.Length == 0,
            "these files touch Dispatcher.UIThread without claiming it for their own thread first "
            + "via AvaloniaDispatcherTestSupport.ClaimUiThreadForThisThread(), add the claim, or "
            + "this reintroduces the flake that method exists to prevent:"
            + $"{Environment.NewLine}  {string.Join($"{Environment.NewLine}  ", offenders)}");
    }

    /// <summary>
    /// Walks up from the test binary to the directory holding the solution file, the way
    /// <c>PortalThemeSourceTests</c> does.
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
