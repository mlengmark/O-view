namespace OView.Presentation.Panel;

/// <summary>
/// The panel header, as a head has to place it: the app name, the freshness line beneath it,
/// and the account block on the right.
///
/// <para>Everything here is already decided. <see cref="Email"/> is null rather than empty
/// when there is none, because "hide the row" and "draw an empty row" are different layouts
/// and the choice between them was the kind of thing each head used to make for itself —
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
    /// The app's own name, above the freshness line.
    ///
    /// <para>The Windows panel also carries it as a literal in <c>PopupWindow.xaml</c>, which
    /// this does not replace — binding the markup to it is a change to a file this did not
    /// touch. It is here so the Linux head stops authoring its own copy, and so the markup has
    /// something to bind to when a slice next opens that file.</para>
    /// </summary>
    public const string AppName = "O-view";
}
