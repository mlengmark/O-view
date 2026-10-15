using System.Text.RegularExpressions;
using OView.Presentation.Samples;

namespace OView.Presentation.Tests;

/// <summary>
/// The sample states are what every golden image is rendered from, so a state that quietly
/// depended on the real clock, or contradicted itself, would poison every comparison.
/// </summary>
public class SampleStatesTests
{
    [Fact]
    public void NamesAreUniqueKebabCase_BecauseTheyBecomeGoldenFileNames()
    {
        var names = SampleStates.All.Select(s => s.Name).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(names, n => Assert.Matches(new Regex("^[a-z0-9]+(-[a-z0-9]+)*$"), n));
    }

    [Fact]
    public void EveryStateSaysWhatItShows()
    {
        Assert.All(SampleStates.All, s => Assert.False(string.IsNullOrWhiteSpace(s.Shows)));
    }

    [Fact]
    public void NoStateReadsTheRealClock()
    {
        // Every capture time is the fixed render instant (or absent), so a render made today
        // and one made next year draw the same pixels.
        Assert.All(SampleStates.All, s =>
        {
            if (s.Snapshot.CapturedAtUtcValue.Value is { } captured)
            {
                Assert.Equal(SampleStates.Now, captured);
            }
        });
    }

    [Fact]
    public void RecordedCompositionsAgreeWithTheTokenTiles()
    {
        // The tiles headline output; a composition whose Output disagreed would render a
        // panel contradicting itself (issue #253).
        foreach (var s in SampleStates.All)
        {
            if (s.Stats.CompositionToday.Output > 0)
            {
                Assert.Equal(s.Stats.TokensToday, s.Stats.CompositionToday.Output);
            }
            if (s.Stats.Composition31Days.Output > 0)
            {
                Assert.Equal(s.Stats.Tokens31Days, s.Stats.Composition31Days.Output);
            }
        }
    }

    [Fact]
    public void RendersInUtc_SoCiAndADesktopAgree()
    {
        Assert.Equal(TimeZoneInfo.Utc, SampleStates.Zone);
    }
}
