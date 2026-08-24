using OView.Core.Providers.PlanHistory;

namespace OView.Core.Tests;

public class PlanHistoryLocatorTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("oview-locator-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string AppData => Path.Combine(_root, "Roaming");
    private string LocalAppData => Path.Combine(_root, "Local");

    private string WriteCanonical(DateTime? lastWriteUtc = null)
    {
        var path = Path.Combine(AppData, "Claude", PlanHistoryLocator.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{}");
        if (lastWriteUtc is { } t)
        {
            File.SetLastWriteTimeUtc(path, t);
        }

        return path;
    }

    private string WritePackaged(string packageFamily, DateTime? lastWriteUtc = null)
    {
        var path = Path.Combine(LocalAppData, "Packages", packageFamily,
            "LocalCache", "Roaming", "Claude", PlanHistoryLocator.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{}");
        if (lastWriteUtc is { } t)
        {
            File.SetLastWriteTimeUtc(path, t);
        }
        return path;
    }

    [Fact]
    public void Canonical_path_is_always_first_even_when_absent()
    {
        var candidates = PlanHistoryLocator.Candidates(AppData, LocalAppData);

        Assert.NotEmpty(candidates);
        Assert.Equal(Path.Combine(AppData, "Claude", PlanHistoryLocator.FileName), candidates[0]);
    }

    [Fact]
    public void Packaged_location_is_found_when_canonical_is_absent()
    {
        // The reported case: Claude Desktop installed as an MSIX package, so its %APPDATA%
        // writes land in the package store and the canonical path never appears.
        var packaged = WritePackaged("Claude_pzs8sxrjxfjjc");

        var candidates = PlanHistoryLocator.Candidates(AppData, LocalAppData);

        Assert.Contains(packaged, candidates);
    }

    /// <summary>
    /// The freshest file wins, wherever it lives — the canonical path has no standing of its
    /// own. It used to be tried first because it is the documented location, which is the same
    /// "first location that exists wins" rule that read a migration stub instead of the account
    /// file and an abandoned cache instead of the live one (2026-08-24). A machine can carry an
    /// unpackaged leftover beside a live MSIX install, and only one of them is being written.
    /// </summary>
    [Fact]
    public void The_freshest_file_wins_wherever_it_lives()
    {
        var canonical = WriteCanonical(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var packaged = WritePackaged("Claude_pzs8sxrjxfjjc", new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));

        // Candidates[0] is what Locate() returns for these roots: the first that exists.
        Assert.Equal(packaged, PlanHistoryLocator.Candidates(AppData, LocalAppData)[0]);

        // And the other way round, so this pins freshness rather than a new fixed preference.
        File.SetLastWriteTimeUtc(canonical, new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(canonical, PlanHistoryLocator.Candidates(AppData, LocalAppData)[0]);
    }

    /// <summary>Both locations stay in the list — reading one is never a substitute for the other.</summary>
    [Fact]
    public void Both_locations_are_always_candidates()
    {
        var canonical = WriteCanonical();
        var packaged = WritePackaged("Claude_pzs8sxrjxfjjc");

        var candidates = PlanHistoryLocator.Candidates(AppData, LocalAppData);

        Assert.Contains(canonical, candidates);
        Assert.Contains(packaged, candidates);
    }

    [Fact]
    public void Multiple_packages_are_ordered_newest_first()
    {
        var older = WritePackaged("Claude_oldpkg", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var newer = WritePackaged("Claude_newpkg", new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));

        var packaged = PlanHistoryLocator.Candidates(AppData, LocalAppData).Skip(1).ToList();

        Assert.Equal(newer, packaged[0]);
        Assert.Equal(older, packaged[1]);
    }

    [Fact]
    public void Unrelated_packages_are_ignored()
    {
        var unrelated = Path.Combine(LocalAppData, "Packages", "Microsoft.SomethingElse",
            "LocalCache", "Roaming", "Claude", PlanHistoryLocator.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(unrelated)!);
        File.WriteAllText(unrelated, "{}");

        var candidates = PlanHistoryLocator.Candidates(AppData, LocalAppData);

        Assert.DoesNotContain(unrelated, candidates);
    }

    [Fact]
    public void Missing_packages_root_is_not_fatal()
    {
        // No %LOCALAPPDATA%\Packages at all — still returns the canonical candidate.
        var candidates = PlanHistoryLocator.Candidates(AppData, Path.Combine(_root, "no-such-local"));

        Assert.Single(candidates);
    }
}
