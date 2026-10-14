using OView.Presentation.Text;

namespace OView.Presentation.Tests;

/// <summary>
/// The weekly-reset entry dialog's words (issue #186), asserted character for character as
/// the Windows dialog produced them before the extraction.
/// </summary>
public class WeeklyResetTextTests
{
    [Fact]
    public void TheHeadingAndFieldLabelsReadAsTheyDid()
    {
        Assert.Equal("Weekly reset time", WeeklyResetText.Title);
        Assert.Equal("Day", WeeklyResetText.DayLabel);
        Assert.Equal("Time", WeeklyResetText.TimeLabel);
    }

    [Fact]
    public void TheButtonsReadAsTheyDid()
    {
        Assert.Equal("Clear", WeeklyResetText.ClearButton);
        Assert.Equal("Cancel", WeeklyResetText.CancelButton);
        Assert.Equal("Save", WeeklyResetText.SaveButton);
    }

    /// <summary>
    /// The line under the heading names where the time comes from. That is the whole reason an
    /// entry outranks what O-view derives — the user is copying a fact Anthropic assigned,
    /// not estimating one — so it has to send them somewhere specific rather than describe
    /// the field.
    /// </summary>
    [Fact]
    public void TheSourceLineSendsTheReaderToClaudesOwnSettings()
    {
        Assert.Equal(
            "Anthropic assigns each account a fixed weekly reset. Find yours in Claude → "
            + "Settings → Usage.",
            WeeklyResetText.Source);
    }

    /// <summary>
    /// Nothing is pre-filled from the derived value — a guess the user confirms becomes a
    /// fact, which is strictly worse than one the panel labels approximate. So the first-entry
    /// hint is the only thing telling a reader that leaving the field alone is a valid answer,
    /// and it has to say what happens if they do.
    /// </summary>
    [Fact]
    public void TheFirstEntryHintSaysWhatAnEmptyFieldMeans()
    {
        Assert.Equal(
            "Local time, 24-hour, e.g. 22:59. Left empty, O-view keeps deriving the reset by "
            + "watching your weekly percentage fall.",
            WeeklyResetText.FirstEntryHint);
    }

    /// <summary>
    /// Once something is entered the example matters less than the route back, which is the
    /// one thing the dialog offers that cancelling does not.
    /// </summary>
    [Fact]
    public void TheExistingEntryHintNamesTheRouteBackToDeriving()
    {
        Assert.Equal(
            "Local time, 24-hour. Clearing this returns to the derived reset.",
            WeeklyResetText.ExistingEntryHint);
    }

    /// <summary>
    /// The error quotes what was typed and gives both the accepted shape and the rejected
    /// one. "Enter a valid time" would leave someone who typed <c>10:59 PM</c> with no idea
    /// which half of it was wrong.
    /// </summary>
    [Fact]
    public void TheTimeErrorQuotesTheEntryAndNamesBothShapes()
    {
        Assert.Equal(
            "'10:59 PM' is not a 24-hour time. Enter it as HH:mm — 22:59, not 10:59 PM.",
            WeeklyResetText.TimeError("10:59 PM"));

        Assert.Contains("'25:00'", WeeklyResetText.TimeError("25:00"), StringComparison.Ordinal);
    }
}
