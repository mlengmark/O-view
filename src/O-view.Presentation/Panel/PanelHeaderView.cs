namespace OView.Presentation.Panel;

/// <summary>
/// The panel header, as a head has to place it: the app name, the freshness line beneath it,
/// and the account block on the right.
///
/// <para>Everything here is already decided. <see cref="Email"/> is null rather than empty
/// when there is none, because "hide the row" and "draw an empty row" are different layouts
/// and the choice between them was the kind of thing each head used to make for itself,
/// <see cref="AccountName"/> and <see cref="Tier"/> are never null for the same reason, since
/// their absence is a stand-in sentence rather than a missing row.</para>
/// </summary>
public sealed record PanelHeaderView(
    string Title,
    string Freshness,
    string AccountName,
    string? Email,
    string Tier)
{
    /// <summary>
    /// The app's own name, above the freshness line, and the one place it is spelled: the
    /// Windows panel and menu bind their markup to it, and the Linux window title, tray
    /// tooltip and notifications read it too.
    /// </summary>
    public const string AppName = "O-view";

    /// <summary>
    /// What the header is, said out loud: a screen reader reaching it announces "Move panel"
    /// rather than reading the app name and the account block and leaving the one thing the band
    /// does unsaid.
    ///
    /// <para>Here rather than in either head for the plain reason all the panel's wording is:
    /// two heads authoring it separately is two spoken names for one band, and the drag and the
    /// keyboard nudge are the same affordance on both. It is a name, not a sentence, the words
    /// a reader puts in front of its own "grouping"/"pane" role noun.</para>
    /// </summary>
    public const string MoveName = "Move panel";
}
