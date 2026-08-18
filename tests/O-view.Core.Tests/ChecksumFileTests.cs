using OView.Core.Updates;

namespace OView.Core.Tests;

/// <summary>
/// The bug these prevent: an updater that believes a malformed or ambiguous
/// <c>SHA256SUMS</c> and installs on the strength of it.
///
/// <para>Every case here asserts the same property — anything short of an unambiguous,
/// well-formed entry for the exact asset yields null, and null means "do not install".
/// A lenient parser would turn each of these into permission.</para>
/// </summary>
public class ChecksumFileTests
{
    private const string InstallerDigest =
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private const string OtherDigest =
        "5891b5b522d5df086d0ff0b110fbd9d21bb4fc7163af34d08286a2e846f6be03";

    private static string Manifest(params string[] lines) => string.Join("\n", lines) + "\n";

    // ── the shapes sha256sum actually writes ────────────────────────────────────────

    [Fact]
    public void ReadsTheDigestForTheNamedAsset()
    {
        var text = Manifest(
            $"{OtherDigest}  O-view.Tray.exe",
            $"{InstallerDigest}  O-view-Setup.exe",
            $"{OtherDigest}  o-view_0.7.0_amd64.deb");

        Assert.Equal(InstallerDigest, ChecksumFile.DigestFor(text, "O-view-Setup.exe"));
    }

    [Theory]
    [InlineData("  ")]           // text mode, what sha256sum writes by default
    [InlineData(" *")]           // binary mode
    public void AcceptsBothSeparatorForms(string separator)
    {
        var text = Manifest($"{InstallerDigest}{separator}O-view-Setup.exe");

        Assert.Equal(InstallerDigest, ChecksumFile.DigestFor(text, "O-view-Setup.exe"));
    }

    [Fact]
    public void ToleratesCarriageReturnsAndBlankLines()
    {
        var text = $"\r\n{InstallerDigest}  O-view-Setup.exe\r\n\r\n";

        Assert.Equal(InstallerDigest, ChecksumFile.DigestFor(text, "O-view-Setup.exe"));
    }

    // ── everything that must NOT produce a digest ───────────────────────────────────

    [Fact]
    public void AnAssetTheManifestDoesNotNameHasNoDigest()
    {
        var text = Manifest($"{OtherDigest}  O-view.Tray.exe");

        Assert.Null(ChecksumFile.DigestFor(text, "O-view-Setup.exe"));
    }

    [Fact]
    public void NamedTwiceIsRefusedRatherThanResolved()
    {
        // Which line wins is not a guess worth making: one of them is wrong, and picking
        // either could be picking the attacker's.
        var text = Manifest(
            $"{InstallerDigest}  O-view-Setup.exe",
            $"{OtherDigest}  O-view-Setup.exe");

        Assert.Null(ChecksumFile.DigestFor(text, "O-view-Setup.exe"));
    }

    [Theory]
    [InlineData("abc123  O-view-Setup.exe")]                       // too short
    [InlineData("O-view-Setup.exe")]                               // no digest at all
    [InlineData("zzz0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855  O-view-Setup.exe")]
    public void MalformedLinesAreIgnored(string line)
    {
        Assert.Null(ChecksumFile.DigestFor(Manifest(line), "O-view-Setup.exe"));
    }

    [Fact]
    public void ADigestRecordedAgainstAPathIsNotAMatchForTheBareName()
    {
        // "dist/O-view-Setup.exe" describes a file somewhere else. Accepting it would let
        // the manifest speak about something other than the asset that was downloaded.
        var text = Manifest($"{InstallerDigest}  dist/O-view-Setup.exe");

        Assert.Null(ChecksumFile.DigestFor(text, "O-view-Setup.exe"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyInputHasNoDigest(string? text)
    {
        Assert.Null(ChecksumFile.DigestFor(text, "O-view-Setup.exe"));
    }

    // ── comparison ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ComparisonIgnoresHexCasing()
    {
        // sha256sum writes lowercase; Convert.ToHexString writes uppercase. A case-sensitive
        // comparison would reject every genuine update.
        Assert.True(ChecksumFile.Matches(InstallerDigest, InstallerDigest.ToUpperInvariant()));
    }

    [Fact]
    public void DifferentDigestsDoNotMatch()
    {
        Assert.False(ChecksumFile.Matches(InstallerDigest, OtherDigest));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(null, "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", null)]
    [InlineData("", "")]
    [InlineData("abc", "abc")]
    public void MissingOrShortDigestsNeverMatch(string? recorded, string? computed)
    {
        // Two nulls matching would mean "we know nothing about either side, so install it".
        Assert.False(ChecksumFile.Matches(recorded, computed));
    }
}
