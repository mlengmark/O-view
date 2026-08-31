using OView.Core.Pricing;
using System.Text;
using OView.Core.Providers.Jsonl;

namespace OView.Core.Tests;

/// <summary>
/// <see cref="TranscriptReader.ReadFrom"/> streams rather than reading each file whole
/// (issue #125). The records and the resumption offset must be byte-for-byte what the
/// slurping implementation produced — the change was made for allocation, not behaviour,
/// and a difference here is the kind that silently loses or double-counts tokens.
///
/// <para>The cases that matter are the ones a whole-file read could not get wrong by
/// construction: a record longer than the read buffer, and a record that lands across a
/// buffer boundary. <see cref="IncrementalReadTests"/> covers the resumption semantics
/// themselves; these cover the seam underneath them.</para>
/// </summary>
public class StreamedTranscriptReadTests : IDisposable
{
    /// <summary>Must match <c>TranscriptReader.ReadBufferSize</c>; the point is to cross it.</summary>
    private const int ReadBufferSize = 64 * 1024;

    private readonly string _dir = Directory.CreateTempSubdirectory("oview-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary><paramref name="padding"/> inflates one record so its length can be controlled.</summary>
    private static string Line(string requestId, long output, int padding = 0) =>
        $"{{\"type\":\"assistant\",\"requestId\":\"{requestId}\",\"timestamp\":\"2026-07-21T10:00:00.000Z\"," +
        $"\"padding\":\"{new string('x', padding)}\"," +
        $"\"message\":{{\"model\":\"claude-opus-4-8\",\"usage\":{{\"input_tokens\":1," +
        $"\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0,\"output_tokens\":{output}}}}}}}";

    private string WriteRaw(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    [Fact]
    public void ARecordLongerThanTheReadBufferIsStillParsed()
    {
        var path = WriteRaw("big.jsonl", Line("req_A", 10, padding: ReadBufferSize * 3) + "\n");

        var (records, offset) = TranscriptReader.ReadFrom(path, 0);

        Assert.Single(records);
        Assert.Equal("req_A", records[0].RequestId);
        Assert.Equal(new FileInfo(path).Length, offset);
    }

    /// <summary>
    /// The boundary is walked one byte at a time across the interesting window, because a
    /// carry-over bug shows up at exactly one alignment and is invisible at every other.
    /// </summary>
    [Theory]
    [InlineData(-2)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ARecordStraddlingTheBufferBoundaryIsParsedWhole(int nudge)
    {
        // Pad the first record so its terminating newline lands on, just before, or just
        // after the point where one buffer fill ends and the next begins.
        var head = Line("req_A", 10);
        var padding = ReadBufferSize + nudge - head.Length - 1;
        var path = WriteRaw("straddle.jsonl",
            Line("req_A", 10, padding) + "\n" + Line("req_B", 20) + "\n");

        var (records, offset) = TranscriptReader.ReadFrom(path, 0);

        Assert.Equal(["req_A", "req_B"], records.Select(r => r.RequestId));
        Assert.Equal(20, records[1].Tokens.Output);
        Assert.Equal(new FileInfo(path).Length, offset);
    }

    /// <summary>
    /// The half-written final line. Claude appends while O-view reads, so this is the
    /// normal case, not the exotic one — and the offset must stay behind it so the next
    /// poll re-reads the record whole rather than skipping it.
    /// </summary>
    [Fact]
    public void AnUnterminatedFinalLineIsLeftForTheNextPoll()
    {
        var complete = Line("req_A", 10) + "\n";
        var path = WriteRaw("partial.jsonl", complete + Line("req_B", 20)[..40]);

        var (records, offset) = TranscriptReader.ReadFrom(path, 0);

        Assert.Single(records);
        Assert.Equal(complete.Length, offset);

        // Completed by the writer, the second record arrives in full and exactly once.
        File.WriteAllText(path, complete + Line("req_B", 20) + "\n");
        var (rest, next) = TranscriptReader.ReadFrom(path, offset);

        Assert.Equal(["req_B"], rest.Select(r => r.RequestId));
        Assert.Equal(new FileInfo(path).Length, next);
    }

    /// <summary>
    /// A file that is nothing but an unterminated line yields no records and no advance —
    /// the case the old implementation spelled as "no newline found, return the offset
    /// unchanged".
    /// </summary>
    [Fact]
    public void AFileWithNoNewlineAdvancesNothing()
    {
        var path = WriteRaw("nonewline.jsonl", Line("req_A", 10));

        var (records, offset) = TranscriptReader.ReadFrom(path, 0);

        Assert.Empty(records);
        Assert.Equal(0, offset);
    }

    /// <summary>
    /// Blank lines carry no record but do carry bytes. Counting them wrong would shift
    /// every subsequent offset and re-ingest or skip whatever followed.
    /// </summary>
    [Fact]
    public void BlankLinesAreSkippedButStillCounted()
    {
        var path = WriteRaw("blanks.jsonl",
            "\n" + Line("req_A", 10) + "\n\n   \n" + Line("req_B", 20) + "\n");

        var (records, offset) = TranscriptReader.ReadFrom(path, 0);

        Assert.Equal(["req_A", "req_B"], records.Select(r => r.RequestId));
        Assert.Equal(new FileInfo(path).Length, offset);
    }

    /// <summary>
    /// Offsets are byte counts, not character counts. A record containing multi-byte UTF-8
    /// would desynchronise the two the moment anything measured the decoded length.
    /// </summary>
    [Fact]
    public void OffsetsAreCountedInBytesNotCharacters()
    {
        // Three ASCII padding characters replaced by text that is 2, 3 and 4 bytes per
        // character, so the line's byte length and its character length cannot agree.
        var multibyte = Line("req_A", 10, padding: 3)
            .Replace("xxx", "ü—🙂", StringComparison.Ordinal);
        var path = WriteRaw("utf8.jsonl", multibyte + "\n" + Line("req_B", 20) + "\n");

        var (records, offset) = TranscriptReader.ReadFrom(path, 0);

        Assert.Equal(2, records.Count);
        Assert.True(new FileInfo(path).Length > multibyte.Length + Line("req_B", 20).Length + 2,
            "the fixture is not actually multi-byte — the test would pass on a character count");
        Assert.Equal(new FileInfo(path).Length, offset);

        var (none, again) = TranscriptReader.ReadFrom(path, offset);
        Assert.Empty(none);
        Assert.Equal(offset, again);
    }
}
