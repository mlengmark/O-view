namespace OView.Core;

/// <summary>
/// Whether two paths name the same thing — a question with a different answer on each
/// platform, so it is asked in exactly one place.
///
/// <para>Windows filesystems are case-insensitive: <c>Alpha</c> and <c>alpha</c> are one
/// directory, and treating them as two would scan it twice. Linux filesystems are
/// case-sensitive: they are two directories, and treating them as one <b>silently drops
/// the second</b> — no error, no warning, just missing usage.</para>
///
/// <para><b>This is only for path <i>identity</i>.</b> Plenty of other comparisons in this
/// codebase are correctly case-insensitive on every platform — model ids, org UUIDs, the
/// vendor name inside a package directory, the frozen Windows asset name. Those are text
/// matching and must not be routed through here; changing them would break real
/// behaviour.</para>
///
/// <para><b>macOS is deliberately not handled.</b> It is out of scope (ADR-0012), and its
/// default filesystem is case-insensitive but case-<i>preserving</i>, which is a third
/// answer rather than one of these two. If it ever comes into scope this needs revisiting
/// rather than inheriting the Linux branch by default.</para>
/// </summary>
public static class PathIdentity
{
    /// <summary>
    /// The comparer for de-duplicating or looking up paths. Resolved once — the platform
    /// does not change while the process runs.
    /// </summary>
    public static StringComparer Comparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>The same rule, for <c>string.Equals</c> and friends.</summary>
    public static StringComparison Comparison { get; } =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Whether two paths name the same filesystem entry, as strings.</summary>
    public static bool AreSame(string a, string b) => Comparer.Equals(a, b);
}
