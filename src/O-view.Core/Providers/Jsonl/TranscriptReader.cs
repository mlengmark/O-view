using System.Buffers;
using System.Globalization;
using System.Text.Json;
using OView.Core.Pricing;

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
    /// Claude Code's marker for a locally generated assistant message — an interruption,
    /// an error, a "prompt too long" notice. Not a model and not an API call.
    ///
    /// <para><b>This is the one and only place such records are handled.</b> They are
    /// dropped at parse time, so nothing downstream ever sees the id: it cannot reach the
    /// rollup store, so it cannot reach <see cref="Pricing.CostEstimator"/> or
    /// <see cref="Models.ModelDisplayName"/>. Both of those used to carry their own
    /// <c>&lt;synthetic&gt;</c> branch, which was unreachable and diverged from this one on
    /// case sensitivity — so an upstream casing change would have silently swapped which
    /// implementation was live, and with it whether these records count toward
    /// <c>DailyRollup.RequestCount</c> (GitHub issue #57).</para>
    ///
    /// <para>Dropping rather than storing at zero cost is deliberate and is the behaviour
    /// that has always shipped: the filter dates from the same commit that introduced the
    /// store, so no build has ever ingested one. Measured on real transcripts, every
    /// synthetic record carries all-zero usage in all four token fields — so storing them
    /// would add nothing to any total while inflating the request count with messages no
    /// model produced.</para>
    /// </summary>
    internal const string SyntheticModel = "<synthetic>";

    /// <summary>
    /// Parse a whole transcript file into assistant records, in file order (append
    /// order — so per-request, later records supersede earlier ones). Returns empty on
    /// any file-level failure. Never throws.
    /// </summary>
    public static IReadOnlyList<TranscriptRecord> ReadFile(string path) => ReadFrom(path, 0).Records;

    /// <summary>
    /// The file's length <b>as an open handle reports it</b>, or null if it cannot be opened.
    ///
    /// <para><b>Not <see cref="FileInfo.Length"/>, and the difference is the point.</b> That
    /// reads the cached directory entry through <c>GetFileAttributesEx</c>, which Windows
    /// documents as not necessarily current for a file that is open and being written — and
    /// every transcript this app reads is exactly that, held open by Claude for the length of a
    /// session. A stale entry makes a growing file look untouched, and ingestion's "unchanged
    /// since the last poll" test then skips it on every poll for as long as the session lasts:
    /// no error, no growth, no records, and a token tile frozen while the user works.</para>
    ///
    /// <para>Opening the file forces the real size. It costs one open per transcript per poll,
    /// which is O(1) rather than O(history) and does not touch the optimisation this check
    /// exists to serve.</para>
    /// </summary>
    public static long? CurrentLength(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return stream.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parse only the bytes appended after <paramref name="startOffset"/>, returning
    /// the records found and the offset to resume from next time.
    ///
    /// Transcripts are append-only and grow without bound, so re-parsing them whole on
    /// every poll wastes work that scales with history rather than with new activity.
    /// The returned offset lands on a line boundary: a poll that catches a half-written
    /// line leaves it unconsumed and re-reads it next time, rather than parsing a
    /// truncated record or skipping it permanently.
    ///
    /// <para><b>Streamed, not slurped.</b> This used to read the whole range into one
    /// <c>byte[]</c>, decode it to a string and <c>Split</c> that — roughly five times the
    /// file's size in transient allocations per file, nearly all of it on the large object
    /// heap. Steady state never noticed, because the offset above means there is usually
    /// nothing new to read. A <i>first</i> run has no offsets, and the first machine to
    /// arrive with 563 MB of history spent that whole ingest unresponsive (issue #125).
    /// Peak allocation is now the read buffer plus the longest single line, whatever the
    /// file's size.</para>
    /// </summary>
    public static (IReadOnlyList<TranscriptRecord> Records, long NextOffset) ReadFrom(string path, long startOffset)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                ReadBufferSize, FileOptions.SequentialScan);

            // A shorter file than we last saw means it was replaced or rotated; the
            // stored offset now points into unrelated content, so start over.
            if (startOffset > stream.Length)
            {
                startOffset = 0;
            }

            stream.Position = startOffset;

            var records = new List<TranscriptRecord>();
            var buffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);

            // Holds one line while it is being assembled, because a line can straddle any
            // number of buffer fills. Cleared per line, so it settles at the size of the
            // longest record seen rather than growing with the file.
            var line = new ArrayBufferWriter<byte>(4096);

            // Advanced only past a line that was terminated by a newline. Whatever is left
            // in `line` when the stream ends is a record still being written: it is
            // discarded, the offset stays behind it, and the next poll re-reads it whole.
            // That is the same guarantee the previous "consume to the last newline"
            // implementation gave.
            var consumed = startOffset;

            try
            {
                int read;
                while ((read = stream.Read(buffer, 0, ReadBufferSize)) > 0)
                {
                    var remaining = buffer.AsSpan(0, read);

                    while (true)
                    {
                        var newline = remaining.IndexOf((byte)'\n');
                        if (newline < 0)
                        {
                            line.Write(remaining);
                            break;
                        }

                        line.Write(remaining[..newline]);

                        if (TryParseLine(line.WrittenMemory) is { } record)
                        {
                            records.Add(record);
                        }

                        consumed += line.WrittenCount + 1;   // + the newline itself
                        line.Clear();
                        remaining = remaining[(newline + 1)..];
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return (records, consumed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ([], startOffset);
        }
    }

    /// <summary>
    /// 64 KB. Large enough that a poll over a big transcript is not dominated by syscalls,
    /// small enough to stay off the large object heap — the 85 KB threshold is the ceiling
    /// this is chosen under, and exceeding it is what the streaming change exists to avoid.
    /// </summary>
    private const int ReadBufferSize = 64 * 1024;

    /// <summary>
    /// One line, as raw UTF-8. Parsed from the bytes rather than a decoded string: the
    /// JSON reader consumes UTF-8 natively, so decoding first would allocate a copy at
    /// twice the size purely to hand it straight back.
    /// </summary>
    private static TranscriptRecord? TryParseLine(ReadOnlyMemory<byte> line)
    {
        // Blank and whitespace-only lines are skipped, as the Split that preceded this did.
        if (line.Span.IndexOfAnyExcept((byte)' ', (byte)'\t', (byte)'\r') < 0) return null;

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

            // See SyntheticModel: dropped here and nowhere else. Case-insensitive, so an
            // upstream casing change cannot slip one past into the store.
            if (model.Equals(SyntheticModel, StringComparison.OrdinalIgnoreCase)) return null;

            return new TranscriptRecord(
                requestId,
                timestamp,
                model,
                ReadTokens(usage),
                UsageModifiers.From(ReadStringField(usage, "speed"),
                    ReadStringField(usage, "inference_geo")));
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

    /// <summary>
    /// The six billable quantities behind one request.
    ///
    /// <para><b><c>usage.cache_creation</c> carries the TTL split, and reading it is the whole
    /// of GitHub issue #255.</b> The flat <c>cache_creation_input_tokens</c> was read and the
    /// object beside it was not, so every cache write was priced at the 5-minute rate while the
    /// transcripts here were almost entirely 1-hour, which bills at 2× rather than 1.25×.
    /// Measured on this machine: the object is present on 15,851 of 15,851 Claude Code
    /// assistant records and on 296 of 296 Cowork audit records (2026-08-31).</para>
    ///
    /// <para><b>Present on effectively every record is not present on every record.</b> A
    /// record whose object is missing or unreadable keeps its flat total in
    /// <see cref="TokenSplit.CacheWriteTtlUnrecorded"/> rather than being attributed to either
    /// TTL — the same bucket the migration puts pre-existing rows in, priced at the cheaper
    /// rate with the assumption named in the panel's caveat. Splitting the difference, or
    /// assuming the majority TTL, would be a fabricated attribution (rule 6).</para>
    ///
    /// <para>The two are reconciled rather than trusted separately: the flat field is the
    /// authority on the total, so anything it carries beyond the two TTL fields lands in the
    /// unrecorded bucket, and a total smaller than them clamps at zero.</para>
    /// </summary>
    private static TokenSplit ReadTokens(JsonElement usage)
    {
        var cacheWrite = ReadTokenField(usage, "cache_creation_input_tokens");

        long write5m = 0, write1h = 0;
        if (usage.TryGetProperty("cache_creation", out var split) &&
            split.ValueKind == JsonValueKind.Object)
        {
            write5m = ReadTokenField(split, "ephemeral_5m_input_tokens");
            write1h = ReadTokenField(split, "ephemeral_1h_input_tokens");
        }

        return new TokenSplit(
            ReadTokenField(usage, "input_tokens"),
            write5m,
            write1h,
            Math.Max(0, cacheWrite - write5m - write1h),
            ReadTokenField(usage, "cache_read_input_tokens"),
            ReadTokenField(usage, "output_tokens"));
    }

    /// <summary>
    /// A string field, or null when it is absent, JSON <c>null</c>, or another type. All of
    /// those mean the same thing to <see cref="UsageModifiers.From"/>: nothing said the price
    /// was modified. <c>null</c> is not hypothetical — a Cowork audit record here carries
    /// <c>"speed": null</c> beside <c>"inference_geo": null</c>.
    /// </summary>
    private static string? ReadStringField(JsonElement usage, string name) =>
        usage.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>Absent token fields mean none reported; zero, not an error.</summary>
    private static long ReadTokenField(JsonElement usage, string name) =>
        usage.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var value) && value >= 0
            ? value
            : 0;
}
