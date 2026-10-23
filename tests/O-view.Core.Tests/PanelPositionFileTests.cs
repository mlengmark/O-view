using OView.Core.Storage;

namespace OView.Core.Tests;

public class PanelPositionFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("oview-tests-").FullName;

    private string Path => System.IO.Path.Combine(_dir, "panel-position.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void RoundTrips()
    {
        var file = new PanelPositionFile(Path);
        file.Save("display-1", new PanelPositionFile.Place("BottomRight", 12, 34));

        var loaded = file.Read("display-1");

        Assert.NotNull(loaded);
        Assert.Equal("BottomRight", loaded!.Value.Corner);
        Assert.Equal(12, loaded.Value.OffsetXDip);
        Assert.Equal(34, loaded.Value.OffsetYDip);
    }

    /// <summary>A laptop's external monitor coming and going must not disturb the laptop panel's own remembered place.</summary>
    [Fact]
    public void EachDisplayKeepsItsOwnEntry()
    {
        var file = new PanelPositionFile(Path);
        file.Save("laptop-panel", new PanelPositionFile.Place("TopLeft", 1, 2));
        file.Save("external-monitor", new PanelPositionFile.Place("BottomRight", 3, 4));

        file.Save("external-monitor", new PanelPositionFile.Place("TopRight", 5, 6));   // monitor re-dragged

        Assert.Equal("TopLeft", file.Read("laptop-panel")!.Value.Corner);
        Assert.Equal("TopRight", file.Read("external-monitor")!.Value.Corner);
    }

    [Fact]
    public void AnAbsentFile_ReadsAsNothingStored()
    {
        Assert.Null(new PanelPositionFile(Path).Read("display-1"));
    }

    [Fact]
    public void AMalformedFile_ReadsAsNothingStored()
    {
        File.WriteAllText(Path, "{not json");

        Assert.Null(new PanelPositionFile(Path).Read("display-1"));
    }

    /// <summary>A display nothing was ever saved for is not the same bug as a missing file — both read as null.</summary>
    [Fact]
    public void ADisplayNeverSavedTo_ReadsAsNothingStored()
    {
        var file = new PanelPositionFile(Path);
        file.Save("display-1", new PanelPositionFile.Place("TopLeft", 0, 0));

        Assert.Null(file.Read("display-2"));
    }

    [Fact]
    public void SavingDoesNotLeaveATempFileBehind()
    {
        var file = new PanelPositionFile(Path);
        file.Save("display-1", new PanelPositionFile.Place("TopLeft", 0, 0));

        Assert.False(File.Exists(Path + ".tmp"));
        Assert.True(File.Exists(Path));
    }

    [Fact]
    public void ForgettingADisplay_RemovesOnlyItsEntry()
    {
        var file = new PanelPositionFile(Path);
        file.Save("laptop-panel", new PanelPositionFile.Place("TopLeft", 1, 2));
        file.Save("external-monitor", new PanelPositionFile.Place("BottomRight", 3, 4));

        Assert.True(file.Forget("laptop-panel"));

        Assert.Null(file.Read("laptop-panel"));
        Assert.Equal(new PanelPositionFile.Place("BottomRight", 3, 4), file.Read("external-monitor"));
    }

    /// <summary>Forgetting a display that was never saved is the state the caller wanted, not a failure.</summary>
    [Fact]
    public void ForgettingADisplayNeverSavedTo_Succeeds()
    {
        var file = new PanelPositionFile(Path);

        Assert.True(file.Forget("display-1"));
        Assert.Null(file.Read("display-1"));
        Assert.False(File.Exists(Path));
    }
}
