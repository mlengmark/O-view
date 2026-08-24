namespace OView.Core.Providers.PlanHistory;

/// <summary>
/// Finds Claude Desktop's usage file. It normally sits at
/// <c>%APPDATA%\Claude\plan-usage-history.json</c>, but Claude Desktop ships as an MSIX
/// package, and Windows redirects a packaged app's <c>%APPDATA%</c> writes into its own
/// per-package store at
/// <c>%LOCALAPPDATA%\Packages\&lt;family&gt;\LocalCache\Roaming\</c>.
///
/// Whether the canonical path is also populated depends on how the package declares its
/// resources, so it varies by machine and Desktop build: on one machine both locations
/// hold the data; on another the canonical path does not exist at all and O-view reported
/// "no usage data" while Claude Desktop was open and working. Checking only the canonical
/// path is therefore an assumption about someone else's packaging, which is exactly the
/// kind of thing that differs across users' machines.
///
/// <b>Order is by freshness, across every location — the canonical path included.</b> It used
/// to be tried first on the grounds that it is the documented one, with only the package
/// stores sorted by age. That is the same "first location that exists wins" rule that read a
/// migration stub instead of the account file and an abandoned cache instead of the live one
/// (2026-08-24): a machine carrying both an unpackaged leftover and a live MSIX install would
/// read whichever the packaging convention favours rather than whichever Claude Desktop is
/// actually writing. Both locations are read, every time, and the freshest answers.
///
/// Locations that do not exist keep their place at the end so diagnostics can still report
/// what was searched — a path that was checked and found missing is evidence too.
/// </summary>
public static class PlanHistoryLocator
{
    public const string FileName = "plan-usage-history.json";

    /// <summary>The documented location, used when nothing else is found.</summary>
    public static string CanonicalPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Claude",
        FileName);

    /// <summary>
    /// Every location worth checking, in priority order. Always non-empty — the canonical
    /// path is the first entry even when it does not exist, so diagnostics can report it.
    /// </summary>
    public static IReadOnlyList<string> Candidates() => Candidates(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        ClaudeDataRoots.Redirected());

    /// <summary>
    /// Windows-shaped overload, kept because the MSIX case is the one with the longest
    /// history here and its tests name it directly.
    /// </summary>
    public static IReadOnlyList<string> Candidates(string appDataRoot, string localAppDataRoot) =>
        Candidates(appDataRoot, ClaudeDataRoots.Packaged(localAppDataRoot));

    /// <summary>
    /// Builds candidates from explicit roots. Public so the layout rules can be tested
    /// against a synthetic profile — the sandboxed-app layout is the whole reason this
    /// class exists, and it cannot be exercised against the real machine.
    /// </summary>
    public static IReadOnlyList<string> Candidates(string appDataRoot, IReadOnlyList<string> redirectedRoots)
    {
        var canonical = Path.Combine(appDataRoot, "Claude", FileName);

        // A sandboxed Claude Desktop redirects its config away from the canonical path —
        // MSIX into a LocalCache on Windows, Snap or Flatpak into a per-app tree on Linux.
        // Root discovery is shared with Cowork ingestion (ClaudeDataRoots).
        //
        // Distinct because a root can legitimately resolve to the canonical path on some
        // installs, and the same file listed twice would look like corroboration.
        var existing = new[] { canonical }
            .Concat(redirectedRoots.Select(root => Path.Combine(root, FileName)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(File.Exists)
            .OrderByDescending(LastWriteUtcOrMin)
            .ToList();

        // The canonical path is named even when it does not exist, so diagnostics can report
        // what was tried. A location that was checked and found missing is evidence too.
        return existing.Contains(canonical, StringComparer.OrdinalIgnoreCase)
            ? existing
            : [canonical, .. existing];
    }

    /// <summary>The first candidate that exists, or null when none do.</summary>
    public static string? Locate() => Candidates().FirstOrDefault(File.Exists);

    private static DateTime LastWriteUtcOrMin(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }
}
