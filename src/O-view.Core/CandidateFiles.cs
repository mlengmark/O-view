using System.Text.Json;

namespace OView.Core;

/// <summary>
/// Reads the same file from every place it might live and keeps the freshest copy. A
/// relocated configuration can leave a stub behind at the old path, so the newest parsed
/// result wins rather than the first path that exists; an unreadable or malformed candidate is
/// skipped, and nothing is reported rather than a figure this cannot stand behind.
/// </summary>
public static class CandidateFiles
{
    /// <param name="candidates">Every path to try, in no particular order.</param>
    /// <param name="parse">Turns a file's text into a result, or null when the file does not carry one.</param>
    /// <param name="fetchedAtUtc">When the result's source says it was captured, which is what "freshest" means.</param>
    public static T? Newest<T>(
        IReadOnlyList<string> candidates, Func<string, T?> parse, Func<T, DateTimeOffset> fetchedAtUtc)
        where T : class
    {
        T? newest = null;

        foreach (var candidate in candidates)
        {
            try
            {
                if (parse(File.ReadAllText(candidate)) is { } parsed &&
                    (newest is null || fetchedAtUtc(parsed) > fetchedAtUtc(newest)))
                {
                    newest = parsed;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // Unreadable or malformed: try the next candidate.
            }
        }

        return newest;
    }
}
