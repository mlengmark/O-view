using System.Text.Json;
using OView.Core.Providers.CachedUsage;

namespace OView.Core.Tests;

/// <summary>
/// The per-model weekly limits in Claude Code's cache. The one row ever seen was Fable at 10%
/// on 2026-11-06, which is also the first row anyone has seen with a scope at all; the rest of
/// these are the shapes a private cache is liable to drift into.
/// </summary>
public class ModelLimitTests
{
    private static ModelLimitRead Read(string utilization) =>
        ModelLimitRead.Read(JsonDocument.Parse(utilization).RootElement);

    private static string Limits(params string[] rows) => $$$"""{"limits": [{{{string.Join(",", rows)}}}]}""";

    private static string Row(
        string name, int percent = 10, string group = "weekly", string surface = "null",
        string resets = "\"2026-11-09T21:00:00+00:00\"") =>
        $$$"""
        {"kind": "weekly_scoped", "group": "{{{group}}}", "percent": {{{percent}}}, "resets_at": {{{resets}}},
         "scope": {"model": {"id": null, "display_name": "{{{name}}}"}, "surface": {{{surface}}}}}
        """;

    [Fact]
    public void RowsAreReadInFileOrder()
    {
        var read = Read(Limits(Row("Fable", 10), Row("Opus", 40), Row("Haiku", 0)));

        Assert.Equal(["Fable", "Opus", "Haiku"], read.Limits.Select(l => l.Model));
        Assert.Equal([10, 40, 0], read.Limits.Select(l => l.Bar.Percent));
        Assert.Equal(3, read.RowsSeen);
        Assert.Equal(0, read.RowsSkipped);
    }

    /// <summary>No plan name is consulted: the same row reads the same wherever it appears.</summary>
    [Fact]
    public void TheRowIsReadFromItsShapeAlone()
    {
        var read = Read("""
        {"plan": "claude_max_20x", "limits": [
          {"kind": "weekly_scoped", "group": "weekly", "percent": 55, "resets_at": null,
           "scope": {"model": {"display_name": "Sonnet"}}}]}
        """);

        var limit = Assert.Single(read.Limits);
        Assert.Equal("Sonnet", limit.Model);
        Assert.Equal(55, limit.Bar.Percent);
        Assert.Null(limit.Bar.ResetsAtUtc);
    }

    [Fact]
    public void TheAccountWideRowsAreSkippedAndCounted()
    {
        var read = Read(Limits(
            """{"kind": "session", "group": "session", "percent": 7, "scope": null}""",
            """{"kind": "weekly_all", "group": "weekly", "percent": 13, "scope": null}""",
            Row("Fable")));

        Assert.Equal("Fable", Assert.Single(read.Limits).Model);
        Assert.Equal(3, read.RowsSeen);
        Assert.Equal(2, read.RowsSkipped);
    }

    [Fact]
    public void ASurfaceScopedRowIsNotAModelLimit()
    {
        var read = Read(Limits(Row("Fable", surface: "\"claude_code\""), Row("Opus")));

        Assert.Equal("Opus", Assert.Single(read.Limits).Model);
        Assert.Equal(1, read.RowsSkipped);
    }

    [Fact]
    public void ARowWithoutAGroupOfWeeklyIsSkipped()
    {
        var read = Read(Limits(Row("Fable", group: "session")));

        Assert.Empty(read.Limits);
        Assert.Equal(1, read.RowsSkipped);
    }

    /// <summary>
    /// <c>model.id</c> is null on the real row, so the display name is the only name there is.
    /// A row that lacks it is skipped, never given a guessed one.
    /// </summary>
    [Theory]
    [InlineData("""{"model": {"id": null, "display_name": ""}, "surface": null}""")]
    [InlineData("""{"model": {"id": "claude-fable-5", "display_name": null}, "surface": null}""")]
    [InlineData("""{"model": {"id": "claude-fable-5"}}""")]
    [InlineData("""{"model": null}""")]
    [InlineData("""{"model": "Fable"}""")]
    [InlineData("""{}""")]
    [InlineData("""null""")]
    public void ARowWithoutAModelNameIsSkipped(string scope)
    {
        var read = Read(Limits($$$"""{"group": "weekly", "percent": 10, "scope": {{{scope}}}}"""));

        Assert.Empty(read.Limits);
        Assert.Equal(1, read.RowsSeen);
        Assert.Equal(1, read.RowsSkipped);
    }

    [Theory]
    [InlineData("\"10\"")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("1e300")]
    public void ARowWithoutAUsablePercentIsSkipped(string percent)
    {
        var read = Read(Limits($$$"""
            {"group": "weekly", "percent": {{{percent}}},
             "scope": {"model": {"display_name": "Fable"}, "surface": null}}
            """));

        Assert.Empty(read.Limits);
        Assert.Equal(1, read.RowsSkipped);
    }

    [Fact]
    public void APercentThatIsNotWholeIsRoundedLikeAMeter()
    {
        var read = Read(Limits("""
            {"group": "weekly", "percent": 12.6,
             "scope": {"model": {"display_name": "Fable"}, "surface": null}}
            """));

        Assert.Equal(13, Assert.Single(read.Limits).Bar.Percent);
    }

    /// <summary>
    /// The window may have rolled over by the time the file is read, and the account still
    /// has a limit for that model. Parsing keeps the row; the shared rule judges it.
    /// </summary>
    [Fact]
    public void ARowWhoseResetHasPassedIsKept()
    {
        var read = Read(Limits(Row("Fable", resets: "\"2020-01-01T00:00:00+00:00\"")));

        var limit = Assert.Single(read.Limits);
        Assert.Equal(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), limit.Bar.ResetsAtUtc);
    }

    [Fact]
    public void AnUnreadableResetKeepsThePercentageAndDropsTheInstant()
    {
        var read = Read(Limits(Row("Fable", resets: "\"soon\"")));

        var limit = Assert.Single(read.Limits);
        Assert.Equal(10, limit.Bar.Percent);
        Assert.Null(limit.Bar.ResetsAtUtc);
    }

    /// <summary>One bad row costs itself, not the rows on either side of it.</summary>
    [Fact]
    public void ABadRowDoesNotCostItsNeighbours()
    {
        var read = Read(Limits(Row("Fable"), "null", "7", "\"x\"", "[]", Row("Opus")));

        Assert.Equal(["Fable", "Opus"], read.Limits.Select(l => l.Model));
        Assert.Equal(6, read.RowsSeen);
        Assert.Equal(4, read.RowsSkipped);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"limits": null}""")]
    [InlineData("""{"limits": {}}""")]
    [InlineData("""{"limits": "none"}""")]
    [InlineData("""{"limits": []}""")]
    public void AMissingOrMalformedListYieldsNothing(string utilization)
    {
        var read = Read(utilization);

        Assert.Empty(read.Limits);
        Assert.Equal(0, read.RowsSeen);
        Assert.Equal(0, read.RowsSkipped);
    }

    [Fact]
    public void ANonObjectUtilizationYieldsNothing()
    {
        Assert.Equal(ModelLimitRead.None, ModelLimitRead.Read(default));
        Assert.Equal(ModelLimitRead.None, Read("[]"));
    }
}
