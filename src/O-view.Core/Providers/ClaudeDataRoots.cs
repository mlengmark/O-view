namespace OView.Core.Providers;

/// <summary>
/// Every directory Claude Desktop might keep its data in.
///
/// <para>Normally that is the canonical per-user config location — <c>%APPDATA%\Claude</c>
/// on Windows, <c>~/.config/Claude</c> on Linux. But a sandboxed package does not write
/// there: the platform redirects it into a per-package store, and on a machine where only
/// that store is populated, a canonical-path-only locator finds nothing at all. Assuming
/// the canonical path already cost this project one "no usage data" report against a
/// machine where Claude Desktop was open and working — see
/// <see cref="PlanHistory.PlanHistoryLocator"/>, which is where that lesson was first paid
/// for.</para>
///
/// <para>Every platform has some version of this, under a different name:</para>
///
/// <list type="table">
///   <item><term>Windows MSIX</term><description><c>%LOCALAPPDATA%\Packages\&lt;family&gt;\LocalCache\Roaming\Claude</c></description></item>
///   <item><term>Linux Snap</term><description><c>~/snap/&lt;snap&gt;/current/.config/Claude</c></description></item>
///   <item><term>Linux Flatpak</term><description><c>~/.var/app/&lt;app-id&gt;/config/Claude</c></description></item>
/// </list>
///
/// <para>Each layout is a <b>pure function of a search root</b>, so both platforms' rules
/// are exercised by tests on either CI runner. <see cref="Redirected()"/> is the only thing
/// that asks what OS this is.</para>
///
/// <para>Shared so the rule is written once. Consumers differ in what they do with the
/// roots: plan history wants the single best file, Cowork ingestion wants all of them.</para>
/// </summary>
public static class ClaudeDataRoots
{
    /// <summary>The documented, unvirtualized location.</summary>
    public static string Canonical => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Claude");

    /// <summary>
    /// Windows MSIX package stores, if any. The package family name carries a publisher
    /// hash, so packages are matched by name rather than assumed. Order is not meaningful
    /// here; callers that care (plan history prefers the newest file) impose their own.
    /// </summary>
    /// <param name="localAppDataRoot">Stands in for <c>%LOCALAPPDATA%</c>, so the layout is testable.</param>
    public static IReadOnlyList<string> Packaged(string localAppDataRoot) =>
        MatchingVendorDirectories(Path.Combine(localAppDataRoot, "Packages"))
            .Select(d => Path.Combine(d, "LocalCache", "Roaming", "Claude"))
            .ToList();

    /// <summary>
    /// Snap sandbox stores. A snap's <c>$HOME</c> is redirected to
    /// <c>~/snap/&lt;snap&gt;/&lt;revision&gt;</c>, with <c>current</c> a symlink to the live one — so
    /// config lands at <c>~/snap/&lt;snap&gt;/current/.config/Claude</c>.
    /// </summary>
    /// <param name="homeRoot">Stands in for <c>$HOME</c>, so the layout is testable.</param>
    public static IReadOnlyList<string> SnapStores(string homeRoot) =>
        MatchingVendorDirectories(Path.Combine(homeRoot, "snap"))
            .Select(d => Path.Combine(d, "current", ".config", "Claude"))
            .ToList();

    /// <summary>
    /// Flatpak sandbox stores. Flatpak points <c>$XDG_CONFIG_HOME</c> at
    /// <c>~/.var/app/&lt;app-id&gt;/config</c>, so Claude's directory sits inside that.
    /// </summary>
    /// <param name="homeRoot">Stands in for <c>$HOME</c>, so the layout is testable.</param>
    public static IReadOnlyList<string> FlatpakStores(string homeRoot) =>
        MatchingVendorDirectories(Path.Combine(homeRoot, ".var", "app"))
            .Select(d => Path.Combine(d, "config", "Claude"))
            .ToList();

    /// <summary>
    /// The redirected roots for the platform this is running on — <b>the only place here
    /// that asks what OS it is</b> (ADR-0012).
    ///
    /// <para>Anthropic ships Claude Desktop for Linux through an apt repository, which does
    /// <i>not</i> redirect, so on a stock supported install this finds nothing and the
    /// canonical path is the real one. It is here for unofficial Snap and Flatpak
    /// repackagings, which exist and are widely used — and because this project has already
    /// paid once for assuming the canonical path was the only one.</para>
    /// </summary>
    public static IReadOnlyList<string> Redirected() =>
        OperatingSystem.IsWindows()
            ? Packaged(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
            : [
                .. SnapStores(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
                .. FlatpakStores(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
              ];

    /// <summary>
    /// Canonical root first, then any redirected stores. Includes the canonical path even
    /// when it does not exist, so diagnostics can name the location that was checked.
    /// </summary>
    public static IReadOnlyList<string> All() => [Canonical, .. Redirected()];

    /// <summary>
    /// Overload taking explicit redirected roots so a whole layout can be tested without
    /// depending on the running platform.
    /// </summary>
    public static IReadOnlyList<string> All(IReadOnlyList<string> redirectedRoots) =>
        [Canonical, .. redirectedRoots];

    /// <summary>
    /// Windows-shaped overload, kept because the MSIX case is the one with the longest
    /// history here and its tests name it directly.
    /// </summary>
    public static IReadOnlyList<string> All(string localAppDataRoot) =>
        All(Packaged(localAppDataRoot));

    /// <summary>
    /// Directories under <paramref name="searchRoot"/> whose name mentions the vendor.
    ///
    /// <para>Matching is deliberately case-<b>insensitive</b> and stays that way: this is
    /// looking for a vendor name inside a package identifier, not comparing two paths for
    /// identity (which is <see cref="PathIdentity"/>'s job). MSIX family names are
    /// capitalised, Snap and Flatpak ids are conventionally lowercase
    /// (<c>com.anthropic.claude</c>), and both must be found.</para>
    ///
    /// <para>Best-effort: the canonical root is still usable if this cannot be read.</para>
    /// </summary>
    private static IEnumerable<string> MatchingVendorDirectories(string searchRoot)
    {
        try
        {
            if (!Directory.Exists(searchRoot))
            {
                return [];
            }

            return Directory.EnumerateDirectories(searchRoot)
                .Where(d => Path.GetFileName(d).Contains("Claude", StringComparison.OrdinalIgnoreCase)
                         || Path.GetFileName(d).Contains("Anthropic", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
