using System.Globalization;
using System.Text;
using System.Text.Json;

namespace OView.Core.Providers.Jsonl;

/// <summary>
/// Reads Claude agent JSONL transcripts (docs/findings/jsonl-schema.md) — both Claude
/// Code transcripts and Cowork audit logs, whose assistant records carry the identical
/// usage schema and differ only in how the request id is spelled (see
/// <see cref="ReadRequestId"/>). Claude appends while we read, so files open with
/// FileShare.ReadWrite and a partially flushed final line is normal, not corruption.
/// Malformed lines and unknown record types are skipped silently; one bad line must
/// never fail a scan.
/// </summary>
public static class TranscriptReader
{
    /// <summary>
    /// Parse a whole transcript file into assistant records, in file order (append
    /// order — so per-request, later records supersede earlier ones). Returns empty on
    /// any file-level failure. Never throws.
    /// </summary>
    public static IReadOnlyList<TranscriptRecord> ReadFile(string path) => ReadFrom(path, 0).Records;

    /// <summary>
    /// Parse only the bytes appended after <paramref name="startOffset"/>, returning
    /// the records found and the offset to resume from next time.
    ///
    /// Transcripts are append-only and grow without bound, so re-parsing them whole on
    /// every poll wastes work that scales with history rather than with new activity.
    /// The returned offset lands on a line boundary: a poll that catches a half-written
    /// line leaves it unconsumed and re-reads it next time, rather than parsing a
    /// truncated record or skipping it permanently.
    /// </summary>
    public static (IReadOnlyList<TranscriptRecord> Records, long NextOffset) ReadFrom(string path, long startOffset)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            // A shorter file than we last saw means it was replaced or rotated; the
            // stored offset now points into unrelated content, so start over.
            if (startOffset > stream.Length)
            {
                startOffset = 0;
            }

            stream.Position = startOffset;
            var buffer = new byte[stream.Length - startOffset];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            if (read == 0)
            {
                return ([], startOffset);
            }

            // Consume up to the last newline only. Anything after it is a line still
            // being written; leaving it unconsumed is what makes resumption safe.
            var lastNewline = Array.LastIndexOf(buffer, (byte)'\n', read - 1);
            if (lastNewline < 0)
            {
                return ([], startOffset);
            }

            var records = new List<TranscriptRecord>();
            foreach (var line in Encoding.UTF8.GetString(buffer, 0, lastNewline + 1)
                         .Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (TryParseLine(line) is { } record)
                {
                    records.Add(record);
                }
            }

            return (records, startOffset + lastNewline + 1);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ([], startOffset);
        }
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

            if (ReadRequestId(root) is not { } requestId)
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

    /// <summary>
    /// The request id under either spelling. Claude Code transcripts write
    /// <c>requestId</c>; Cowork audit logs write <c>request_id</c> with an otherwise
    /// identical record. Reading only the camelCase name ingested nothing at all from
    /// Cowork — no error, no partial total, just permanently empty tiles (issue #44).
    /// </summary>
    private static string? ReadRequestId(JsonElement root)
    {
        if (root.TryGetProperty("requestId", out var camel) &&
            camel.ValueKind == JsonValueKind.String &&
            camel.GetString() is { Length: > 0 } fromCamel)
        {
            return fromCamel;
        }

        if (root.TryGetProperty("request_id", out var snake) &&
            snake.ValueKind == JsonValueKind.String &&
            snake.GetString() is { Length: > 0 } fromSnake)
        {
            return fromSnake;
        }

        return null;
    }

    /// <summary>Absent token fields mean none reported; zero, not an error.</summary>
    private static long ReadTokenField(JsonElement usage, string name) =>
        usage.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var value) && value >= 0
            ? value
            : 0;
}
