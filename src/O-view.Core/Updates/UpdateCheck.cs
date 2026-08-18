using System.Text.Json;

namespace OView.Core.Updates;

/// <summary>What a check against the release feed concluded.</summary>
public enum UpdateOutcome
{
    /// <summary>The running version is the latest published release.</summary>
    UpToDate,

    /// <summary>A newer release exists; <see cref="UpdateCheckResult.Available"/> is populated.</summary>
    UpdateAvailable,

    /// <summary>The feed could not be understood — no usable tag, or malformed JSON.</summary>
    Unknown,
}

/// <summary>A newer release and the installer asset to fetch it from.</summary>
/// <param name="ChecksumsUrl">
/// Where the release's <c>SHA256SUMS</c> lives, or null when the release does not publish
/// one. Null is not "skip the check" — a build that verifies treats it as "cannot verify,
/// therefore do not install", and says so. It is nullable because releases cut before
/// checksums existed genuinely have none, and because detection must keep working for the
/// platforms that download nothing at all.
/// </param>
public sealed record AvailableUpdate(
    ReleaseVersion Version,
    string Tag,
    string InstallerUrl,
    string? ChecksumsUrl = null);

/// <summary>The outcome of comparing the current build against the latest release.</summary>
public sealed record UpdateCheckResult(UpdateOutcome Outcome, AvailableUpdate? Available = null)
{
    public static readonly UpdateCheckResult UpToDate = new(UpdateOutcome.UpToDate);
    public static readonly UpdateCheckResult Unknown = new(UpdateOutcome.Unknown);
}

/// <summary>
/// Pure logic for the auto-updater (ADR-0009): given the running version and the JSON body of
/// GitHub's <c>releases/latest</c> endpoint, decide whether a newer release exists and which
/// asset installs it. Kept in Core with no HTTP so the version comparison and the JSON shape
/// are unit-testable without a network; the Tray's <c>UpdateService</c> supplies the bytes.
/// </summary>
public static class UpdateCheck
{
    /// <summary>
    /// Evaluates the latest-release JSON against <paramref name="currentVersion"/>. A newer tag
    /// only becomes <see cref="UpdateOutcome.UpdateAvailable"/> when an asset <paramref name="asset"/>
    /// recognises is also present with a download URL — otherwise there is nothing to update *to*,
    /// so it reports <see cref="UpdateOutcome.Unknown"/> rather than dangling an offer it cannot
    /// fulfil. A draft or prerelease is treated as no update. Never throws on malformed input.
    ///
    /// <para><paramref name="asset"/> is supplied by the caller rather than chosen here, so this
    /// stays a pure function with no knowledge of the running platform. That is what stops a
    /// Linux build finding <c>O-view-Setup.exe</c> in a release carrying both platforms and
    /// handing a Windows installer to <c>Process.Start</c>.</para>
    /// </summary>
    public static UpdateCheckResult Evaluate(
        string currentVersion, string releaseJson, ReleaseAssetSelector asset)
    {
        if (!ReleaseVersion.TryParse(currentVersion, out var current))
        {
            // If we cannot establish what we are running, we cannot claim an update is newer.
            return UpdateCheckResult.Unknown;
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(releaseJson);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return UpdateCheckResult.Unknown;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return UpdateCheckResult.Unknown;
        }

        if (GetBool(root, "draft") || GetBool(root, "prerelease"))
        {
            return UpdateCheckResult.Unknown;
        }

        var tag = GetString(root, "tag_name");
        if (tag is null || !ReleaseVersion.TryParse(tag, out var latest))
        {
            return UpdateCheckResult.Unknown;
        }

        if (latest <= current)
        {
            return UpdateCheckResult.UpToDate;
        }

        var installerUrl = FindInstallerUrl(root, asset);
        if (installerUrl is null)
        {
            return UpdateCheckResult.Unknown;
        }

        // Absent rather than fatal: a release published before checksums existed has none,
        // and this method is also what the Linux heads use to *detect* an update they will
        // never download. Whether a null here blocks an install is the downloading head's
        // decision, not this one's.
        var checksumsUrl = FindAssetUrl(
            root, name => string.Equals(name, ReleaseAssets.ChecksumsName, StringComparison.Ordinal));

        return new UpdateCheckResult(
            UpdateOutcome.UpdateAvailable,
            new AvailableUpdate(latest, tag, installerUrl, checksumsUrl));
    }

    private static string? FindInstallerUrl(JsonElement root, ReleaseAssetSelector selector) =>
        FindAssetUrl(root, selector.Matches);

    private static string? FindAssetUrl(JsonElement root, Func<string, bool> matches)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            if (GetString(asset, "name") is { } name
                && matches(name)
                && GetString(asset, "browser_download_url") is { Length: > 0 } url)
            {
                return url;
            }
        }
        return null;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
