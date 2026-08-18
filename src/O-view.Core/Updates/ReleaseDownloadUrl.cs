namespace OView.Core.Updates;

/// <summary>
/// Whether a URL from the release feed is one this app will fetch from.
///
/// <para><b>Why the feed's own answer is not enough.</b> <c>browser_download_url</c> arrives
/// inside the JSON, and the app acts on it by downloading bytes and — on Windows — executing
/// them. Nothing in <see cref="UpdateCheck"/> constrains where it points: a release whose
/// asset is named <c>O-view-Setup.exe</c> but whose URL is an attacker's host would be
/// fetched and run. That makes the URL, not just the file, a trust decision, and it is one
/// worth making explicitly rather than inheriting from whatever the feed says.</para>
///
/// <para>This does not replace TLS or the checksum; it is the cheap outer check that keeps
/// the other two pointed at GitHub. A compromised release can still publish a bad asset on a
/// legitimate host — that is what provenance attestation is for, and it is not this.</para>
/// </summary>
public static class ReleaseDownloadUrl
{
    /// <summary>
    /// Hosts GitHub serves release assets from. <c>github.com</c> issues the redirect;
    /// the object stores are where it lands.
    /// </summary>
    private static readonly string[] AllowedHosts =
    [
        "github.com",
        "api.github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
    ];

    /// <summary>
    /// True only for an absolute <c>https</c> URL on a known GitHub host. Anything else —
    /// a different scheme, a bare path, a look-alike host, a userinfo trick — is false.
    /// </summary>
    public static bool IsTrusted(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            return false;
        }

        // A populated userinfo section is how "https://github.com@evil.example/x" reads as
        // GitHub to a human and as evil.example to a fetcher. Uri.Host already resolves
        // that correctly, so this is belt and braces — and it costs one comparison.
        if (uri.UserInfo.Length > 0)
        {
            return false;
        }

        // Host comparison is case-insensitive because DNS is, and invariant because a host
        // is not culture-sensitive text. Exact match, never EndsWith: "notgithub.com" ends
        // with neither, but "evil-github.com" would pass a careless suffix test.
        foreach (var host in AllowedHosts)
        {
            if (string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
