namespace OView.Presentation.Text;

/// <summary>
/// The weekly-reset entry dialog's words (issue #186).
///
/// <para>Only Windows has this dialog today, so nothing here is duplicated yet. It is here
/// because of what the copy <i>is</i>: every line exists to keep an entered time from being
/// confused with a derived one, which is the job
/// <see cref="PanelText.WeeklyResetUserSupplied"/> and
/// <see cref="PanelText.WeeklyResetConflict"/> do on the panel this entry feeds. Those three
/// have to agree, and they cannot agree from two projects.</para>
/// </summary>
public static class WeeklyResetText
{
    public const string Title = "Weekly reset time";

    /// <summary>
    /// Names the source rather than describing the field. Anthropic assigns this time per
    /// account and shows it in Claude, so the user is copying a fact rather than estimating
    /// one, which is the whole reason an entry outranks what O-view derives.
    /// </summary>
    public const string Source =
        "Anthropic assigns each account a fixed weekly reset. Find yours in Claude → Settings "
        + "→ Usage.";

    public const string DayLabel = "Day";

    public const string TimeLabel = "Time";

    /// <summary>
    /// What the field wants, and what happens if it is left alone. Nothing is pre-filled from
    /// the derived value, a guess the user confirms becomes a fact, which is worse than one
    /// labelled uncertain, so this is the only thing telling a first-time reader that an
    /// empty field is a valid answer.
    /// </summary>
    public const string FirstEntryHint =
        "Local time, 24-hour, e.g. 22:59. Left empty, O-view keeps deriving the reset by "
        + "watching your weekly percentage fall.";

    /// <summary>The same once something is entered, where the route back matters more than
    /// the example.</summary>
    public const string ExistingEntryHint =
        "Local time, 24-hour. Clearing this returns to the derived reset.";

    /// <summary>
    /// Quotes what was typed and gives the accepted shape beside the rejected one: "enter a
    /// valid time" would leave a reader who typed <c>10:59 PM</c> with no idea which half was
    /// wrong.
    /// </summary>
    public static string TimeError(string entered) =>
        $"'{entered}' is not a 24-hour time. Enter it as HH:mm, 22:59, not 10:59 PM.";

    /// <summary>Goes back to deriving. Distinct from <see cref="CancelButton"/>.</summary>
    public const string ClearButton = "Clear";

    public const string CancelButton = "Cancel";

    public const string SaveButton = "Save";
}
