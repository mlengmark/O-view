using OView.Core.Providers;
using OView.Core.Providers.PlanHistory;

namespace OView.Core.Tests;

/// <summary>
/// The Linux versions of the packaged-install trap that
/// <see cref="CoworkIngestionTests.PackagedDataRoot_IsDiscovered_AlongsideCanonical"/>
/// covers for MSIX: a sandboxed Claude Desktop writes its config somewhere other than the
/// canonical path, and a locator that only knows the canonical path finds nothing at all
/// while the app is open and working.
///
/// <para>Anthropic's own Linux build ships through apt and does <b>not</b> redirect, so on
/// a stock supported install these find nothing — they are here for unofficial Snap and
/// Flatpak repackagings.</para>
///
/// <para>Each layout is a pure function of a search root, so every case here runs on both
/// CI platforms rather than only the one it describes.</para>
/// </summary>
public class LinuxDataRootTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("oview-linuxroots-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_home, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        GC.SuppressFinalize(this);
    }

    private string Make(params string[] segments)
    {
        var path = Path.Combine([_home, .. segments]);
        Directory.CreateDirectory(path);
        return path;
    }

    // ── Snap ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SnapStoreIsDiscovered()
    {
        // A snap's $HOME is redirected to ~/snap/<snap>/<revision>, with `current` a
        // symlink to the live revision — so config lands under current/.config.
        Make("snap", "claude-desktop", "current", ".config", "Claude");

        var found = ClaudeDataRoots.SnapStores(_home);

        Assert.Single(found);
        Assert.Equal(
            Path.Combine(_home, "snap", "claude-desktop", "current", ".config", "Claude"),
            found[0]);
    }

    [Fact]
    public void UnrelatedSnapsAreIgnored()
    {
        Make("snap", "firefox", "current", ".config", "Claude");
        Make("snap", "spotify", "current", ".config", "Claude");

        Assert.Empty(ClaudeDataRoots.SnapStores(_home));
    }

    [Fact]
    public void SnapsNamedForTheVendorAreFoundToo()
    {
        // Snap and Flatpak ids are conventionally lowercase; MSIX family names are not.
        // Vendor matching must stay case-insensitive for both to work.
        Make("snap", "anthropic-claude", "current", ".config", "Claude");

        Assert.Single(ClaudeDataRoots.SnapStores(_home));
    }

    // ── Flatpak ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void FlatpakStoreIsDiscovered()
    {
        // Flatpak points $XDG_CONFIG_HOME at ~/.var/app/<app-id>/config.
        Make(".var", "app", "com.anthropic.Claude", "config", "Claude");

        var found = ClaudeDataRoots.FlatpakStores(_home);

        Assert.Single(found);
        Assert.Equal(
            Path.Combine(_home, ".var", "app", "com.anthropic.Claude", "config", "Claude"),
            found[0]);
    }

    [Fact]
    public void UnrelatedFlatpaksAreIgnored()
    {
        Make(".var", "app", "org.mozilla.firefox", "config", "Claude");

        Assert.Empty(ClaudeDataRoots.FlatpakStores(_home));
    }

    // ── absent trees ────────────────────────────────────────────────────────────────

    [Fact]
    public void NoSnapOrFlatpakTreeYieldsNothing()
    {
        // The normal case on a machine using the official apt package, which does not
        // redirect. Must be empty, not an error.
        Assert.Empty(ClaudeDataRoots.SnapStores(_home));
        Assert.Empty(ClaudeDataRoots.FlatpakStores(_home));
    }

    [Fact]
    public void AMissingHomeIsNotAnError()
    {
        var absent = Path.Combine(_home, "does-not-exist");

        Assert.Empty(ClaudeDataRoots.SnapStores(absent));
        Assert.Empty(ClaudeDataRoots.FlatpakStores(absent));
    }

    // ── composition ─────────────────────────────────────────────────────────────────

    [Fact]
    public void CanonicalComesFirstSoDiagnosticsCanNameIt()
    {
        var snap = Make("snap", "claude-desktop", "current", ".config", "Claude");

        var all = ClaudeDataRoots.All(ClaudeDataRoots.SnapStores(_home));

        Assert.Equal(ClaudeDataRoots.Canonical, all[0]);
        Assert.Contains(snap, all);
    }

    [Fact]
    public void BothSandboxKindsCanCoexist()
    {
        Make("snap", "claude-desktop", "current", ".config", "Claude");
        Make(".var", "app", "com.anthropic.Claude", "config", "Claude");

        IReadOnlyList<string> redirected =
            [.. ClaudeDataRoots.SnapStores(_home), .. ClaudeDataRoots.FlatpakStores(_home)];

        Assert.Equal(2, redirected.Count);
        Assert.Equal(3, ClaudeDataRoots.All(redirected).Count);
    }

    /// <summary>
    /// Plan history must consider the same redirected roots, or a sandboxed install shows
    /// "no usage data" while Claude Desktop is open — the exact failure
    /// <see cref="PlanHistoryLocator"/> was written after.
    /// </summary>
    [Fact]
    public void PlanHistoryLooksInsideASandboxedStore()
    {
        var store = Make("snap", "claude-desktop", "current", ".config", "Claude");
        var file = Path.Combine(store, PlanHistoryLocator.FileName);
        File.WriteAllText(file, "{}");

        var candidates = PlanHistoryLocator.Candidates(
            appDataRoot: Path.Combine(_home, ".config"),
            redirectedRoots: ClaudeDataRoots.SnapStores(_home));

        // Canonical is always first, even absent, so diagnostics can report what was tried.
        Assert.Equal(Path.Combine(_home, ".config", "Claude", PlanHistoryLocator.FileName), candidates[0]);
        Assert.Contains(file, candidates);
    }

    [Fact]
    public void PlanHistoryIgnoresASandboxRootWithNoFileInIt()
    {
        Make("snap", "claude-desktop", "current", ".config", "Claude");   // no file written

        var candidates = PlanHistoryLocator.Candidates(
            appDataRoot: Path.Combine(_home, ".config"),
            redirectedRoots: ClaudeDataRoots.SnapStores(_home));

        // Only the canonical path, which is listed unconditionally.
        Assert.Single(candidates);
    }
}
