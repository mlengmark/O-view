using System.Globalization;
using System.Text.Json;

namespace OView.Core.Providers.Jsonl;

/// <summary>
/// Reads Claude Code JSONL transcripts (docs/findings/jsonl-schema.md). Claude Code
/// appends while we read, so files open with FileShare.ReadWrite and a partially
/// flushed final line is normal, not corruption. Malformed lines and unknown record
/// types are skipped silently; one bad line must never fail a scan.
/// </summary>
public static class TranscriptReader
{
    /// <summary>
    /// Parse one transcript file into assistant records, in file order (append order —
    /// so per-request, later records supersede earlier ones). Returns empty on any
    /// file-level failure. Never throws.
    /// </summary>
    public static IReadOnlyList<TranscriptRecord> ReadFile(string path)
    {
        var result = new List<TranscriptRecord>();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (TryParseLine(line) is { } record)
                {
                    result.Add(record);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return result;
    }

    private static TranscriptRecord? TryParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            // Only assistant records carry usage. Unknown types are skipped silently —
            // the type list is not assumed exhaustive.
            if (!root.TryGetProperty("type", out var type) ||
                type.ValueKind != JsonValueKind.String ||
                type.GetString() != "assistant")
            {
                return null;
            }

            if (!root.TryGetProperty("requestId", out var reqId) ||
                reqId.ValueKind != JsonValueKind.String ||
                reqId.GetString() is not { Length: > 0 } requestId)
            {
                return null;
            }

            if (!root.TryGetProperty("timestamp", out var ts) ||
                ts.ValueKind != JsonValueKind.String ||
                !DateTimeOffset.TryParse(ts.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
            {
                return null;
            }

            if (!root.TryGetProperty("message", out var message) ||
                message.ValueKind != JsonValueKind.Object ||
                !message.TryGetProperty("usage", out var usage) ||
                usage.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var model = message.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString()!
                : "unknown";

            // Claude Code writes placeholder records (errors, interruptions) with
            // model "<synthetic>" and all-zero usage — verified on real transcripts.
            // Not API calls: counting them inflates request counts and marks cost
            // tiles unpriceable.
            if (model == "<synthetic>") return null;

            return new TranscriptRecord(
                requestId,
                timestamp,
                model,
                ReadTokenField(usage, "input_tokens"),
                ReadTokenField(usage, "cache_creation_input_tokens"),
                ReadTokenField(usage, "cache_read_input_tokens"),
                ReadTokenField(usage, "output_tokens"));
        }
        catch (JsonException)
        {
            // Truncated final line or malformed content — skip, never fatal.
            return null;
        }
    }

    /// <summary>Absent token fields mean none reported; zero, not an error.</summary>
    private static long ReadTokenField(JsonElement usage, string name) =>
        usage.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var value) && value >= 0
            ? value
            : 0;
}
