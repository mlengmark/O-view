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
/// Order matters: the canonical path is tried first (it is the documented location and the
/// one Desktop uses when unvirtualized), then package stores, most-recently-written first
/// so a stale leftover never shadows the live file.
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
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <summary>
    /// Builds candidates from explicit roots. Public so the layout rules can be tested
    /// against a synthetic profile — the packaged-app layout is the whole reason this
    /// class exists, and it cannot be exercised against the real machine.
    /// </summary>
    public static IReadOnlyList<string> Candidates(string appDataRoot, string localAppDataRoot)
    {
        var paths = new List<string> { Path.Combine(appDataRoot, "Claude", FileName) };

        // Packaged (MSIX) Claude Desktop redirects %APPDATA% into its own LocalCache.
        // Root discovery is shared with Cowork ingestion (ClaudeDataRoots); the ordering
        // below is this caller's own — newest file first, because a machine can carry an
        // old package's leftovers and a stale file must never shadow the live one.
        paths.AddRange(ClaudeDataRoots.Packaged(localAppDataRoot)
            .Select(root => Path.Combine(root, FileName))
            .Where(File.Exists)
            .OrderByDescending(LastWriteUtcOrMin));

        return paths;
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
