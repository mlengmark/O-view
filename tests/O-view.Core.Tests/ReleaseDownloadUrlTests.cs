using OView.Core.Updates;

namespace OView.Core.Tests;

/// <summary>
/// The bug these prevent: the app fetching an executable from wherever the release feed
/// points and handing it to <c>Process.Start</c>. The asset <i>name</i> is matched by
/// <see cref="ReleaseAssets"/>; nothing constrained the <i>URL</i> until this existed.
/// </summary>
public class ReleaseDownloadUrlTests
{
    [Theory]
    [InlineData("https://github.com/mlengmark/O-view/releases/download/v0.7.0/O-view-Setup.exe")]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset/1/2")]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/1/2")]
    [InlineData("https://api.github.com/repos/mlengmark/O-view/releases/assets/1")]
    [InlineData("HTTPS://GITHUB.COM/mlengmark/O-view/releases/download/v0.7.0/O-view-Setup.exe")]
    public void GitHubReleaseUrlsAreTrusted(string url)
    {
        Assert.True(ReleaseDownloadUrl.IsTrusted(url));
    }

    [Theory]
    [InlineData("https://evil.example/O-view-Setup.exe")]
    [InlineData("http://github.com/mlengmark/O-view/releases/download/v0.7.0/O-view-Setup.exe")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("ftp://github.com/O-view-Setup.exe")]
    [InlineData("/releases/download/v0.7.0/O-view-Setup.exe")]
    [InlineData("")]
    [InlineData(null)]
    public void AnythingElseIsNot(string? url)
    {
        Assert.False(ReleaseDownloadUrl.IsTrusted(url));
    }

    [Theory]
    // Reads as github.com to a human; resolves to the attacker's host.
    [InlineData("https://github.com@evil.example/O-view-Setup.exe")]
    [InlineData("https://github.com:token@evil.example/O-view-Setup.exe")]
    // Suffix look-alikes, which a careless EndsWith check would accept.
    [InlineData("https://evil-github.com/O-view-Setup.exe")]
    [InlineData("https://github.com.evil.example/O-view-Setup.exe")]
    [InlineData("https://notgithubusercontent.com/O-view-Setup.exe")]
    public void HostLookalikesAreRefused(string url)
    {
        Assert.False(ReleaseDownloadUrl.IsTrusted(url));
    }
}
