using OView.Core.Models;

namespace OView.Core.Tests;

public class TraySettingsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("oview-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void RoundTrips()
    {
        var path = Path.Combine(_dir, "settings.json");
        new TraySettings(NotifyOnThreshold: false, ThresholdPercent: 70).Save(path);

        var loaded = TraySettings.Load(path);

        Assert.False(loaded.NotifyOnThreshold);
        Assert.Equal(70, loaded.ThresholdPercent);
    }

    [Fact]
    public void MissingOrMalformed_YieldsDefaults()
    {
        Assert.Equal(new TraySettings(), TraySettings.Load(Path.Combine(_dir, "absent.json")));

        var bad = Path.Combine(_dir, "bad.json");
        File.WriteAllText(bad, "{not json");
        Assert.Equal(new TraySettings(), TraySettings.Load(bad));
    }

    [Fact]
    public void OutOfRangeThreshold_YieldsDefaults()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{\"NotifyOnThreshold\":true,\"ThresholdPercent\":0}");

        Assert.Equal(85, TraySettings.Load(path).ThresholdPercent);
    }
}
