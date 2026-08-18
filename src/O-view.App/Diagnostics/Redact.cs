using System.Text.RegularExpressions;

namespace OView.App.Diagnostics;

/// <summary>
/// Removes the two identifying things a support bundle would otherwise publish: the account
/// name embedded in every path, and organization UUIDs.
///
/// <para><b>Why this is one pass over the finished text rather than a change at each field.</b>
/// The bundle is assembled from three sources — <see cref="DiagnosticsBundle"/> itself, the
/// plan-history report and the transcript scope report — and each emits paths independently.
/// Redacting at every site means the next field someone adds leaks by default, and the
/// failure is silent: nobody notices a username in a bug report until it is already public.
/// Applied once at the funnel, a new field is redacted whether or not its author thought
/// about it.</para>
///
/// <para><b>Shape is preserved, because shape is the diagnostic.</b> The roots are printed so
/// that a wrong <c>SpecialFolder</c> resolution is visible, so collapsing a path to <c>~</c>
/// would defeat the purpose of printing it. Only the account name is replaced:
/// <c>C:\Users\ada\AppData\Roaming</c> becomes <c>C:\Users\&lt;user&gt;\AppData\Roaming</c>,
/// which still shows the drive, the profile container and everything below it.</para>
///
/// <para><b>UUIDs keep a prefix rather than vanishing.</b> The org UUID's diagnostic job is
/// to be compared — the one in <c>~/.claude.json</c> against the ones in the plan-history
/// file, to explain an org filter dropping every sample. Eight hex characters is enough to
/// tell "these match" from "these differ" at a glance, and is not the identifier.</para>
///
/// <para><b>Over-redaction is the safe failure here</b>, which is why account-name matching
/// ignores case on every platform. On Linux two differently-cased names are genuinely two
/// users, so this could redact a segment that merely resembles the account name. That costs
/// a slightly less precise path in a bug report. Missing one costs a real name in a public
/// issue, and only one of those can be undone.</para>
/// </summary>
public static partial class Redact
{
    /// <summary>What replaces the account name inside a path.</summary>
    public const string UserPlaceholder = "<user>";

    /// <summary>
    /// Redacts a finished bundle. Uses this machine's account name; the overload takes it
    /// explicitly so the behaviour is testable against a value rather than against whatever
    /// account the test runner happens to be using.
    /// </summary>
    public static string Bundle(string text) => Bundle(text, AccountNames());

    /// <param name="accountNames">
    /// Every spelling of the account name worth removing. Normally the login name and the
    /// profile directory's own name, which are usually but not always identical — a display
    /// name of <c>ada.lovelace</c> can own a profile folder called <c>ada</c>, and a bundle
    /// that redacted only one of those would still carry the other.
    /// </param>
    public static string Bundle(string text, IReadOnlyCollection<string> accountNames)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var redacted = text;

        // Longest first, because one account name can be a prefix of another. A login of
        // "ada.lovelace" beside a profile folder "ada" is the real case: taking "ada" first
        // matches the prefix inside "ada.lovelace" — the trailing dot is a valid segment
        // boundary — and leaves "<user>.lovelace", which publishes the surname while looking
        // redacted. Longest match wins, so the more specific spelling is consumed first.
        foreach (var name in accountNames.OrderByDescending(n => n?.Length ?? 0))
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length < 2)
            {
                // A one-character account name cannot be matched as a path segment without
                // hitting drive letters and directory names that merely start with it.
                continue;
            }

            // Only as a whole path segment. A substring replace would turn a user called
            // "max" into "<user>" inside "maxsize", mangling paths that mention nobody.
            redacted = Regex.Replace(
                redacted,
                $@"(?<=^|[/\\]){Regex.Escape(name)}(?=$|[/\\.,;:'""\s])",
                UserPlaceholder,
                RegexOptions.IgnoreCase | RegexOptions.Multiline,
                TimeSpan.FromSeconds(2));
        }

        return UuidPattern().Replace(redacted, m => $"{m.Value[..8]}…");
    }

    /// <summary>
    /// The account name as this machine spells it, in both places it appears. Distinct and
    /// non-empty; either can legitimately be missing on a stripped-down system.
    /// </summary>
    public static IReadOnlyCollection<string> AccountNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (Environment.UserName is { Length: > 0 } login)
        {
            names.Add(login);
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile is { Length: > 0 })
        {
            var leaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(profile));
            if (leaf is { Length: > 0 })
            {
                names.Add(leaf);
            }
        }

        return names;
    }

    // Canonical 8-4-4-4-12. Anchored on non-hex boundaries so a longer hex run — a SHA-256
    // in a checksum line, say — is not mistaken for one.
    [GeneratedRegex(
        @"(?<![0-9a-fA-F-])[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}(?![0-9a-fA-F-])")]
    private static partial Regex UuidPattern();
}
