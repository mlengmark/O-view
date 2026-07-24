using OView.Core.Providers.PlanHistory;

namespace OView.Core.Tests;

public class PlanHistoryFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("oview-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteFile(string content)
    {
        var path = Path.Combine(_dir, "plan-usage-history.json");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void MissingFile_ReturnsEmpty_DoesNotThrow()
    {
        var samples = PlanHistoryFile.Read(Path.Combine(_dir, "does-not-exist.json"));

        Assert.Empty(samples);
    }

    [Fact]
    public void MalformedJson_ReturnsEmpty_DoesNotThrow()
    {
        var path = WriteFile("{ \"version\": 2, \"samples\": [ { \"t\": 17845");

        Assert.Empty(PlanHistoryFile.Read(path));
    }

    [Fact]
    public void WrongRootShape_ReturnsEmpty()
    {
        Assert.Empty(PlanHistoryFile.Read(WriteFile("[1, 2, 3]")));
        Assert.Empty(PlanHistoryFile.Read(WriteFile("{ \"version\": 3 }")));
        Assert.Empty(PlanHistoryFile.Read(WriteFile("{ \"samples\": \"nope\" }")));
    }

    [Fact]
    public void ValidFile_ParsesRealShape()
    {
        // Shape verified against the real file, docs/findings/plan-usage-history.md.
        var path = WriteFile("""
            {"version":2,"samples":[
              {"t":1784535700086,"org":"00000000-0000-0000-0000-000000000000","u":{"fh":0,"sd":2}},
              {"t":1784535999973,"org":"00000000-0000-0000-0000-000000000000","u":{"fh":5,"sd":3}}
            ]}
            """);

        var samples = PlanHistoryFile.Read(path);

        Assert.Equal(2, samples.Count);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1784535700086), samples[0].AtUtc);
        Assert.Equal(0, samples[0].FiveHourPercent);
        Assert.Equal(2, samples[0].SevenDayPercent);
        Assert.Equal(5, samples[1].FiveHourPercent);
        Assert.Equal("00000000-0000-0000-0000-000000000000", samples[1].OrgUuid);
    }

    [Fact]
    public void MalformedSamples_AreSkipped_ValidOnesKept()
    {
        var path = WriteFile("""
            {"version":2,"samples":[
              {"t":1784535700086,"org":"org-a","u":{"fh":10,"sd":2}},
              {"org":"org-a","u":{"fh":11,"sd":2}},
              {"t":1784535800000,"u":{"fh":12,"sd":2}},
              {"t":1784535810000,"org":"org-a"},
              {"t":1784535820000,"org":"org-a","u":{"fh":"high","sd":2}},
              {"t":1784535830000,"org":"org-a","u":{"fh":120,"sd":2}},
              {"t":1784535840000,"org":"","u":{"fh":13,"sd":2}},
              "not-an-object",
              {"t":1784535900000,"org":"org-a","u":{"fh":14,"sd":3}}
            ]}
            """);

        var samples = PlanHistoryFile.Read(path);

        Assert.Equal(2, samples.Count);
        Assert.Equal(10, samples[0].FiveHourPercent);
        Assert.Equal(14, samples[1].FiveHourPercent);
    }

    [Fact]
    public void UnorderedSamples_AreSortedByTime()
    {
        var path = WriteFile("""
            {"version":2,"samples":[
              {"t":1784535900000,"org":"org-a","u":{"fh":9,"sd":3}},
              {"t":1784535700086,"org":"org-a","u":{"fh":1,"sd":2}}
            ]}
            """);

        var samples = PlanHistoryFile.Read(path);

        Assert.Equal(1, samples[0].FiveHourPercent);
        Assert.Equal(9, samples[1].FiveHourPercent);
    }

    [Fact]
    public void FileLockedForWriting_CanStillBeRead()
    {
        // Claude Desktop appends while we read; FileShare.ReadWrite must tolerate it.
        var path = WriteFile("""
            {"version":2,"samples":[{"t":1784535700086,"org":"org-a","u":{"fh":7,"sd":2}}]}
            """);

        using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var samples = PlanHistoryFile.Read(path);

        Assert.Single(samples);
        Assert.Equal(7, samples[0].FiveHourPercent);
    }
}
