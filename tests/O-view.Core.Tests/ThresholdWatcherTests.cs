using OView.Core.Models;

namespace OView.Core.Tests;

public class ThresholdWatcherTests
{
    [Fact]
    public void Crossing_NotifiesExactlyOnce()
    {
        var w = new ThresholdWatcher(85);

        Assert.False(w.ShouldNotify(60));
        Assert.True(w.ShouldNotify(87));   // crossing
        Assert.False(w.ShouldNotify(90));  // still above — no repeat
        Assert.False(w.ShouldNotify(99));
    }

    [Fact]
    public void ExactThreshold_Counts()
    {
        var w = new ThresholdWatcher(85);

        Assert.True(w.ShouldNotify(85));
    }

    [Fact]
    public void DroppingBelow_ReArms()
    {
        var w = new ThresholdWatcher(85);

        Assert.True(w.ShouldNotify(90));
        Assert.False(w.ShouldNotify(2));   // window reset
        Assert.True(w.ShouldNotify(86));   // new crossing in the new window
    }

    [Fact]
    public void UnknownData_ReArms_AndNeverNotifies()
    {
        var w = new ThresholdWatcher(85);

        Assert.True(w.ShouldNotify(90));
        Assert.False(w.ShouldNotify(null));  // data lost — no notification, re-armed
        Assert.True(w.ShouldNotify(91));     // data back above — this IS new information
    }

    [Fact]
    public void StartingAboveThreshold_NotifiesOnFirstSample()
    {
        var w = new ThresholdWatcher(85);

        Assert.True(w.ShouldNotify(92));   // app launched mid-heavy-session
    }
}
