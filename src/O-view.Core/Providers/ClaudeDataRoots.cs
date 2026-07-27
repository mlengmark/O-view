namespace OView.Core.Providers;

/// <summary>
/// Every directory Claude Desktop might keep its data in.
///
/// Normally that is <c>%APPDATA%\Claude</c>, but Desktop ships as an MSIX package and
/// Windows redirects a packaged app's <c>%APPDATA%</c> writes into its own per-package
/// store at <c>%LOCALAPPDATA%\Packages\&lt;family&gt;\LocalCache\Roaming\Claude</c>.
/// Whether the canonical path is *also* populated varies by machine and Desktop build:
/// on one it holds the data, on another it does not exist at all. Assuming the canonical
/// path already cost this project one "no usage data" report against a machine where
/// Claude Desktop was open and working — see <see cref="PlanHistory.PlanHistoryLocator"/>,
/// which is where that lesson was first paid for.
///
/// Shared so the rule is written once. Consumers differ in what they do with the roots:
/// plan history wants the single best file, Cowork ingestion wants all of them.
/// </summary>
public static class ClaudeDataRoots
{
    /// <summary>The documented, unvirtualized location.</summary>
    public static string Canonical => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Claude");

    /// <summary>
    /// Per-package data roots, if any. The package family name carries a publisher hash,
    /// so packages are matched by name rather than assumed. Order is not meaningful here;
    /// callers that care (plan history prefers the newest file) impose their own.
    /// </summary>
    public static IReadOnlyList<string> Packaged(string localAppDataRoot)
    {
        var packages = Path.Combine(localAppDataRoot, "Packages");

        try
        {
            if (!Directory.Exists(packages))
            {
                return [];
            }

            return Directory.EnumerateDirectories(packages)
                .Where(d => Path.GetFileName(d).Contains("Claude", StringComparison.OrdinalIgnoreCase)
                         || Path.GetFileName(d).Contains("Anthropic", StringComparison.OrdinalIgnoreCase))
                .Select(d => Path.Combine(d, "LocalCache", "Roaming", "Claude"))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort: the canonical root is still usable.
            return [];
        }
    }

    /// <summary>
    /// Canonical root first, then any package stores. Includes the canonical path even
    /// when it does not exist, so diagnostics can name the location that was checked.
    /// </summary>
    public static IReadOnlyList<string> All() => All(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <summary>Overload taking an explicit LOCALAPPDATA so the layout can be tested.</summary>
    public static IReadOnlyList<string> All(string localAppDataRoot)
    {
        var roots = new List<string> { Canonical };
        roots.AddRange(Packaged(localAppDataRoot));
        return roots;
    }
}
