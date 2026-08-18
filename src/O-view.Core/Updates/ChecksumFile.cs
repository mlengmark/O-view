namespace OView.Core.Updates;

/// <summary>
/// The <c>SHA256SUMS</c> file published with every release, parsed.
///
/// <para>Kept in Core with no IO so the parsing is unit-testable without a network or a
/// download — the same reason <see cref="UpdateCheck"/> lives here. The head supplies the
/// text and the bytes; this decides what the file claims.</para>
///
/// <para><b>Every failure is "no answer", never a wrong one.</b> A malformed line, a
/// duplicate name, a truncated file — each yields a lookup that fails rather than a hash
/// that might match by accident. The caller treats "no answer" as "do not install", so
/// leniency here would quietly become permission there (CLAUDE.md rule 6).</para>
/// </summary>
public static class ChecksumFile
{
    /// <summary>Length of a SHA-256 digest written as hex.</summary>
    private const int DigestLength = 64;

    /// <summary>
    /// The digest recorded for <paramref name="fileName"/>, or null when the file does not
    /// name it, names it more than once, or records something that is not a SHA-256 digest.
    ///
    /// <para>Accepts the shapes <c>sha256sum</c> actually writes: two spaces between digest
    /// and name, a <c>*</c> marking binary mode, and a <c>./</c> prefix on the name. It does
    /// not accept a path — a name with a directory separator in it is a malformed entry for
    /// this purpose, and treating one as a match would let the file speak about something
    /// other than the asset that was downloaded.</para>
    /// </summary>
    public static string? DigestFor(string? text, string fileName)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        string? found = null;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var split = line.IndexOf(' ');
            if (split != DigestLength)
            {
                // Includes a short digest, a long one, and a line with no separator at all.
                continue;
            }

            var digest = line[..DigestLength];
            if (!IsHex(digest))
            {
                continue;
            }

            var name = line[(split + 1)..].TrimStart();
            if (name.StartsWith('*'))
            {
                name = name[1..];   // binary-mode marker
            }
            if (name.StartsWith("./", StringComparison.Ordinal))
            {
                name = name[2..];
            }

            if (name.Length == 0 || name.Contains('/') || name.Contains('\\'))
            {
                continue;
            }

            // Asset names are compared case-sensitively: the release workflow writes them
            // and ReleaseAssets matches them, and both are exact. This is not the
            // path-identity question PathIdentity answers.
            if (!string.Equals(name, fileName, StringComparison.Ordinal))
            {
                continue;
            }

            if (found is not null)
            {
                // Named twice. Which one is authoritative is not a guess worth making.
                return null;
            }

            found = digest;
        }

        return found;
    }

    /// <summary>
    /// Whether a computed digest matches a recorded one. Hex casing differs between tools —
    /// <c>sha256sum</c> writes lowercase, .NET's <c>Convert.ToHexString</c> uppercase — so
    /// the comparison is case-insensitive over a fixed alphabet, which carries no culture
    /// sensitivity.
    /// </summary>
    public static bool Matches(string? recorded, string? computed) =>
        recorded is { Length: DigestLength }
        && computed is { Length: DigestLength }
        && string.Equals(recorded, computed, StringComparison.OrdinalIgnoreCase);

    private static bool IsHex(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F')))
            {
                return false;
            }
        }
        return true;
    }
}
