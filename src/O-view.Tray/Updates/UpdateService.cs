using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
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
    /// Downloads the installer to a fresh temp file, verifies it against the release's
    /// published <c>SHA256SUMS</c>, and returns its path. Throws on any failure — a bad
    /// download, a missing manifest, a digest that does not match — so the caller reports
    /// "couldn't update" rather than launching something unverified.
    ///
    /// <para><b>This fails closed, deliberately.</b> Falling back to "install it anyway"
    /// when the manifest is missing would mean an attacker who can replace the asset simply
    /// omits the manifest, and the check buys nothing. The cost is that a release which
    /// forgets to publish <c>SHA256SUMS</c> strands every user on their current version —
    /// the same failure mode as renaming a frozen asset, and the release workflow asserts
    /// against it for the same reason.</para>
    ///
    /// <para><b>What this does and does not establish.</b> The manifest ships from the same
    /// release as the asset, so it proves the bytes are the ones that release published — not
    /// that the release itself is honest. A compromised account can replace both. Provenance
    /// attestation is the control for that; this is the one that costs nothing.</para>
    ///
    /// <para>Sweeps previously downloaded installers out of the temp directory on the way in
    /// — see <see cref="InstallerDownloads"/> for why that has to happen here rather than
    /// after a successful install.</para>
    /// </summary>
    public async Task<string> DownloadInstallerAsync(AvailableUpdate update, CancellationToken cancellation = default)
    {
        // The URL decides where bytes come from and those bytes get executed, so it is
        // checked before it is used rather than trusted because the feed said it.
        if (!ReleaseDownloadUrl.IsTrusted(update.InstallerUrl))
        {
            throw new UpdateVerificationException(
                $"Refusing to download the installer from an unexpected location: {update.InstallerUrl}");
        }

        if (update.ChecksumsUrl is not { Length: > 0 } checksumsUrl
            || !ReleaseDownloadUrl.IsTrusted(checksumsUrl))
        {
            throw new UpdateVerificationException(
                $"Release {update.Tag} publishes no usable {ReleaseAssets.ChecksumsName}, so the "
                + "installer cannot be verified.");
        }

        var dir = InstallerDownloads.DefaultDirectory;
        Directory.CreateDirectory(dir);

        // Sweep before downloading, not after (issue #159). Nothing deletes the installer on
        // the success path and nothing can: LaunchInstaller hands the file to another process
        // and this one exits so its exe can be replaced. Since each download is named for its
        // version, every release used to land as a permanent new ~71 MB file. This is the one
        // moment the process is alive and knows the directory matters — and everything already
        // here is by definition an installer that has already run or already failed.
        InstallerDownloads.Prune(dir, _log);

        // Named from the PARSED version, never from update.Tag. The tag is a remote string,
        // and ReleaseVersion.TryParse truncates at the first '-' or '+' — so a tag of
        // "v9.9.9-../../../../Startup/evil" parses as 9.9.9 and would have carried its
        // traversal segments straight into this path, landing the download outside the temp
        // directory and then executing it.
        var target = Path.Combine(dir, $"O-view-Setup-{update.Version}.exe");

        _log?.Write($"downloading installer {update.Tag} from {update.InstallerUrl}");
        await DownloadToFileAsync(update.InstallerUrl, target, cancellation).ConfigureAwait(false);

        if (new FileInfo(target).Length == 0)
        {
            File.Delete(target);
            throw new IOException("Downloaded installer was empty.");
        }

        await VerifyAsync(target, checksumsUrl, cancellation).ConfigureAwait(false);

        _log?.Write($"installer verified at {target} ({new FileInfo(target).Length} bytes)");
        return target;
    }

    /// <summary>
    /// Compares the downloaded file against the digest the release recorded for
    /// <see cref="ReleaseAssets.WindowsInstallerName"/>. Deletes the file on any failure —
    /// leaving a rejected installer sitting in the temp directory is how it later gets run
    /// by hand.
    /// </summary>
    private async Task VerifyAsync(string path, string checksumsUrl, CancellationToken cancellation)
    {
        try
        {
            using var response = await Http.GetAsync(checksumsUrl, cancellation).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var manifest = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);

            var recorded = ChecksumFile.DigestFor(manifest, ReleaseAssets.WindowsInstallerName);
            if (recorded is null)
            {
                throw new UpdateVerificationException(
                    $"{ReleaseAssets.ChecksumsName} does not record a digest for "
                    + $"{ReleaseAssets.WindowsInstallerName}.");
            }

            string computed;
            await using (var file = File.OpenRead(path))
            {
                computed = Convert.ToHexString(
                    await SHA256.HashDataAsync(file, cancellation).ConfigureAwait(false));
            }

            if (!ChecksumFile.Matches(recorded, computed))
            {
                throw new UpdateVerificationException(
                    "The downloaded installer does not match the checksum published with this "
                    + "release. It has not been run.");
            }

            _log?.Write($"installer checksum verified against {ReleaseAssets.ChecksumsName}");
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    private static async Task DownloadToFileAsync(string url, string target, CancellationToken cancellation)
    {
        using var response = await Http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        await using var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(file, cancellation).ConfigureAwait(false);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort. The file is unverified, not dangerous on its own — nothing runs
            // it, and the next attempt overwrites it.
        }
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
