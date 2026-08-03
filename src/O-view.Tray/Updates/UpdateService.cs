using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
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
    // Used for the installer DOWNLOAD only; the release-feed query moved to the shared
    // ReleaseFeed so both heads cannot drift on the endpoint. Downloading stays here because
    // only this head is ever allowed to do it (ADR-0009).
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
    public static string CurrentVersion => ReleaseFeed.VersionOf(Assembly.GetExecutingAssembly());

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
    ///
    /// <para>Delegates to the shared <see cref="ReleaseFeed"/> so both heads ask the same URL
    /// with the same User-Agent. Restating the endpoint per head is how the two quietly come
    /// to check different things. Behaviour here is unchanged: same asset, same swallowed
    /// failures, same <see cref="UpdateOutcome.Unknown"/> on a dead network.</para>
    /// </summary>
    public Task<UpdateCheckResult> CheckAsync(CancellationToken cancellation = default) =>
        new ReleaseFeed(_log).CheckAsync(
            CurrentVersion,
            UpdatePolicy.DetectionAsset(CurrentInstallKind, RuntimeInformation.OSArchitecture),
            cancellation);

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
