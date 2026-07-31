namespace OView.App;

/// <summary>
/// Parses the diagnostic flags both heads accept.
///
/// <para>Shared so a bug report reads the same on either platform: <c>--log</c>,
/// <c>--interval-ms</c> and the offscreen render hooks mean the same thing everywhere, and
/// two parsers would eventually disagree about something small like whether a flag may take
/// a value.</para>
/// </summary>
public static class CommandLine
{
    /// <summary>
    /// Flags to their values. A flag followed by a non-flag token takes it as a value;
    /// otherwise the value is null and the flag is present-only. Case-insensitive, because
    /// these are typed by hand into a terminal.
    /// </summary>
    public static Dictionary<string, string?> Parse(string[] args)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                result[args[i]] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                    ? args[++i]
                    : null;
            }
        }
        return result;
    }
}
