using OView.Core.Models;

namespace OView.Core.Tests;

public class UsageLevelTests
{
    [Theory]
    [InlineData(0, UsageLevel.Normal)]
    [InlineData(49, UsageLevel.Normal)]
    [InlineData(50, UsageLevel.Warning)]   // band boundary (issue #2)
    [InlineData(69, UsageLevel.Warning)]
    [InlineData(70, UsageLevel.Critical)]  // band boundary — also the notify default
    [InlineData(100, UsageLevel.Critical)]
    public void Classify_MapsToIssue2Bands(int percent, UsageLevel expected)
    {
        Assert.Equal(expected, UsageLevels.Classify(percent));
    }
}
