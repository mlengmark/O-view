using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using OView.Core.Updates;

namespace OView.App.Updates;

/// <summary>
/// Fetches <c>releases/latest</c> and compares it to the running build.
///
/// <para><b>Shared because the endpoint is one decision.</b> Both heads need to ask the same
/// URL with the same User-Agent and swallow the same failures; restating that in each head
/// is how the two quietly come to check different things. The comparison itself stays in
/// Core's <see cref="UpdateCheck"/> — this class is only the IO.</para>
///
/// <para><b>It returns a result and does nothing with it.</b> Downloading and executing are
/// separate steps that only the Windows-installer build may take, gated by
/// <see cref="UpdatePolicy.MayDownloadAndRun"/>. Fetching is safe on every platform; acting
/// on the answer is not.</para>
/// </summary>
public sealed class ReleaseFeed
{
    public const string LatestReleaseApi =
        "https://api.github.com/repos/mlengmark/O-view/releases/latest";

    // One client, a real User-Agent (the GitHub API rejects requests without one), and a
    // short timeout so a stalled network never hangs a menu action.
    private static readonly HttpClient Http = CreateClient();

    private readonly IAppLog? _log;
    private readonly Func<DateTimeOffset> _utcNow;

    /// <summary>
    /// When GitHub's rate limit lifts. Held here rather than in either head so both get the
    /// cooldown without either growing the logic — and because this class is the only one
    /// that ever sees the headers (GitHub issue #176).
    /// </summary>
    private DateTimeOffset? _rateLimitedUntilUtc;

    public ReleaseFeed(IAppLog? log = null, Func<DateTimeOffset>? utcNow = null)
    {
        _log = log;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// The running build's version, from the assembly version stamped at release time
    /// (<c>-p:Version</c> in the release workflow).
    ///
    /// <para>A local dev build carries no stamp and reports 0.0.0, which is older than any
    /// real release — so a dev build always sees an update. That is harmless: a dev build is
    /// neither an installer build nor a package, so nothing is permitted to act on it.</para>
    /// </summary>
    public static string VersionOf(Assembly assembly) =>
        assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";

    /// <summary>Web page for a release, for the manual-download paths.</summary>
    public static string ReleasePageUrl(AvailableUpdate update) =>
        $"https://github.com/mlengmark/O-view/releases/tag/{update.Tag}";

    /// <summary>
    /// Queries the feed and compares. Any network or HTTP failure becomes
    /// <see cref="UpdateOutcome.Unknown"/> — a failed update check must never take down a
    /// tray app that is otherwise working, and "I could not tell" is an honest answer where
    /// "up to date" would be a fabricated one (CLAUDE.md rule 6).
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(
        string currentVersion,
        ReleaseAssetSelector asset,
        CancellationToken cancellation = default)
    {
        // Still throttled from a previous answer: do not spend a request finding that out
        // again. The old code retried straight back into the limit on every check.
        if (_rateLimitedUntilUtc is { } until && _utcNow() < until)
        {
            _log?.Write($"update check skipped — rate limited until {until:u}");
            return UpdateCheckResult.RateLimited(until);
        }

        try
        {
            using var response = await Http.GetAsync(LatestReleaseApi, cancellation).ConfigureAwait(false);

            // Before EnsureSuccessStatusCode, which cannot tell a throttle from a dead
            // network once it has thrown. A 403 needs the headers to agree before it counts
            // as one — GitHub uses 403 for things a cooldown would not fix.
            if (RateLimitResponse.IsRateLimited(
                    (int)response.StatusCode,
                    Header(response, "x-ratelimit-remaining"),
                    Header(response, "x-ratelimit-reset"),
                    Header(response, "retry-after"),
                    _utcNow(),
                    out var retryAfterUtc))
            {
                _rateLimitedUntilUtc = retryAfterUtc;
                _log?.Write($"update check rate limited status={(int)response.StatusCode} " +
                            $"retry-after={(retryAfterUtc is { } r ? r.ToString("u") : "unknown")}");
                return UpdateCheckResult.RateLimited(retryAfterUtc);
            }

            response.EnsureSuccessStatusCode();
            _rateLimitedUntilUtc = null;
            var json = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);

            var result = UpdateCheck.Evaluate(currentVersion, json, asset);
            _log?.Write($"update check current={currentVersion} asset={asset.Description} " +
                        $"outcome={result.Outcome}" +
                        (result.Available is { } a ? $" latest={a.Tag}" : ""));
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            _log?.Write($"update check failed {ex.GetType().Name}: {ex.Message}");
            return UpdateCheckResult.Unknown;
        }
    }

    /// <summary>First value of a header, or null. Header names are case-insensitive here.</summary>
    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("O-view", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}
