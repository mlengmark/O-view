using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using OView.Core.Updates;
using OView.App;
using OView.App.Updates;

namespace OView.Tray.Updates;

/// <summary>
/// Fetches the latest GitHub release, compares it to the running build, and (when the app
/// was installed) downloads the installer and hands off to it for a self-replacing update
/// (ADR-0009). The version comparison and JSON shape live in <see cref="UpdateCheck"/> in
/// Core; this class is only the IO — HTTP, the temp download, and launching the installer —
/// so it stays out of the unit tests and Core stays free of network dependencies.
/// </summary>
public sealed class UpdateService
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/mlengmark/O-view/releases/latest";

    // A shared client with a real User-Agent (the GitHub API rejects requests without one)
    // and a short timeout so a stalled network never hangs a menu action.
    private static readonly HttpClient Http = CreateClient();

    private readonly IAppLog? _log;

    public UpdateService(IAppLog? log = null) => _log = log;

    /// <summary>
    /// The running build's version, from the assembly version stamped at release time
    /// (<c>-p:Version</c> in the release workflow). A local dev build has no version stamp
    /// and reports 0.0.0, which compares older than any real release — so a dev build will
    /// always see an "update available"; that is harmless (it is not an installed build, so
    /// the update path opens the releases page rather than replacing anything).
    /// </summary>
    public static string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : "0.0.0";

    /// <summary>
    /// Whether this process is running from the per-user install location
    /// (<c>%LOCALAPPDATA%\Programs\O-view</c>, ADR-0008). Only an installed build can be
    /// replaced in place by re-running the installer; a portable exe is sent to the
    /// releases page instead — the installer would create a parallel install rather than
    /// update the loose exe, and a running single-file exe cannot overwrite itself.
    /// </summary>
    public static bool IsInstalled => CurrentInstallKind is InstallKind.WindowsInstaller;

    /// <summary>
    /// How this build arrived, which <see cref="UpdatePolicy"/> turns into what it may do.
    ///
    /// <para>Only the two Windows kinds are reachable from here — this class is the WPF
    /// head's, and Linux never reaches it (ADR-0009 as amended: an apt build defers to the
    /// package manager and downloads nothing). The Linux head supplies its own kind.</para>
    ///
    /// <para>The comparison is case-insensitive because this path is Windows-only and
    /// Windows filesystems are; it is deliberately not the general path-identity question
    /// that #71 is about.</para>
    /// </summary>
    public static InstallKind CurrentInstallKind
    {
        get
        {
            if (Environment.ProcessPath is not { Length: > 0 } exe)
            {
                return InstallKind.WindowsPortable;
            }

            var installRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "O-view");
            var exeDir = Path.GetDirectoryName(Path.GetFullPath(exe));

            return exeDir is not null
                && string.Equals(
                    Path.TrimEndingDirectorySeparator(exeDir),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot)),
                    StringComparison.OrdinalIgnoreCase)
                ? InstallKind.WindowsInstaller
                : InstallKind.WindowsPortable;
        }
    }

    /// <summary>Web page for the release, for the portable / manual-download path.</summary>
    public static string ReleasePageUrl(AvailableUpdate update) =>
        $"https://github.com/mlengmark/O-view/releases/tag/{update.Tag}";

    /// <summary>
    /// Queries the release feed and returns the comparison result. Any network or HTTP
    /// failure is swallowed to <see cref="UpdateOutcome.Unknown"/> — a failed update check
    /// must never crash a tray app that is otherwise working.
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellation = default)
    {
        try
        {
            using var response = await Http.GetAsync(LatestReleaseApi, cancellation).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
            var result = UpdateCheck.Evaluate(CurrentVersion, json, ReleaseAssets.WindowsInstaller);
            _log?.Write($"update check current={CurrentVersion} outcome={result.Outcome}" +
                        (result.Available is { } a ? $" latest={a.Tag}" : ""));
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            _log?.Write($"update check failed {ex.GetType().Name}: {ex.Message}");
            return UpdateCheckResult.Unknown;
        }
    }

    /// <summary>
    /// Downloads the installer to a fresh temp file and returns its path. Throws on any
    /// failure so the caller can report "couldn't download" rather than launch a truncated
    /// or missing file.
    /// </summary>
    public async Task<string> DownloadInstallerAsync(AvailableUpdate update, CancellationToken cancellation = default)
    {
        var dir = Path.Combine(Path.GetTempPath(), "O-view-update");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, $"O-view-Setup-{update.Tag}.exe");

        _log?.Write($"downloading installer {update.Tag} from {update.InstallerUrl}");
        using (var response = await Http.GetAsync(update.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancellation)
                   .ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
            await using var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(file, cancellation).ConfigureAwait(false);
        }

        if (new FileInfo(target).Length == 0)
        {
            File.Delete(target);
            throw new IOException("Downloaded installer was empty.");
        }

        _log?.Write($"installer downloaded to {target} ({new FileInfo(target).Length} bytes)");
        return target;
    }

    /// <summary>
    /// Launches the downloaded installer to upgrade in place and relaunch O-view, as an
    /// independent process so it survives this app exiting. <c>/SILENT</c> shows only a
    /// progress bar (no wizard pages); <c>/update=1</c> tells the installer to relaunch the
    /// app when it finishes (see installer/O-view.iss). The caller must shut the app down
    /// immediately after so it does not hold the exe locked while the installer replaces it.
    /// </summary>
    public void LaunchInstaller(string installerPath)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = "/SILENT /update=1",
            UseShellExecute = true,
        };
        System.Diagnostics.Process.Start(psi);
        _log?.Write("installer launched; app will exit for in-place update");
    }

    /// <summary>Opens a URL in the user's default browser (portable-update / manual path).</summary>
    public void OpenInBrowser(string url) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true,
        });

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("O-view", CurrentVersion));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}
