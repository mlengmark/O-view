using System.Text.RegularExpressions;

namespace OView.App.Tests;

/// <summary>
/// Neither skin names anything the shell is made of (docs/architecture.md, ADR-0012).
///
/// <para><b>Why this is asserted over the source.</b> Both skins now ask through
/// <see cref="ISkinToShell"/> and are told through <see cref="IShellToSkin"/>, and nothing at
/// runtime would notice if one of them reached past those two for the engine again, it
/// compiles, the tests pass, and the layering is gone. The property worth keeping is about
/// what the source may <i>name</i>, so that is what is read.</para>
///
/// <para><b>Whole identifiers, not substrings.</b> Both skins legitimately compose the shell's
/// options and set the usage-cache refresher on them, which a substring scan for
/// <c>UsageEngine</c> or <c>UsageCacheRefresh</c> would fail on. Word boundaries pass those and
/// still catch the real thing, and they are also what makes this decidable without parsing:
/// a mention in a comment or an XML doc link is as much a breach of the seam as a call,
/// because it means the skin still knows the name.</para>
/// </summary>
public class SkinBoundaryTests
{
    /// <summary>
    /// What the shell is made of, and what a skin may therefore not name.
    ///
    /// <para>The seam itself is deliberately absent: <see cref="ShellHost"/>,
    /// <see cref="UsageEngineOptions"/>, the dispatcher and the timer factory are what a skin
    /// is <i>supposed</i> to hold, and banning them would ban the attach point along with the
    /// thing it hides.</para>
    /// </summary>
    private static readonly string[] ShellClasses =
    [
        "UsageEngine",
        "UsageCacheRefresh",
        "WeeklyResetCoordinator",
        "PlanHistoryPoll",
        "UsageAlerts",
        "PollRunner",
        "UsageProviders",
    ];

    private static readonly string[] Skins = ["O-view.Tray", "O-view.Linux"];

    /// <summary>
    /// Below this, something has moved and the scan is reading an almost empty directory,
    /// which would pass while asserting nothing. The smaller skin has fifteen files today.
    /// </summary>
    private const int FewestFilesASkinCanHave = 5;

    [Fact]
    public void NeitherSkinNamesAShellClass()
    {
        var root = RepositoryRoot();
        var banned = new Regex($@"\b({string.Join("|", ShellClasses)})\b", RegexOptions.Compiled);

        var scanned = new List<string>();
        var offenders = new List<string>();

        foreach (var skin in Skins)
        {
            var directory = Path.Combine(root, "src", skin);
            Assert.True(Directory.Exists(directory), $"no skin at {directory}, the scan below would read nothing");

            var files = SourceFiles(directory).ToArray();
            Assert.True(files.Length >= FewestFilesASkinCanHave,
                $"only {files.Length} source files under {directory}, so this test is no longer "
                + "reading the skin it names, has it moved?");

            foreach (var file in files)
            {
                var name = Path.GetRelativePath(root, file).Replace('\\', '/');
                scanned.Add(name);

                offenders.AddRange(File.ReadLines(file)
                    .Select((line, i) => (Number: i + 1, Text: line.Trim()))
                    .Where(line => banned.IsMatch(line.Text))
                    .Select(line => $"{name}:{line.Number}  {line.Text}"));
            }
        }

        // The scanned files are listed on failure as well as the offending lines: a scan that
        // has quietly stopped looking at the file in question is the one way this test can be
        // green and worthless, and the list is what tells the two apart.
        Assert.True(offenders.Count == 0,
            "a skin may only reach the shell through ISkinToShell and IShellToSkin, so it may "
            + $"name none of: {string.Join(", ", ShellClasses)}{Environment.NewLine}"
            + string.Join(Environment.NewLine, offenders)
            + $"{Environment.NewLine}{Environment.NewLine}Scanned {scanned.Count} files:"
            + $"{Environment.NewLine}  {string.Join($"{Environment.NewLine}  ", scanned)}");
    }

    /// <summary>Everything a skin is written in, minus what the build wrote.</summary>
    private static IEnumerable<string> SourceFiles(string directory) =>
        new[] { "*.cs", "*.xaml", "*.axaml" }
            .SelectMany(pattern => Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal);

    /// <summary>
    /// Walks up from the test binary to the directory holding the solution file, the way
    /// <c>PortalThemeSourceTests</c> does. The tests run from <c>bin/</c>, several levels below,
    /// and the depth differs by configuration, so the marker is searched for rather than
    /// counted to, and a walk that finds nothing fails here rather than scanning nothing.
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
