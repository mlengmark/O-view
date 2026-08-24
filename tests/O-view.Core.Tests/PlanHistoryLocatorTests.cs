using OView.Core.Providers.PlanHistory;

namespace OView.Core.Tests;

public class PlanHistoryLocatorTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("oview-locator-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string AppData => Path.Combine(_root, "Roaming");
    private string LocalAppData => Path.Combine(_root, "Local");

    private string WriteCanonical()
    {
        var path = Path.Combine(AppData, "Claude", PlanHistoryLocator.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{}");
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

    [Fact]
    public void Canonical_wins_when_both_exist()
    {
        var canonical = WriteCanonical();
        WritePackaged("Claude_pzs8sxrjxfjjc");

        var candidates = PlanHistoryLocator.Candidates(AppData, LocalAppData);

        Assert.Equal(canonical, candidates[0]);
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
