using OView.Core.Models;
using OView.Core.Providers.Jsonl;
using OView.Core.Storage;

namespace OView.Core.Tests;

/// <summary>
/// The tokens behind the session bar (GitHub issue #218).
///
/// <para>Every other token figure on the panel is a calendar day or 31 of them, while the bar
/// above them is a five-hour rolling window. Nothing was scoped to the bar, so a user reading
/// <c>5h: 87%</c> and looking for the tokens behind that 87% found a number measuring a
/// different period — and reported their usage as uncounted. It was counted; it was never
/// shown against the period it belongs to.</para>
/// </summary>
public class SessionWindowUsageTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WindowStart = Now.AddHours(-3);

    private readonly string _dir = Directory.CreateTempSubdirectory("oview-sessionwindow-").FullName;
    private readonly RollupStore _store;

    public SessionWindowUsageTests() => _store = new RollupStore(Path.Combine(_dir, "usage.db"));

    public void Dispose()
    {
        _store.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        GC.SuppressFinalize(this);
    }

    private void Seed(string id, DateTimeOffset at, long output, string model = "claude-opus-5") =>
        _store.Ingest([new TranscriptRecord(id, at, model, 10, 0, 0, output)]);

    /// <summary>A meter series that establishes a window without tripping the divergence detector.</summary>
    private static int[] Steady => [10, 12, 14];

    private PanelStatistics Build(IReadOnlyList<int> percents) =>
        PanelStatistics.Build(_store, Now, TimeZoneInfo.Utc)
            .WithDivergence(_store, WindowStart, percents, TimeSpan.Zero);

    /// <summary>
    /// <b>The figure the issue asked for.</b> Usage inside the window is summed and priced;
    /// usage outside it is not, however recent — that boundary is the whole point, because a
    /// figure that quietly included yesterday would be the same mismatch in the other
    /// direction.
    /// </summary>
    [Fact]
    public void TheSessionFigureCountsTheWindowAndNothingOutsideIt()
    {
        Seed("in-1", WindowStart.AddMinutes(10), 1_000);
        Seed("in-2", Now.AddMinutes(-5), 2_000);
        Seed("before", WindowStart.AddMinutes(-10), 500_000);

        var stats = Build(Steady);

        Assert.True(stats.HasSessionWindow);
        Assert.Equal(10 + 1_000 + 10 + 2_000, stats.TokensSession);
        Assert.NotNull(stats.EstSessionUsd);
        Assert.True(stats.EstSessionUsd > 0);
    }

    /// <summary>
    /// A window with no local record reports zero <i>and</i> is distinguished from having no
    /// window at all. Those are different machines: one used Claude somewhere that keeps no
    /// local record, the other has never had the plan meters read.
    /// </summary>
    [Fact]
    public void AnEmptyWindowIsNotTheSameAsNoWindow()
    {
        Seed("before", WindowStart.AddHours(-40), 500_000);

        var withWindow = Build(Steady);
        Assert.True(withWindow.HasSessionWindow);
        Assert.Equal(0, withWindow.TokensSession);

        // No meter samples at all — GetCurrentWindow's empty case.
        var withoutWindow = Build([]);
        Assert.False(withoutWindow.HasSessionWindow);
    }

    /// <summary>
    /// The line names its own scope. The panel's other token figures are a day and 31 days, so
    /// a figure placed under the bar without a scope word would move the ambiguity rather than
    /// remove it — the lesson of #210 and #169, applied before the fact.
    /// </summary>
    [Fact]
    public void TheLineNamesTheWindowAndSaysTheFiguresAreLocal()
    {
        Seed("in-1", WindowStart.AddMinutes(10), 1_000);

        var line = PanelText.SessionUsageLine(Build(Steady));

        Assert.Contains("This session window", line, StringComparison.Ordinal);
        Assert.Contains("local sessions only", line, StringComparison.Ordinal);
        Assert.Contains("Est.", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The pairing that got reported as a bug.</b> An empty local record beside a high
    /// percentage is explained on the spot, because the panel has the facts and the alternative
    /// is a support round trip — the same failure as #44, #58 and #170.
    /// </summary>
    [Fact]
    public void AnEmptyWindowExplainsWhyTheBarCanStillBeHigh()
    {
        // History exists, just none of it inside the window — the reported machine's shape.
        Seed("earlier", Now.AddDays(-3), 500_000);

        var stats = Build(Steady);

        Assert.Contains("no local session activity recorded",
            PanelText.SessionUsageLine(stats), StringComparison.Ordinal);

        var note = PanelText.SessionUsageNote(stats, Now, TimeZoneInfo.Utc);
        Assert.Contains("whole account", note, StringComparison.Ordinal);
        Assert.Contains("Chat keeps no local usage record", note, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The figure that turns an alarming absence into a dated observation.</b> "No local
    /// session activity" beside a bar reading 100% invites the reading that something has just
    /// broken; on the machine in issue #218 nothing had been written for two days. Stating how
    /// stale the record is says what actually happened, and is the figure a support report
    /// needs.
    /// </summary>
    [Fact]
    public void TheExplanationLeadsWithHowStaleTheLocalRecordIs()
    {
        Seed("earlier", Now.AddDays(-3), 500_000);

        var note = PanelText.SessionUsageNote(Build(Steady), Now, TimeZoneInfo.Utc);

        Assert.StartsWith("Newest local record: 3d 0h old", note, StringComparison.Ordinal);
    }

    /// <summary>
    /// The last clause is deliberately open. It read "a session on another device", which was
    /// too narrow — a session can leave no transcript on the machine it is running on, which is
    /// issue #218 exactly, and the mechanism was never established. Naming a cause O-view cannot
    /// see would be the fabrication rule 6 forbids.
    /// </summary>
    [Fact]
    public void TheExplanationNamesTheConsequenceRatherThanACause()
    {
        Seed("earlier", Now.AddDays(-3), 500_000);

        var note = PanelText.SessionUsageNote(Build(Steady), Now, TimeZoneInfo.Utc);

        Assert.Contains("a session that writes no transcript on this machine", note, StringComparison.Ordinal);
        Assert.DoesNotContain("another device", note, StringComparison.Ordinal);
    }

    /// <summary>
    /// A machine that has recorded nothing at all keeps quiet here: the token-scope note
    /// already names the surfaces and the locations searched (#58, #170), and it is the better
    /// explanation because "none anywhere" is a stronger statement than "none in this window".
    /// Two paragraphs saying overlapping things is how a panel stops being read.
    /// </summary>
    [Fact]
    public void TheExplanationDefersToTheScopeNoteWhenNothingWasEverRecorded()
    {
        var stats = Build(Steady);

        Assert.Equal(0, stats.Tokens31Days);
        Assert.Contains("no local session activity recorded",
            PanelText.SessionUsageLine(stats), StringComparison.Ordinal);
        Assert.Equal("", PanelText.SessionUsageNote(stats, Now, TimeZoneInfo.Utc));
    }

    /// <summary>The explanation appears only where it is needed — a figure that agrees with its bar explains itself.</summary>
    [Fact]
    public void TheExplanationIsAbsentWhenTheWindowHasUsage()
    {
        Seed("in-1", WindowStart.AddMinutes(10), 1_000);

        Assert.Equal("", PanelText.SessionUsageNote(Build(Steady), Now, TimeZoneInfo.Utc));
    }

    /// <summary>
    /// No window means no line at all. A zero would be a claim about usage where the truth is
    /// that there is nothing to measure it against (rule 6).
    /// </summary>
    [Fact]
    public void NoWindowRendersNothingRatherThanAZero()
    {
        Seed("in-1", WindowStart.AddMinutes(10), 1_000);

        var stats = Build([]);

        Assert.Equal("", PanelText.SessionUsageLine(stats));
        Assert.Equal("", PanelText.SessionUsageNote(stats, Now, TimeZoneInfo.Utc));
    }

    /// <summary>
    /// An unpriced model is named rather than voiding the figure, and rather than being folded
    /// in silently — the same rule the 31-day caveat follows (#56).
    /// </summary>
    [Fact]
    public void AnUnpricedModelIsNamedBesideTheFigure()
    {
        Seed("priced", WindowStart.AddMinutes(10), 1_000);
        Seed("unpriced", WindowStart.AddMinutes(20), 1_000, "claude-from-the-future");

        var stats = Build(Steady);
        var line = PanelText.SessionUsageLine(stats);

        Assert.Contains("claude-from-the-future", stats.UnpricedModelsSession);
        Assert.Contains("no published rate", line, StringComparison.Ordinal);

        // The priced part still shows: one unrecognised model must not blank the figure.
        Assert.NotNull(stats.EstSessionUsd);
    }

    /// <summary>
    /// The money label flips off-plan, and this is the one figure where that is unambiguously
    /// right: divergence is detected for <i>this</i> window, so unlike the 31-day heading the
    /// label extends no claim past what was measured.
    /// </summary>
    [Fact]
    public void TheMoneyLabelFlipsWhenTheWindowIsOffPlan()
    {
        Seed("in-1", WindowStart.AddMinutes(10), 400_000);

        // A meter pinned flat at the limit while output keeps climbing is the off-plan shape.
        var offPlan = Build([100, 100, 100]);

        Assert.True(offPlan.IsOffPlan);
        Assert.Contains("Est. spend", PanelText.SessionUsageLine(offPlan), StringComparison.Ordinal);
    }
}
