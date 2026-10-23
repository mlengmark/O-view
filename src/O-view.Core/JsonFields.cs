using System.Text.Json;

namespace OView.Core;

/// <summary>
/// The two ways every file O-view reads asks a JSON object for an optional string. Six private
/// copies of these used to sit beside their callers; one definition means one answer to
/// "what does an absent, null or empty field mean".
/// </summary>
public static class JsonFields
{
    /// <summary>
    /// A non-empty string property, or null when it is absent, JSON <c>null</c>, another type
    /// or the empty string. For fields where an empty value carries no more information than
    /// a missing one: a display name, an organization type, a message.
    /// </summary>
    public static string? NonEmptyString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String &&
        v.GetString() is { Length: > 0 } s
            ? s
            : null;

    /// <summary>
    /// A string property as written, or null when it is absent, JSON <c>null</c> or another
    /// type. The empty string comes back as itself. <c>null</c> is not hypothetical: a Cowork
    /// audit record carries <c>"speed": null</c> beside <c>"inference_geo": null</c>, and both
    /// mean the same as the field being missing.
    /// </summary>
    public static string? StringOrNull(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
