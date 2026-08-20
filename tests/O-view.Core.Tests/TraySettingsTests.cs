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
        // A non-default value, so a bug that silently returns defaults would fail here.
        new TraySettings(NotifyOnThreshold: false, ThresholdPercent: 55).Save(path);

        var loaded = TraySettings.Load(path);

        Assert.False(loaded.NotifyOnThreshold);
        Assert.Equal(55, loaded.ThresholdPercent);
    }

    [Fact]
    public void DefaultThreshold_MatchesCriticalBand()
    {
        // Out of the box, notify exactly when the gauge turns red (issue #2).
        Assert.Equal(OView.Core.Models.UsageLevels.CriticalPercent, new TraySettings().ThresholdPercent);
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

        Assert.Equal(70, TraySettings.Load(path).ThresholdPercent);
    }

    // ── automatic updates (ADR-0009 as amended, issue #140) ───────────────────────

    /// <summary>
    /// Constraint 1 of the amendment, asserted directly: a release must never turn this on.
    /// The whole justification for permitting automatic install is that the user chose it,
    /// so anything that switches it on for them removes the thing that made it acceptable.
    /// </summary>
    [Fact]
    public void UpdateAutomatically_IsOffByDefault()
    {
        Assert.False(new TraySettings().UpdateAutomatically);
    }

    /// <summary>
    /// A settings file written before the field existed — every install upgrading into this
    /// version. It must read back as off, not as "absent so who knows".
    /// </summary>
    [Fact]
    public void UpdateAutomatically_IsOffWhenTheFilePredatesTheField()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{\"NotifyOnThreshold\":true,\"ThresholdPercent\":80}");

        var loaded = TraySettings.Load(path);

        Assert.False(loaded.UpdateAutomatically);
        Assert.Equal(80, loaded.ThresholdPercent);   // and the rest still loads
    }

    [Fact]
    public void UpdateAutomatically_SurvivesTheRoundTrip()
    {
        var path = Path.Combine(_dir, "settings.json");
        new TraySettings(UpdateAutomatically: true).Save(path);

        Assert.True(TraySettings.Load(path).UpdateAutomatically);
    }

    /// <summary>A malformed file falls back to off, like every other setting.</summary>
    [Fact]
    public void UpdateAutomatically_IsOffWhenTheFileIsUnreadable()
    {
        var path = Path.Combine(_dir, "bad-auto.json");
        File.WriteAllText(path, "{\"UpdateAutomatically\":tru");

        Assert.False(TraySettings.Load(path).UpdateAutomatically);
    }
}
