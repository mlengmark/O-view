using System.Windows;
using System.Windows.Controls;

namespace OView.Tray.Popup;

/// <summary>Small text-placement helpers shared by more than one panel piece.</summary>
internal static class PopupText
{
    /// <summary>
    /// Sets a line's text and hides it when there is none. The empty string is the signal,
    /// every text member that can have nothing to say returns one, so the decision
    /// lives with the text rather than being re-derived from the statistics at each call site.
    /// A visible but empty <see cref="TextBlock"/> leaves a gap that reads as a figure which
    /// failed to load.
    /// </summary>
    public static void SetLine(TextBlock line, string text)
    {
        line.Text = text;
        line.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
