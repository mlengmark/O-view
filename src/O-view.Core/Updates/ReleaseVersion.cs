using System.Globalization;

namespace OView.Core.Updates;

/// <summary>
/// A three-part release version (<c>major.minor.patch</c>) parsed from a Git tag or an
/// assembly version. Release tags are <c>v0.4.2</c>; an assembly version is <c>0.4.2.0</c>.
/// Both normalise to the same three components — a trailing revision (the assembly's 4th
/// part) is ignored, and a leading <c>v</c> and any pre-release suffix (<c>-beta.1</c>,
/// <c>+build</c>) are stripped so that a plain numeric comparison decides "is newer".
/// </summary>
/// <remarks>
/// Comparison is numeric per component, not lexicographic, so 0.10.0 &gt; 0.9.0. Only the
/// stable numeric core is compared: a pre-release suffix is discarded rather than ordered,
/// which is deliberate — O-view ships no pre-releases, and treating an unexpected suffix as
/// "same core version, do not offer an update" is the safe failure (it never nags to
/// downgrade or side-grade), matching CLAUDE.md rule 6 "never fabricate".
/// </remarks>
public readonly record struct ReleaseVersion(int Major, int Minor, int Patch)
    : IComparable<ReleaseVersion>
{
    /// <summary>
    /// Parses a tag or version string. Accepts an optional leading <c>v</c>/<c>V</c>, one to
    /// four dot-separated numeric components (missing minor/patch default to 0), and a
    /// pre-release/build suffix introduced by <c>-</c> or <c>+</c> which is ignored. Returns
    /// false for anything that does not begin with a recognisable numeric version.
    /// </summary>
    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var span = text.AsSpan().Trim();
        if (span.Length > 0 && (span[0] == 'v' || span[0] == 'V'))
        {
            span = span[1..];
        }

        // Cut off a pre-release/build suffix ("-beta", "+build") before splitting on '.'.
        var suffix = span.IndexOfAny('-', '+');
        if (suffix >= 0)
        {
            span = span[..suffix];
        }

        if (span.IsEmpty)
        {
            return false;
        }

        var parts = span.ToString().Split('.');
        if (parts.Length is 0 or > 4)
        {
            return false;
        }

        var numbers = new int[3];
        for (var i = 0; i < parts.Length && i < 3; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                return false;
            }
            numbers[i] = n;
        }

        version = new ReleaseVersion(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    public int CompareTo(ReleaseVersion other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0)
        {
            return major;
        }
        var minor = Minor.CompareTo(other.Minor);
        return minor != 0 ? minor : Patch.CompareTo(other.Patch);
    }

    public static bool operator <(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) >= 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}
