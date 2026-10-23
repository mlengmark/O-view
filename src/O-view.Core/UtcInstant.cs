using System.Globalization;

namespace OView.Core;

/// <summary>
/// Parses and formats the one timestamp shape O-view stores and reads: an ISO 8601 instant
/// taken as UTC. Every file this app reads, and its own store, write instants that way, and
/// the same two <see cref="DateTimeStyles"/> flags were spelled out at seven call sites before
/// this existed.
/// </summary>
public static class UtcInstant
{
    private const DateTimeStyles Styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;

    /// <summary>Parses <paramref name="text"/> as a UTC instant; false for null or anything unparseable.</summary>
    public static bool TryParse(string? text, out DateTimeOffset value) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, Styles, out value);

    /// <summary>Parses <paramref name="text"/> as a UTC instant, or null for null or anything unparseable.</summary>
    public static DateTimeOffset? ParseOrNull(string? text) =>
        TryParse(text, out var value) ? value : null;

    /// <summary>The round-trip form the store writes: <c>yyyy-MM-ddTHH:mm:ss.fffffffZ</c>.</summary>
    public static string Format(DateTimeOffset at) =>
        at.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
}
